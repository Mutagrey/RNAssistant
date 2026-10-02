using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;

namespace RNAssistant.Core.Storage
{
    public sealed partial class ChatStore
    {
        private const int SqliteProjectionVersion = 2;
        private static readonly object SqliteProviderSync = new object();
        private static bool _sqliteProviderReady;
        private long _sqliteProjectionFullBuildCount;
        private long _sqliteProjectionSuffixCount;
        private long _sqliteProjectionHitCount;

        internal long SqliteProjectionFullBuildCount { get { return Interlocked.Read(ref _sqliteProjectionFullBuildCount); } }
        internal long SqliteProjectionSuffixCount { get { return Interlocked.Read(ref _sqliteProjectionSuffixCount); } }
        internal long SqliteProjectionHitCount { get { return Interlocked.Read(ref _sqliteProjectionHitCount); } }

        private sealed class SqliteCursor
        {
            public string SessionId;
            public long Sequence;
            public string HeadHash;
            public long ByteLength;
            public long NextOffset;
            public long TailOffset;
            public long LastWriteUtcTicks;
            public string ProtectionKeyId;
            public bool IncompleteTail;
            public long MessageCount;
            public long ArtifactCount;
            public JObject Root;
            public ChatHeaderReducer Header;
        }

        private sealed class ProjectionRowException : Exception
        {
            public ProjectionRowException(Exception inner) : base("The disposable chat index has an invalid row.", inner) { }
        }

        private static void InitializeSqliteProvider()
        {
            if (_sqliteProviderReady) return;
            lock (SqliteProviderSync)
            {
                if (_sqliteProviderReady) return;
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                    SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
                else
                    SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
                _sqliteProviderReady = true;
            }
        }

        private static SqliteConnection OpenProjectionDatabase(string eventPath)
        {
            try { return OpenProjectionDatabaseCore(eventPath); }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 11 || ex.SqliteErrorCode == 26)
            {
                DeleteSqliteProjection(eventPath);
                return OpenProjectionDatabaseCore(eventPath);
            }
        }

