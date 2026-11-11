using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace RNAssistant.Core.Storage
{
    // The single disposable index owner for the new generation. Domain reducers
    // remain in their concrete journal owners; no pluggable backend or replay DSL.
    internal static partial class JournalIndex
    {
        internal static bool IsDamaged(SqliteException error) { return error.SqliteErrorCode == 11 || error.SqliteErrorCode == 26; }
        internal sealed class IndexVersionMismatch : InvalidOperationException { }

        internal static void Repair(AppDataPaths paths, StorageProtector protector, bool force, CancellationToken token)
        {
            using (RuntimeStorageActivity.Acquire(paths, token))
            using (JournalIndexAdmission.Repair(paths, token))
            {
                // All connections/append sections have drained; active model runs
                // and CAS pins remain alive. Another repair may already have won.
                if (!force)
                {
                    try
                    {
                        using (var healthy = Open(paths))
                        using (var check = healthy.CreateCommand())
                        {
                            check.CommandText = "PRAGMA quick_check";
                            if (string.Equals(Convert.ToString(check.ExecuteScalar()), "ok", StringComparison.Ordinal)) return;
                        }
                    }
                    catch (SqliteException error) when (IsDamaged(error)) { }
                    catch (IndexVersionMismatch) { }
                }
                StorageFileSystem.WriteAtomic(IndexPath(paths), temporary => {
                    using (var empty = Open(paths, temporary)) { }
                    foreach (var stream in StorageJournal.Inventory(paths))
                    {
                        token.ThrowIfCancellationRequested();
                        if (stream.Resources) new ResourceJournal(paths, protector).ReplayInto(temporary, token);
                        else new ConversationJournal(paths, stream.Identity, protector).ReplayInto(temporary, token);
                    }
                });
            }
        }

        internal static string IndexPath(AppDataPaths paths) { return Path.Combine(paths.Root, "index.sqlite"); }

        internal static SqliteConnection Open(AppDataPaths paths, string path = null)
        {
            ChatStore.InitializeSqliteProvider();
            var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path ?? IndexPath(paths), Pooling = false, DefaultTimeout = 10 }.ToString());
            try
            {
                db.Open();
                using (var command = db.CreateCommand())
                {
                    command.CommandText = "PRAGMA foreign_keys=ON"; command.ExecuteNonQuery();
                    command.CommandText = "PRAGMA user_version";
                    var version = Convert.ToInt32(command.ExecuteScalar());
                    if (version != 0 && version != 9) throw new IndexVersionMismatch();
                    if (version == 9) return db;
                    using (var transaction = db.BeginTransaction())
                    {
                    command.Transaction = transaction;
                    command.CommandText = "PRAGMA user_version";
                    version = Convert.ToInt32(command.ExecuteScalar());
                    if (version == 9) { transaction.Commit(); return db; }
                    if (version != 0) throw new IndexVersionMismatch();
                    command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' LIMIT 1";
                    if (command.ExecuteScalar() != null) throw new IndexVersionMismatch();
                    command.CommandText = "PRAGMA busy_timeout=10000; CREATE TABLE IF NOT EXISTS stream_cursor(stream TEXT PRIMARY KEY,segment INTEGER,start_offset INTEGER,end_offset INTEGER,sequence INTEGER,hash TEXT,first_sequence INTEGER); CREATE TABLE IF NOT EXISTS result_fact(stream TEXT,chat TEXT,operation TEXT,fingerprint TEXT,sequence INTEGER,ordinal INTEGER,segment INTEGER,start_offset INTEGER,end_offset INTEGER,hash TEXT,first_sequence INTEGER,run TEXT,context_optional INTEGER,PRIMARY KEY(stream,operation),UNIQUE(stream,chat,ordinal)); CREATE INDEX result_run_page ON result_fact(stream,chat,run,sequence); CREATE INDEX result_optional_page ON result_fact(stream,chat,sequence) WHERE context_optional=1; CREATE INDEX IF NOT EXISTS result_page ON result_fact(stream,chat,sequence); CREATE TABLE IF NOT EXISTS chat_sequence(stream TEXT,chat TEXT,next_e INTEGER,next_t INTEGER,PRIMARY KEY(stream,chat))";
                    command.CommandText += "; CREATE TABLE IF NOT EXISTS call_fact(stream TEXT,chat TEXT,run TEXT,call TEXT,operation TEXT,fingerprint TEXT,segment INTEGER,start_offset INTEGER,end_offset INTEGER,sequence INTEGER,hash TEXT,first_sequence INTEGER,terminal TEXT,signature TEXT,PRIMARY KEY(stream,chat,run,call),UNIQUE(stream,operation))";
                    command.CommandText += "; CREATE TABLE context_required(stream TEXT,chat TEXT,operation TEXT,run TEXT,sequence INTEGER,unknown_effect INTEGER,retry INTEGER,uri TEXT,view TEXT,read_scope INTEGER,signature TEXT,repeat_guard INTEGER,PRIMARY KEY(stream,operation)); CREATE INDEX context_unknown ON context_required(stream,chat,sequence) WHERE unknown_effect=1; CREATE INDEX context_repeat_guard ON context_required(stream,chat,signature,sequence) WHERE repeat_guard=1; CREATE INDEX context_recovery_run ON context_required(stream,chat,run,sequence) WHERE retry<>0; CREATE INDEX context_recovery_target ON context_required(stream,chat,uri,retry)";
                    command.CommandText += "; CREATE TABLE context_dependency(stream TEXT,chat TEXT,operation TEXT,uri TEXT,revision TEXT,PRIMARY KEY(stream,operation,uri),FOREIGN KEY(stream,operation) REFERENCES context_required(stream,operation) ON DELETE CASCADE); CREATE INDEX context_dependency_target ON context_dependency(stream,chat,uri,revision); CREATE TRIGGER context_recovery_cleared AFTER UPDATE OF retry ON context_required WHEN NEW.retry=0 BEGIN DELETE FROM context_dependency WHERE stream=NEW.stream AND operation=NEW.operation; END";
                    command.CommandText += "; CREATE TABLE context_body(stream TEXT,chat TEXT,body_key TEXT,operation TEXT,sequence INTEGER,PRIMARY KEY(stream,chat,body_key)); CREATE INDEX context_body_recent ON context_body(stream,chat,sequence DESC,operation)";
                    command.CommandText += "; CREATE TABLE conversation_operation(stream TEXT,operation TEXT,kind TEXT,PRIMARY KEY(stream,operation))";
                    command.CommandText += "; CREATE TABLE resource_link(stream TEXT,chat TEXT,run TEXT,call TEXT,operation TEXT,fingerprint TEXT,segment INTEGER,start_offset INTEGER,end_offset INTEGER,sequence INTEGER,hash TEXT,first_sequence INTEGER,PRIMARY KEY(stream,operation)); CREATE INDEX resource_link_current ON resource_link(stream,chat,run,call,sequence)";
                    command.CommandText += "; CREATE TABLE resource_fact(scope TEXT,operation TEXT,fingerprint TEXT,segment INTEGER,start_offset INTEGER,end_offset INTEGER,sequence INTEGER UNIQUE,hash TEXT,first_sequence INTEGER,PRIMARY KEY(scope,operation)); CREATE TABLE resource_scope(scope TEXT PRIMARY KEY,generation INTEGER); CREATE TABLE resource_head(scope TEXT,uri TEXT,operation TEXT,PRIMARY KEY(scope,uri)); CREATE TABLE resource_attempt(scope TEXT,attempt TEXT,operation TEXT,PRIMARY KEY(scope,attempt)); CREATE TABLE resource_revision(scope TEXT,uri TEXT,revision TEXT,operation TEXT,PRIMARY KEY(scope,uri,revision)); CREATE TABLE resource_view(scope TEXT,uri TEXT,revision TEXT,view TEXT,coverage TEXT,operation TEXT,PRIMARY KEY(scope,uri,revision,view,coverage))";
                    command.CommandText += "; CREATE TABLE chat_card(stream TEXT,chat TEXT,alias TEXT,operation TEXT,fingerprint TEXT,segment INTEGER,start_offset INTEGER,end_offset INTEGER,sequence INTEGER,hash TEXT,first_sequence INTEGER,PRIMARY KEY(stream,chat)); CREATE INDEX chat_alias ON chat_card(stream,alias)";
                    command.CommandText += "; CREATE TABLE agent_delivery(stream TEXT,operation TEXT,message TEXT,sender TEXT,recipient TEXT,run TEXT,call TEXT,chain TEXT,reply_to TEXT,resolution TEXT,created_ticks INTEGER,fingerprint TEXT,segment INTEGER,start_offset INTEGER,end_offset INTEGER,sequence INTEGER,hash TEXT,first_sequence INTEGER,PRIMARY KEY(stream,operation),UNIQUE(stream,message),UNIQUE(stream,sender,run,call)); CREATE INDEX agent_replies ON agent_delivery(stream,reply_to,sender,recipient,sequence); CREATE UNIQUE INDEX agent_resolution ON agent_delivery(stream,reply_to) WHERE resolution IS NOT NULL";
                    command.CommandText += "; CREATE TABLE agent_chain(stream TEXT,chain TEXT,root TEXT,participants INTEGER,PRIMARY KEY(stream,chain)); CREATE TABLE agent_participant(stream TEXT,chain TEXT,chat TEXT,PRIMARY KEY(stream,chain,chat))";
                    command.CommandText += "; CREATE TABLE input_state(stream TEXT,message TEXT,recipient TEXT,status TEXT,revision INTEGER,run TEXT,accepted_sequence INTEGER,operation TEXT,fingerprint TEXT,segment INTEGER,start_offset INTEGER,end_offset INTEGER,sequence INTEGER,hash TEXT,first_sequence INTEGER,PRIMARY KEY(stream,message)); CREATE INDEX pending_input_page ON input_state(stream,recipient,status,accepted_sequence)";
                    command.CommandText += "; CREATE TABLE input_transition(stream TEXT,operation TEXT,message TEXT,fingerprint TEXT,segment INTEGER,start_offset INTEGER,end_offset INTEGER,sequence INTEGER,hash TEXT,first_sequence INTEGER,PRIMARY KEY(stream,operation))";
                    command.CommandText += "; CREATE TABLE agent_spawn(stream TEXT,operation TEXT,sender TEXT,run TEXT,call TEXT,child TEXT,fingerprint TEXT,segment INTEGER,start_offset INTEGER,end_offset INTEGER,sequence INTEGER,hash TEXT,first_sequence INTEGER,PRIMARY KEY(stream,operation),UNIQUE(stream,sender,run,call))";
                    command.CommandText += "; CREATE TABLE run_fact(stream TEXT,operation TEXT,chat TEXT,run TEXT,revision INTEGER,fingerprint TEXT,segment INTEGER,start_offset INTEGER,end_offset INTEGER,sequence INTEGER,hash TEXT,first_sequence INTEGER,PRIMARY KEY(stream,operation)); CREATE TABLE run_current(stream TEXT,chat TEXT,operation TEXT,PRIMARY KEY(stream,chat)); CREATE TABLE run_identity(stream TEXT,chat TEXT,run TEXT,PRIMARY KEY(stream,chat,run))";
                    command.CommandText += "; CREATE TABLE control_fact(stream TEXT,operation TEXT,chat TEXT,fingerprint TEXT,segment INTEGER,start_offset INTEGER,end_offset INTEGER,sequence INTEGER,hash TEXT,first_sequence INTEGER,PRIMARY KEY(stream,operation)); CREATE TABLE control_current(stream TEXT,chat TEXT,operation TEXT,PRIMARY KEY(stream,chat))";
                    command.ExecuteNonQuery();
                    command.CommandText = "PRAGMA user_version=9"; command.ExecuteNonQuery();
                    transaction.Commit();
                    }
                }
                return db;
            }
            catch { db.Dispose(); throw; }
        }

        internal static JournalCursor LoadCursor(SqliteConnection db, string stream)
        {
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT segment,start_offset,end_offset,sequence,hash,first_sequence FROM stream_cursor WHERE stream=$stream";
                command.Parameters.AddWithValue("$stream", stream);
                using (var reader = command.ExecuteReader()) return reader.Read() ? ReadCursor(reader, 0) : null;
            }
        }

        internal static void SaveCursor(SqliteConnection db, string stream, JournalCursor cursor, SqliteTransaction transaction)
        {
            using (var command = db.CreateCommand())
            {
                command.Transaction = transaction; command.CommandText = "INSERT OR REPLACE INTO stream_cursor VALUES($stream,$segment,$start,$end,$sequence,$hash,$first)";
                command.Parameters.AddWithValue("$stream", stream); AddPosition(command, cursor); command.ExecuteNonQuery();
            }
        }

        internal static void AddPosition(SqliteCommand command, JournalCursor cursor)
        {
            command.Parameters.AddWithValue("$segment", cursor.Segment); command.Parameters.AddWithValue("$start", cursor.Start);
            command.Parameters.AddWithValue("$end", cursor.End); command.Parameters.AddWithValue("$sequence", cursor.Sequence);
            command.Parameters.AddWithValue("$hash", cursor.Hash); command.Parameters.AddWithValue("$first", cursor.SegmentFirstSequence);
        }

        internal static JournalCursor ReadCursor(SqliteDataReader reader, int offset)
        { return new JournalCursor { Segment = reader.GetInt32(offset), Start = reader.GetInt64(offset + 1), End = reader.GetInt64(offset + 2),
            Sequence = reader.GetInt64(offset + 3), Hash = reader.GetString(offset + 4), SegmentFirstSequence = reader.GetInt64(offset + 5) }; }

    }
}
