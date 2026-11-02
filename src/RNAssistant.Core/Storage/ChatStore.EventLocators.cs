using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    public sealed partial class ChatStore
    {
        private long _inputJournalEventReads;
        internal long InputJournalEventReads { get { return Interlocked.Read(ref _inputJournalEventReads); } }
        private long _indexedMaterializationEventReads;
        internal long IndexedMaterializationEventReads { get { return Interlocked.Read(ref _indexedMaterializationEventReads); } }

        // The existing disposable index locates canonical events; it does not own
        // receipts, delivery decisions or a second inbox. All transitions are still
        // validated by ConversationInputJournal against the exact event bodies.
        private sealed class ProjectionEventLocator
        {
            public string Type, Hash;
            public long Sequence, Offset, NextOffset;
        }

        private static bool IsInputJournalEvent(string type)
        {
            return type == SessionEventTypes.ConversationInputChanged || type == SessionEventTypes.SessionCommit;
        }

        private void IndexProjectionEvent(SqliteConnection connection, SqliteTransaction transaction,
            SessionEvent item, long nextOffset, StorageProtector protector)
        {
            if (!IsProjectionEvent(item) && !IsInputJournalEvent(item.Type)) return;
            var locator = new ProjectionEventLocator { Type = item.Type, Hash = item.Hash,
                Sequence = item.Sequence, Offset = item.StorageByteOffset, NextOffset = nextOffset };
            ExecuteSql(connection, transaction, "INSERT INTO event_locators(sequence,type,locator) VALUES($p0,$p1,$p2)",
                item.Sequence, item.Type, SealProjection(item.SessionId, "event/" + item.Sequence,
                    JsonConvert.SerializeObject(locator), protector));
        }

        private IReadOnlyList<SessionEvent> ReadIndexedInputJournal(string path, string sessionId)
        {
            if (!File.Exists(path)) return new SessionEvent[0];
            return ReadSqliteProjection(path, connection =>
            {
                // Header and inbox locators are built together. Reading receipts
                // must not materialize every message or scan model/SSE trace bodies.
                var cursor = EnsureSqliteProjection(connection, path, requireSession: false);
                if (cursor == null || cursor.IncompleteTail || cursor.SessionId != sessionId)
                    throw new ChatConcurrencyException("The chat event log is incomplete or changed and cannot be replayed.");
                var events = new List<SessionEvent>();
                var protector = Protection();
                using (var transaction = connection.BeginTransaction(true))
                using (var reader = new JsonlByteReader(path))
                {
                    if (!SqliteCursorMatches(connection, transaction, cursor))
                        throw new ChatConcurrencyException("Chat index changed while the input journal was being read.");
                    foreach (var locator in ReadProjectionEventLocators(connection, transaction, cursor, protector))
                    {
                        if (!IsInputJournalEvent(locator.Type)) continue;
                        events.Add(ReadLocatedEvent(reader, cursor, locator, protector));
                        Interlocked.Increment(ref _inputJournalEventReads);
                    }
                }
                return events;
            });
        }

        private static List<ProjectionEventLocator> ReadProjectionEventLocators(SqliteConnection connection,
            SqliteTransaction transaction, SqliteCursor cursor, StorageProtector protector)
        {
            var locators = new List<ProjectionEventLocator>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT sequence,type,locator FROM event_locators ORDER BY sequence";
                using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    var sequence = reader.GetInt64(0);
                    ProjectionEventLocator locator;
                    try { locator = JsonConvert.DeserializeObject<ProjectionEventLocator>(OpenProjection(cursor.SessionId,
                        "event/" + sequence, (byte[])reader[2], protector)); }
                    catch (Exception ex) when (IsInvalidProjectionValue(ex)) { throw new ProjectionDataException(ex); }
                    if (locator == null || locator.Sequence != sequence || locator.Type != reader.GetString(1) ||
                        (!IsInputJournalEvent(locator.Type) && !IsProjectionEvent(new SessionEvent { Type = locator.Type })) ||
                        sequence < 1 || sequence > cursor.Sequence || locator.Offset < 0 ||
                        locator.NextOffset <= locator.Offset || locator.NextOffset > cursor.ByteLength)
                        throw new ProjectionDataException(new JsonException("Event locator binding disagrees with its index."));
                    locators.Add(locator);
                }
            }
            return locators;
        }

        private SessionEvent ReadLocatedEvent(JsonlByteReader reader, SqliteCursor cursor, ProjectionEventLocator locator,
            StorageProtector protector)
        {
            var item = ReadValidatedEventAtOffset(reader, protector, cursor.SessionId, locator.Sequence, locator.Hash,
                locator.Offset, locator.NextOffset, cursor.ByteLength);
            if (item == null || item.Type != locator.Type)
                throw new ProjectionDataException(new JsonException("Event locator does not identify a canonical record."));
            HydrateEventData(item, protector);
            return item;
        }

        // The catalog has already validated the entire stream. Materializing its
        // messages uses those exact locators instead of parsing every trace again.
        private SqliteCursor MaterializeIndexedSession(SqliteConnection connection, string path, SqliteCursor cursor)
        {
            var timer = Stopwatch.StartNew();
            var protector = Protection();
            using (var transaction = connection.BeginTransaction())
            using (var reader = new JsonlByteReader(path))
            {
                if (!SqliteCursorMatches(connection, transaction, cursor))
                    throw new ChatConcurrencyException("Chat index changed before session materialization.");
                JObject root = null;
                foreach (var locator in ReadProjectionEventLocators(connection, transaction, cursor, protector))
                {
                    if (locator.Type == SessionEventTypes.ConversationInputChanged) continue;
                    var item = ReadLocatedEvent(reader, cursor, locator, protector);
                    ApplySqliteEvent(connection, transaction, ref root, item, locator.NextOffset, protector);
                    Interlocked.Increment(ref _indexedMaterializationEventReads);
                }
                if (root == null) throw new ProjectionDataException(new JsonException("The chat index has no session seed."));
                cursor.Root = root;
                cursor.Materialized = true;
                cursor.SourceVerified = true;
                cursor.MessageCount = Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM messages"));
                cursor.ArtifactCount = Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM artifacts"));
                StoreSqliteCursor(connection, transaction, cursor, protector);
                transaction.Commit();
            }
            Interlocked.Increment(ref _sqliteProjectionFullBuildCount);
            LogPerformance("projectionIndexMaterialize", timer.ElapsedMilliseconds);
            return cursor;
        }
    }
}