        private static SqliteConnection OpenProjectionDatabaseCore(string eventPath)
        {
            InitializeSqliteProvider();
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = eventPath + ".projection.sqlite",
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            };
            var connection = new SqliteConnection(builder.ToString());
            try
            {
                connection.Open();
                ExecuteSql(connection, null, "PRAGMA busy_timeout=5000");
                ExecuteSql(connection, null, "PRAGMA synchronous=NORMAL");
                if (Convert.ToInt32(Scalar(connection, null, "PRAGMA user_version")) != SqliteProjectionVersion)
                {
                    using (var transaction = connection.BeginTransaction())
                    {
                        if (Convert.ToInt32(Scalar(connection, transaction, "PRAGMA user_version")) != SqliteProjectionVersion)
                        {
                            ExecuteSql(connection, transaction, "DROP TABLE IF EXISTS messages");
                            ExecuteSql(connection, transaction, "DROP TABLE IF EXISTS artifacts");
                            ExecuteSql(connection, transaction, "DROP TABLE IF EXISTS projection");
                            CreateSqliteSchema(connection, transaction);
                            ExecuteSql(connection, transaction, "PRAGMA user_version=" + SqliteProjectionVersion);
                        }
                        transaction.Commit();
                    }
                }
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        private static void CreateSqliteSchema(SqliteConnection connection, SqliteTransaction transaction)
        {
            ExecuteSql(connection, transaction, "CREATE TABLE IF NOT EXISTS projection (id INTEGER PRIMARY KEY CHECK(id=1), " +
                "version INTEGER NOT NULL, session_id TEXT NOT NULL, sequence INTEGER NOT NULL, head_hash TEXT NOT NULL, " +
                "byte_length INTEGER NOT NULL, next_offset INTEGER NOT NULL, tail_offset INTEGER NOT NULL, " +
                "mtime_ticks INTEGER NOT NULL, protection_key_id TEXT, incomplete_tail INTEGER NOT NULL, " +
                "message_count INTEGER NOT NULL, artifact_count INTEGER NOT NULL, root BLOB NOT NULL, header BLOB NOT NULL)");
            ExecuteSql(connection, transaction, "CREATE TABLE IF NOT EXISTS messages (ordinal INTEGER PRIMARY KEY, " +
                "item_id TEXT NOT NULL UNIQUE COLLATE NOCASE, payload BLOB NOT NULL)");
            ExecuteSql(connection, transaction, "CREATE TABLE IF NOT EXISTS artifacts (ordinal INTEGER PRIMARY KEY, " +
                "item_id TEXT NOT NULL UNIQUE COLLATE NOCASE, payload BLOB NOT NULL)");
        }

        private static void DeleteSqliteProjection(string eventPath)
        {
            var path = eventPath + ".projection.sqlite";
            foreach (var suffix in new[] { "-journal", "-wal", "-shm", string.Empty })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }

        private static void MoveSqliteProjection(string oldEventPath, string newEventPath)
        {
            var oldPath = oldEventPath + ".projection.sqlite";
            if (!File.Exists(oldPath)) return;
            var newPath = newEventPath + ".projection.sqlite";
            try
            {
                if (File.Exists(oldPath + "-journal"))
                {
                    DeleteSqliteProjection(oldEventPath);
                    return;
                }
                DeleteSqliteProjection(newEventPath);
                File.Move(oldPath, newPath);
            }
            catch (IOException) { /* The destination will rebuild from its moved event log. */ }
            catch (UnauthorizedAccessException) { /* The destination will rebuild from its moved event log. */ }
        }

        private static void ExecuteSql(SqliteConnection connection, SqliteTransaction transaction, string sql,
            params object[] values)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                for (var i = 0; i < values.Length; i++)
                    command.Parameters.AddWithValue("$p" + i, values[i] ?? DBNull.Value);
                command.ExecuteNonQuery();
            }
        }

        private static object Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql,
            params object[] values)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                for (var i = 0; i < values.Length; i++)
                    command.Parameters.AddWithValue("$p" + i, values[i] ?? DBNull.Value);
                return command.ExecuteScalar();
            }
        }

        private byte[] SealProjection(string sessionId, string component, string json)
        {
            return Protection().Protect(Utf8.GetBytes(json ?? string.Empty),
                "rnassistant/chat-projection/v1/" + sessionId + "/" + component);
        }

        private string OpenProjection(string sessionId, string component, byte[] payload)
        {
            return Utf8.GetString(Protection().Unprotect(payload,
                "rnassistant/chat-projection/v1/" + sessionId + "/" + component));
        }

        private SqliteCursor ReadSqliteCursor(SqliteConnection connection, SqliteTransaction transaction = null)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT version,session_id,sequence,head_hash,byte_length,next_offset," +
                    "tail_offset,mtime_ticks,protection_key_id,incomplete_tail,message_count,artifact_count,root,header," +
                    "(SELECT COUNT(*) FROM messages),(SELECT COUNT(*) FROM artifacts) " +
                    "FROM projection WHERE id=1";
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) return null;
                    if (reader.GetInt32(0) != SqliteProjectionVersion) return null;
                    var sessionId = reader.GetString(1);
                    var keyId = reader.IsDBNull(8) ? null : reader.GetString(8);
                    if (!string.Equals(keyId ?? string.Empty, Protection().KeyId ?? string.Empty,
                        StringComparison.OrdinalIgnoreCase)) return null;
                    var messageCount = reader.GetInt64(10);
                    var artifactCount = reader.GetInt64(11);
                    if (messageCount != reader.GetInt64(14) || artifactCount != reader.GetInt64(15))
                        return null;
                    var root = JObject.Parse(OpenProjection(sessionId, "root", (byte[])reader[12]));
                    var header = ChatHeaderReducer.FromCheckpoint(_blobs,
                        JObject.Parse(OpenProjection(sessionId, "header", (byte[])reader[13])));
                    if ((int?)root["FormatVersion"] != ChatSession.CurrentFormatVersion ||
                        !string.Equals((string)root["Id"], sessionId, StringComparison.OrdinalIgnoreCase) ||
                        !header.IsValid) return null;
                    return new SqliteCursor
                    {
                        SessionId = sessionId,
                        Sequence = reader.GetInt64(2),
                        HeadHash = reader.GetString(3),
                        ByteLength = reader.GetInt64(4),
                        NextOffset = reader.GetInt64(5),
                        TailOffset = reader.GetInt64(6),
                        LastWriteUtcTicks = reader.GetInt64(7),
                        ProtectionKeyId = keyId,
                        IncompleteTail = reader.GetInt32(9) != 0,
                        MessageCount = messageCount,
                        ArtifactCount = artifactCount,
                        Root = root,
                        Header = header
                    };
                }
            }
        }

        private static bool SqliteCursorMatches(SqliteConnection connection, SqliteTransaction transaction,
            SqliteCursor cursor)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT sequence,head_hash,byte_length FROM projection WHERE id=1";
                using (var reader = command.ExecuteReader())
                    return reader.Read() && reader.GetInt64(0) == cursor.Sequence &&
                        string.Equals(reader.GetString(1), cursor.HeadHash, StringComparison.OrdinalIgnoreCase) &&
                        reader.GetInt64(2) == cursor.ByteLength;
            }
        }

        private SqliteCursor EnsureSqliteProjection(SqliteConnection connection, string path)
        {
            var current = CaptureStorageFileState(path);
            if (current == null) return null;
            SqliteCursor saved;
            try { saved = ReadSqliteCursor(connection); }
            catch (Exception ex) when (ex is JsonException || ex is CryptographicException || ex is SqliteException)
            {
                saved = null;
            }
            var append = saved != null && !saved.IncompleteTail &&
                current.ByteLength >= saved.ByteLength &&
                ReadValidatedEventAtOffset(path, saved.SessionId, saved.Sequence, saved.HeadHash,
                    saved.TailOffset, saved.NextOffset, current.ByteLength) != null;
            if (append && current.ByteLength == saved.ByteLength &&
                current.LastWriteUtcTicks == saved.LastWriteUtcTicks)
            {
                Interlocked.Increment(ref _sqliteProjectionHitCount);
                return saved;
            }
            if (append && current.ByteLength == saved.ByteLength) append = false;
            return MaterializeSqliteProjection(connection, path, append ? saved : null);
        }

        private SqliteCursor MaterializeSqliteProjection(
            SqliteConnection connection, string path, SqliteCursor previous)
        {
            var timer = Stopwatch.StartNew();
            var before = CaptureStorageFileState(path);
            if (before == null) return null;
            using (var transaction = connection.BeginTransaction())
            {
                if (previous == null)
                {
                    ExecuteSql(connection, transaction, "DELETE FROM messages");
                    ExecuteSql(connection, transaction, "DELETE FROM artifacts");
                    ExecuteSql(connection, transaction, "DELETE FROM projection");
                }
                var root = previous == null ? null : previous.Root;
                var header = previous == null ? new ChatHeaderReducer(_blobs) : previous.Header;
                var last = previous == null ? null : new SessionEvent
                {
                    SessionId = previous.SessionId,
                    Sequence = previous.Sequence,
                    Hash = previous.HeadHash,
                    StorageByteOffset = previous.TailOffset
                };
                var startOffset = previous == null ? 0 : previous.NextOffset;
                var summary = JsonlRecordReader.Read(path, startOffset, ParseSessionEvent, (item, line) =>
                {
                    ValidateEvent(last, item, Protection());
                    item.StorageByteOffset = line.Offset;
                    HydrateEventData(item, Protection());
                    header.Apply(item);
                    if (IsProjectionEvent(item)) ApplySqliteEvent(connection, transaction, ref root, item);
                    last = item;
                });
                if (last == null || root == null || !header.IsValid)
                    throw new ChatConcurrencyException("The chat event log cannot be indexed.");
                var after = CaptureStorageFileState(path);
                if (after == null || before.ByteLength != after.ByteLength ||
                    before.LastWriteUtcTicks != after.LastWriteUtcTicks ||
                    summary.ByteLength != before.ByteLength)
                    throw new ChatConcurrencyException("Chat storage changed during indexing.");
                var cursor = new SqliteCursor
                {
                    SessionId = last.SessionId,
                    Sequence = last.Sequence,
                    HeadHash = last.Hash,
                    ByteLength = summary.ByteLength,
                    NextOffset = summary.TailNextByteOffset,
                    TailOffset = last.StorageByteOffset,
                    LastWriteUtcTicks = after.LastWriteUtcTicks,
                    ProtectionKeyId = Protection().KeyId,
                    IncompleteTail = summary.HasIncompleteTail,
                    MessageCount = Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM messages")),
                    ArtifactCount = Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM artifacts")),
                    Root = root,
                    Header = header
                };
                StoreSqliteCursor(connection, transaction, cursor);
                transaction.Commit();
                if (previous == null) Interlocked.Increment(ref _sqliteProjectionFullBuildCount);
                else Interlocked.Increment(ref _sqliteProjectionSuffixCount);
                LogPerformance(previous == null ? "projectionIndexBuild" : "projectionIndexSuffix",
                    timer.ElapsedMilliseconds, "bytes=" + (summary.TailNextByteOffset - startOffset));
                return cursor;
            }
        }

        private void StoreSqliteCursor(SqliteConnection connection, SqliteTransaction transaction, SqliteCursor cursor)
        {
            ExecuteSql(connection, transaction, "INSERT OR REPLACE INTO projection " +
                "(id,version,session_id,sequence,head_hash,byte_length,next_offset,tail_offset," +
                "mtime_ticks,protection_key_id,incomplete_tail,message_count,artifact_count,root,header) " +
                "VALUES (1,$p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10,$p11,$p12,$p13)",
                SqliteProjectionVersion, cursor.SessionId, cursor.Sequence, cursor.HeadHash,
                cursor.ByteLength, cursor.NextOffset, cursor.TailOffset, cursor.LastWriteUtcTicks,
                cursor.ProtectionKeyId, cursor.IncompleteTail ? 1 : 0, cursor.MessageCount, cursor.ArtifactCount,
                SealProjection(cursor.SessionId, "root", cursor.Root.ToString(Formatting.None)),
                SealProjection(cursor.SessionId, "header", cursor.Header.ToCheckpoint().ToString(Formatting.None)));
        }

        private void ApplySqliteEvent(SqliteConnection connection, SqliteTransaction transaction,
            ref JObject root, SessionEvent item)
        {
            if (string.Equals(item.Type, SessionEventTypes.SessionCreated, StringComparison.Ordinal) ||
                string.Equals(item.Type, SessionEventTypes.SessionForked, StringComparison.Ordinal))
            {
                if (root != null || !(item.Data is JObject))
                    throw new JsonException("Chat projection has an invalid seed.");
                root = (JObject)item.Data.DeepClone();
                if ((int?)root["FormatVersion"] != ChatSession.CurrentFormatVersion)
                    throw new JsonException("Chat projection format is unsupported.");
                SeedSqliteList(connection, transaction, "messages", item.SessionId, root["Messages"] as JArray);
                SeedSqliteList(connection, transaction, "artifacts", item.SessionId, root["Artifacts"] as JArray);
                root.Remove("Messages");
                root.Remove("Artifacts");
                return;
            }
            if (root == null || !(item.Data is JObject))
                throw new JsonException("Chat projection commit has no seed.");
            var operations = item.Data["Operations"] as JArray;
            if (operations == null) throw new JsonException("Chat projection operations are missing.");
            foreach (var raw in operations)
            {
                var token = raw as JObject;
                if (token == null) throw new JsonException("Chat projection operation must be an object.");
                var type = (string)token["Type"];
                var data = token["Data"] as JObject ?? new JObject();
                switch (type)
                {
                    case SessionOperationTypes.SessionMetadataSet:
                        if (data.Property("DocumentKey") != null)
                            RecordPreviousDocumentKey(root, (string)root["DocumentKey"], (string)data["DocumentKey"]);
                        foreach (var property in data.Properties()) root[property.Name] = property.Value.DeepClone();
                        break;
                    case SessionOperationTypes.ActiveReferencesSet:
                        foreach (var property in data.Properties()) root[property.Name] = property.Value.DeepClone();
                        break;
                    case SessionOperationTypes.ContextSet:
                        root["Context"] = CloneValue(data["Value"]);
                        break;
                    case SessionOperationTypes.RunStarted:
                    case SessionOperationTypes.RunUpdated:
                    case SessionOperationTypes.RunEnded:
                        root["LastRun"] = CloneValue(data["Value"]);
                        break;
                    case SessionOperationTypes.MessageUpdated:
                    case SessionOperationTypes.UserMessageAppended:
                    case SessionOperationTypes.AssistantMessageAppended:
                    case SessionOperationTypes.ToolCallRecorded:
                    case SessionOperationTypes.ToolResultRecorded:
                    case SessionOperationTypes.ToolExecutionStarted:
                    case SessionOperationTypes.ToolExecutionFinished:
                        UpsertSqliteItem(connection, transaction, "messages", item.SessionId, data["Value"] as JObject);
                        break;
                    case SessionOperationTypes.ArtifactRevisionCreated:
                        UpsertSqliteItem(connection, transaction, "artifacts", item.SessionId, data["Value"] as JObject);
                        break;
                    case SessionOperationTypes.MessageRemove:
                        RemoveSqliteItem(connection, transaction, "messages", (string)data["Id"]);
                        break;
                    case SessionOperationTypes.ArtifactRemove:
                        RemoveSqliteItem(connection, transaction, "artifacts", (string)data["Id"]);
                        break;
                    case SessionOperationTypes.MessagesReorder:
                        ReorderSqliteList(connection, transaction, "messages", item.SessionId, data["Ids"] as JArray);
                        break;
                    case SessionOperationTypes.ArtifactsReorder:
                        ReorderSqliteList(connection, transaction, "artifacts", item.SessionId, data["Ids"] as JArray);
                        break;
                    default:
                        throw new JsonException("Unsupported chat projection operation: " + type);
                }
            }
        }

        private void SeedSqliteList(SqliteConnection connection, SqliteTransaction transaction,
            string table, string sessionId, JArray items)
        {
            var ordinal = 0;
            foreach (var raw in items ?? new JArray())
            {
                var value = raw as JObject;
                if (value == null) throw new JsonException("Indexed chat item must be an object.");
                InsertSqliteItem(connection, transaction, table, sessionId, ordinal++, value);
            }
        }

        private void InsertSqliteItem(SqliteConnection connection, SqliteTransaction transaction,
            string table, string sessionId, long ordinal, JObject value)
        {
            var id = (string)value?["Id"];
            if (string.IsNullOrWhiteSpace(id)) throw new JsonException("Indexed chat item has no id.");
            ExecuteSql(connection, transaction, "INSERT INTO " + table +
                " (ordinal,item_id,payload) VALUES ($p0,$p1,$p2)", ordinal, id,
                SealProjection(sessionId, table + "/" + id, value.ToString(Formatting.None)));
        }

        private void UpsertSqliteItem(SqliteConnection connection, SqliteTransaction transaction,
            string table, string sessionId, JObject value)
        {
            var id = (string)value?["Id"];
            if (string.IsNullOrWhiteSpace(id)) throw new JsonException("Indexed chat upsert has no id.");
            var payload = SealProjection(sessionId, table + "/" + id, value.ToString(Formatting.None));
            var ordinal = Scalar(connection, transaction,
                "SELECT ordinal FROM " + table + " WHERE item_id=$p0 COLLATE NOCASE", id);
            if (ordinal != null && ordinal != DBNull.Value)
                ExecuteSql(connection, transaction, "UPDATE " + table + " SET payload=$p0 WHERE item_id=$p1 COLLATE NOCASE",
                    payload, id);
            else
                InsertSqliteItem(connection, transaction, table, sessionId,
                    Convert.ToInt64(Scalar(connection, transaction,
                        "SELECT COALESCE(MAX(ordinal),-1)+1 FROM " + table)), value);
        }

        private static void RemoveSqliteItem(SqliteConnection connection, SqliteTransaction transaction,
            string table, string id)
        {
            if (!string.IsNullOrWhiteSpace(id))
                ExecuteSql(connection, transaction, "DELETE FROM " + table + " WHERE item_id=$p0 COLLATE NOCASE", id);
        }

        private void ReorderSqliteList(SqliteConnection connection, SqliteTransaction transaction,
            string table, string sessionId, JArray ids)
        {
            var existing = ReadSqliteList(connection, transaction, table, sessionId);
            var byId = existing.OfType<JObject>().ToDictionary(value => (string)value["Id"],
                value => value, StringComparer.OrdinalIgnoreCase);
            var ordered = new List<JObject>();
            foreach (var id in (ids ?? new JArray()).Values<string>())
            {
                JObject value;
                if (!string.IsNullOrWhiteSpace(id) && byId.TryGetValue(id, out value))
                {
                    ordered.Add(value);
                    byId.Remove(id);
                }
            }
            ordered.AddRange(existing.OfType<JObject>().Where(value => byId.ContainsKey((string)value["Id"])));
            ExecuteSql(connection, transaction, "DELETE FROM " + table);
            SeedSqliteList(connection, transaction, table, sessionId, new JArray(ordered));
        }

        private JArray ReadSqliteList(SqliteConnection connection, SqliteTransaction transaction,
            string table, string sessionId, long? first = null, long? before = null)
        {
            var values = new JArray();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT item_id,payload FROM " + table +
                    " WHERE ($p0 IS NULL OR ordinal >= $p0) AND ($p1 IS NULL OR ordinal < $p1) ORDER BY ordinal";
                command.Parameters.AddWithValue("$p0", first.HasValue ? (object)first.Value : DBNull.Value);
                command.Parameters.AddWithValue("$p1", before.HasValue ? (object)before.Value : DBNull.Value);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var id = reader.GetString(0);
                        values.Add(ReadSqliteItem(sessionId, table, id, (byte[])reader[1]));
                    }
                }
            }
            return values;
        }

        private JArray ReadSqliteMessageWindow(SqliteConnection connection, SqliteTransaction transaction,
            string sessionId, int start, int count)
        {
            var values = new JArray();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT item_id,payload FROM messages ORDER BY ordinal LIMIT $limit OFFSET $start";
                command.Parameters.AddWithValue("$limit", count);
                command.Parameters.AddWithValue("$start", start);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var id = reader.GetString(0);
                        values.Add(ReadSqliteItem(sessionId, "messages", id, (byte[])reader[1]));
                    }
                }
            }
            return values;
        }

        private JObject ReadSqliteItem(string sessionId, string table, string id, byte[] payload)
        {
            try
            {
                var value = JObject.Parse(OpenProjection(sessionId, table + "/" + id, payload));
                if (!string.Equals((string)value["Id"], id, StringComparison.OrdinalIgnoreCase))
                    throw new JsonException("Indexed chat item id does not match its key.");
                return value;
            }
            catch (Exception ex) when (ex is JsonException || ex is CryptographicException)
            {
                throw new ProjectionRowException(ex);
            }
        }

        internal ConversationMessageWindow ReadMessageWindow(string chatId, int beforeIndex, int pageSize)
        {
            if (string.IsNullOrWhiteSpace(chatId)) return null;
            if (pageSize <= 0 || pageSize > 100) throw new ArgumentOutOfRangeException(nameof(pageSize));
            foreach (var path in SafeFindSessionFiles(chatId))
            {
                ConversationMessageWindow page;
                try { page = ReadMessageWindowAtPath(path, chatId, beforeIndex, pageSize); }
                catch (ProjectionRowException)
                {
                    DeleteSqliteProjection(path);
                    page = ReadMessageWindowAtPath(path, chatId, beforeIndex, pageSize);
                }
                if (page != null) return page;
            }
            return null;
        }

        private ConversationMessageWindow ReadMessageWindowAtPath(string path, string chatId,
            int beforeIndex, int pageSize)
        {
            using (var connection = OpenProjectionDatabase(path))
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var cursor = EnsureSqliteProjection(connection, path);
                    if (cursor == null || !string.Equals(cursor.SessionId, chatId, StringComparison.OrdinalIgnoreCase))
                        return null;
                    JObject root;
                    int total;
                    int start;
                    List<string> retainedSourceIds;
                    using (var transaction = connection.BeginTransaction(true))
                    {
                        if (!SqliteCursorMatches(connection, transaction, cursor)) continue;
                        total = Convert.ToInt32(Scalar(connection, transaction, "SELECT COUNT(*) FROM messages"));
                        if (beforeIndex < 0 || beforeIndex > total)
                            throw new ArgumentOutOfRangeException(nameof(beforeIndex));
                        start = Math.Max(0, beforeIndex - pageSize);
                        root = (JObject)cursor.Root.DeepClone();
                        root["Messages"] = ReadSqliteMessageWindow(connection, transaction,
                            cursor.SessionId, start, beforeIndex - start);
                        root["Artifacts"] = ReadSqliteList(connection, transaction, "artifacts", cursor.SessionId);
                        var pageIds = new HashSet<string>(((JArray)root["Messages"]).OfType<JObject>()
                            .Select(value => (string)value["Id"]).Where(id => !string.IsNullOrWhiteSpace(id)),
                            StringComparer.OrdinalIgnoreCase);
                        retainedSourceIds = ((JArray)root["Artifacts"]).OfType<JObject>()
                            .Select(value => (string)value["SourceMessageId"])
                            .Where(id => !string.IsNullOrWhiteSpace(id) && !pageIds.Contains(id))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Where(id => Scalar(connection, transaction,
                                "SELECT 1 FROM messages WHERE item_id=$p0 COLLATE NOCASE", id) != null)
                            .ToList();
                    }
                    var session = Project(root, cursor.Sequence, cursor.HeadHash, cursor.TailOffset,
                        cursor.ByteLength, cursor.LastWriteUtcTicks, false, true);
                    var pageMessages = session.Messages.ToArray();
                    // Artifact tombstones are applicable only while their source message
                    // exists. Keep ID-only context for presentation without reading bodies.
                    foreach (var id in retainedSourceIds)
                        session.Messages.Add(new ChatMessage { Id = id });
                    return new ConversationMessageWindow
                    {
                        Session = session,
                        PageMessages = pageMessages,
                        StartIndex = start,
                        TotalCount = total
                    };
                }
            }
            throw new ChatConcurrencyException("Chat index changed while the message page was being read.");
        }

        internal void SetTitle(ChatSession session, string title)
        {
            CommitScalarMetadata(session, new JObject { ["Title"] = title });
        }

        internal void SetModel(ChatSession session, string model)
        {
            CommitScalarMetadata(session, new JObject
            {
                ["Model"] = model == null ? JValue.CreateNull() : new JValue(model)
            });
        }

        internal void SetReasoningEnabled(ChatSession session, bool enabled)
        {
            CommitScalarMetadata(session, new JObject { ["ReasoningEnabled"] = enabled });
        }

        private void CommitScalarMetadata(ChatSession session, JObject metadata)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            var path = GetSessionPath(session.Host, session.DocumentKey, session.Id);
            using (AcquirePersistenceRead())
            using (AcquireDocumentPathLock(path))
            {
                if (!File.Exists(path))
                    throw new ChatConcurrencyException("The chat event log no longer exists.");
                var previousRevision = session.Revision;
                var previousUpdatedUtc = session.UpdatedUtc;
                session.UpdatedUtc = DateTime.UtcNow;
                metadata["UpdatedUtc"] = session.UpdatedUtc;
                try
                {
                    var operations = new[] { Operation(SessionOperationTypes.SessionMetadataSet, metadata) };
                    var pending = new[] { PendingEvent(SessionEventTypes.SessionCommit,
                        new JObject { ["Operations"] = JArray.FromObject(operations) }, null,
                        CurrentRunId(session), CurrentTurnId(session), null) };
                    var appended = AppendEvents(path, session.Id, previousRevision,
                        session.StorageHeadHash, session.StorageByteLength,
                        session.StorageLastWriteUtcTicks, session.StorageTailByteOffset,
                        pending, null);
                    var tail = appended[appended.Count - 1];
                    session.Revision = tail.Sequence;
                    session.StorageHeadHash = tail.Hash;
                    session.StorageTailByteOffset = tail.StorageByteOffset;
                    CaptureStorageState(session, path);
                    RemoveProjectionCache(path);
                    RemoveHeaderCache(path);
                }
                catch
                {
                    session.UpdatedUtc = previousUpdatedUtc;
                    try
                    {
                        var recovered = ReadEventLog(path);
                        var tail = LastEvent(recovered);
                        if (tail != null)
                        {
                            session.Revision = tail.Sequence;
                            session.StorageHeadHash = tail.Hash;
                            session.StorageTailByteOffset = tail.StorageByteOffset;
                            CaptureStorageState(session, path);
                        }
                    }
                    catch { session.Revision = previousRevision; }
                    throw;
                }
            }
        }

        private ChatSession ReadSqliteSession(string path, bool hydrateActiveArtifacts, bool rebuildDerivedProjections)
        {
            try { return ReadSqliteSessionOnce(path, hydrateActiveArtifacts, rebuildDerivedProjections); }
            catch (ProjectionRowException)
            {
                DeleteSqliteProjection(path);
                return ReadSqliteSessionOnce(path, hydrateActiveArtifacts, rebuildDerivedProjections);
            }
        }

        private ChatSession ReadSqliteSessionOnce(string path, bool hydrateActiveArtifacts, bool rebuildDerivedProjections)
        {
            using (var connection = OpenProjectionDatabase(path))
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var cursor = EnsureSqliteProjection(connection, path);
                    if (cursor == null) return null;
                    JObject root;
                    using (var transaction = connection.BeginTransaction(true))
                    {
                        if (!SqliteCursorMatches(connection, transaction, cursor)) continue;
                        root = (JObject)cursor.Root.DeepClone();
                        root["Messages"] = ReadSqliteList(connection, transaction, "messages", cursor.SessionId);
                        root["Artifacts"] = ReadSqliteList(connection, transaction, "artifacts", cursor.SessionId);
                    }
                    var session = Project(root, cursor.Sequence, cursor.HeadHash, cursor.TailOffset, cursor.ByteLength,
                        cursor.LastWriteUtcTicks, hydrateActiveArtifacts, rebuildDerivedProjections);
                    if (session != null && !cursor.IncompleteTail) StoreProjectionCache(path, root, session);
                    return session;
                }
            }
            throw new ChatConcurrencyException("Chat index changed while the session was being read.");
        }

        // The routine read trusts an exact tail checkpoint. The explicit maintenance audit
        // compares the entire validated event projection with the disposable index.
        private void AuditSqliteProjection(string path, EventLogReadResult log)
        {
            var timer = Stopwatch.StartNew();
            var canonical = ReplayProjectionRoot(log.Events, null);
            var tail = LastEvent(log);
            if (canonical == null || tail == null)
                throw new ChatConcurrencyException("The chat event log cannot be projected.");
            var matches = false;
            try
            {
                using (var connection = OpenProjectionDatabase(path))
                {
                    var cursor = ReadSqliteCursor(connection);
                    if (cursor != null && cursor.Sequence == tail.Sequence &&
                        string.Equals(cursor.HeadHash, tail.Hash, StringComparison.OrdinalIgnoreCase) &&
                        cursor.ByteLength == log.ByteLength)
                    {
                        var indexed = (JObject)cursor.Root.DeepClone();
                        indexed["Messages"] = ReadSqliteList(connection, null, "messages", cursor.SessionId);
                        indexed["Artifacts"] = ReadSqliteList(connection, null, "artifacts", cursor.SessionId);
                        matches = JToken.DeepEquals(canonical, indexed);
                    }
                }
            }
            catch (Exception ex) when (ex is ProjectionRowException || ex is JsonException ||
                ex is CryptographicException || ex is SqliteException)
            {
                // A valid JSONL stream remains authoritative if the secondary index is damaged.
            }
            if (matches) return;
            DeleteSqliteProjection(path);
            using (var connection = OpenProjectionDatabase(path))
                MaterializeSqliteProjection(connection, path, null);
            LogPerformance("projectionIndexAuditRepair", timer.ElapsedMilliseconds,
                "bytes=" + log.ByteLength);
        }

        private ChatSessionHeader ReadSqliteHeader(string path, string host, string documentKey,
            string documentTitle, ChatBlobStore.StorageSizeSnapshot storageSizes)
        {
            using (var connection = OpenProjectionDatabase(path))
            {
                var cursor = EnsureSqliteProjection(connection, path);
                return cursor == null ? null : cursor.Header.CreateHeader(storageSizes, cursor.Sequence,
                    cursor.ByteLength, host, documentKey, documentTitle);
            }
        }

        private void AdvanceSqliteProjection(string path, IReadOnlyList<SessionEvent> appended)
        {
            if (appended == null || appended.Count == 0) return;
            var newChat = !File.Exists(path + ".projection.sqlite") && appended[0].Sequence == 1;
            if (!newChat && !File.Exists(path + ".projection.sqlite")) return;
            try
            {
                using (var connection = OpenProjectionDatabase(path))
                {
                    if (newChat)
                    {
                        MaterializeSqliteProjection(connection, path, null);
                        return;
                    }
                    using (var transaction = connection.BeginTransaction())
                    {
                        var saved = ReadSqliteCursor(connection, transaction);
                        if (saved == null || saved.IncompleteTail ||
                            saved.Sequence != appended[0].Sequence - 1 ||
                            !string.Equals(saved.HeadHash, appended[0].PreviousHash, StringComparison.OrdinalIgnoreCase))
                            return;
                        var root = saved.Root;
                        var header = saved.Header;
                        foreach (var item in appended)
                        {
                            HydrateEventData(item, Protection());
                            header.Apply(item);
                            if (IsProjectionEvent(item)) ApplySqliteEvent(connection, transaction, ref root, item);
                        }
                        var current = CaptureStorageFileState(path);
                        if (current == null) return;
                        var tail = appended[appended.Count - 1];
                        saved.Sequence = tail.Sequence;
                        saved.HeadHash = tail.Hash;
                        saved.ByteLength = current.ByteLength;
                        saved.NextOffset = current.ByteLength;
                        saved.TailOffset = tail.StorageByteOffset;
                        saved.LastWriteUtcTicks = current.LastWriteUtcTicks;
                        saved.MessageCount = Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM messages"));
                        saved.ArtifactCount = Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM artifacts"));
                        saved.Root = root;
                        saved.Header = header;
                        StoreSqliteCursor(connection, transaction, saved);
                        transaction.Commit();
                    }
                }
            }
            catch (Exception ex)
            {
                LogPerformance("projectionIndexLag", 250, ex.GetType().Name);
                // The fsynced event stream is authoritative. The next read catches up.
            }
        }
    }
}
