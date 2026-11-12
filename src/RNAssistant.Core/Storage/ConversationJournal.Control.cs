using System;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ConversationJournal
    {
        // Durable request/acknowledgement only. The Application owner must hold
        // the live invocation/lease before accepting a peer control or waking it.
        internal QueueControlReceipt CommitControl(string operation, string chat, QueueControlReceipt expected,
            QueueControlAction action, RunCheckpointReceipt run = null, CancellationToken token = default(CancellationToken))
        {
            var peer = action == QueueControlAction.AgentPauseRequested || action == QueueControlAction.AgentPauseAcknowledged || action == QueueControlAction.AgentPauseReleased;
            var pause = action == QueueControlAction.AgentPauseRequested ? TextPatternEngine.Sha256(_scope + "\n" + chat + "\n" + operation) : expected?.Fact.PauseId;
            var fact = new ConversationQueueControl(chat, (expected?.Fact.Revision ?? 0) + 1, expected?.Position.Hash, action,
                peer ? run?.Fact.RunId : null, peer ? run?.Fact.InvocationId : null, peer ? pause : null, peer ? run?.Position.Hash : null);
            var fingerprint = TextPatternEngine.Sha256(JsonConvert.SerializeObject(fact));
            return Access((db, cursor) => {
                var prior = FindControl(db, operation);
                if (prior != null)
                {
                    if (prior.Fingerprint != fingerprint) throw new InvalidOperationException("Control operation was reused with different content.");
                    return prior;
                }
                ValidateControl(db, operation, fact);
                var record = DeliveryRecord(operation, "queue.control", fact, fingerprint);
                var position = Journal.Append(cursor, operation, fingerprint, record.Kind, record.Data);
                var projected = true;
                try { Fault?.Invoke("before_index"); ApplyControl(db, record, position); }
                catch (Exception error) when (error is IOException || error is SqliteException) { projected = false; }
                return new QueueControlReceipt(operation, fingerprint, fact, position, projected);
            }, token);
        }

        internal QueueControlReceipt QueueControl(string chat, CancellationToken token = default(CancellationToken))
        { return Access((db, cursor) => QueueControl(db, chat), token); }

        private QueueControlReceipt QueueControl(SqliteConnection db, string chat)
        {
            var operation = Scalar(db, "SELECT operation FROM control_current WHERE stream=$stream AND chat=$chat", chat) as string;
            var value = operation == null ? null : FindControl(db, operation);
            if (value != null && value.Fact.ChatId != chat) throw new InvalidDataException("Foreign queue control locator.");
            return value;
        }

        private QueueControlReceipt FindControl(SqliteConnection db, string operation)
        {
            using (var command = Command(db, "SELECT operation,fingerprint,segment,start_offset,end_offset,sequence,hash,first_sequence FROM control_fact WHERE stream=$stream AND operation=$operation"))
            {
                command.Parameters.AddWithValue("$operation", operation);
                JournalCursor position; var record = LocatedRecord(command, out position);
                if (record == null) return null;
                if (record.Operation != operation) throw new InvalidDataException("Foreign queue control receipt.");
                return new QueueControlReceipt(operation, record.Fingerprint, DecodeControl(record), position, true);
            }
        }

        private void ValidateControl(SqliteConnection db, string operation, ConversationQueueControl fact)
        {
            if (string.IsNullOrWhiteSpace(operation) || OperationExists(db, operation) || Card(db, fact.ChatId) == null)
                throw new InvalidOperationException("Control operation/chat identity conflicts.");
            var prior = QueueControl(db, fact.ChatId);
            if (fact.PreviousHash != prior?.Position.Hash || fact.Revision != (prior?.Fact.Revision ?? 0) + 1)
                throw new InvalidOperationException("Control revision changed.");
            if (!fact.PeerControl) return;
            var run = CurrentRun(db, fact.ChatId);
            if (run == null || run.Position.Hash != fact.RunHash || run.Fact.RunId != fact.RunId || run.Fact.InvocationId != fact.InvocationId ||
                TerminalRun(run.Fact.Lifecycle)) throw new InvalidOperationException("Control does not address the exact current invocation.");
            if (fact.Action == QueueControlAction.AgentPauseRequested)
            {
                if (prior?.Fact.Paused == true || run.Fact.Lifecycle != RunLifecycle.Running ||
                    fact.PauseId != TextPatternEngine.Sha256(_scope + "\n" + fact.ChatId + "\n" + operation))
                    throw new InvalidOperationException("An existing user/recovery/agent pause cannot be replaced.");
            }
            else
            {
                var previous = prior?.Fact;
                if (previous?.PeerControl != true || !previous.Paused || previous.RunId != fact.RunId ||
                    previous.InvocationId != fact.InvocationId || previous.PauseId != fact.PauseId)
                    throw new InvalidOperationException("Peer control cannot release/acknowledge a different or protected pause.");
                if (fact.Action == QueueControlAction.AgentPauseAcknowledged &&
                    (previous.Action != QueueControlAction.AgentPauseRequested || run.Fact.Lifecycle != RunLifecycle.Waiting || run.Fact.PauseId != fact.PauseId))
                    throw new InvalidOperationException("Pause acknowledgement requires the exact kernel safe boundary.");
            }
        }

        private void ApplyControl(SqliteConnection db, JournalRecord record, JournalCursor position)
        {
            var fact = DecodeControl(record); ValidateControl(db, record.Operation, fact);
            using (var tx = db.BeginTransaction())
            {
                RegisterOperation(db, tx, record.Operation, record.Kind);
                ApplyControlRows(db, tx, record, position, fact);
                SaveCursor(db, position, tx); tx.Commit();
            }
        }

        private void ApplyControlRows(SqliteConnection db, SqliteTransaction tx, JournalRecord record,
            JournalCursor position, ConversationQueueControl fact)
        {
            using (var command = Command(db, "INSERT INTO control_fact VALUES($stream,$operation,$chat,$fingerprint,$segment,$start,$end,$sequence,$hash,$first); INSERT INTO control_current VALUES($stream,$chat,$operation) ON CONFLICT(stream,chat) DO UPDATE SET operation=$operation", tx))
            {
                command.Parameters.AddWithValue("$chat", fact.ChatId); AddDeliveryPosition(command, record, position); command.ExecuteNonQuery();
            }
        }

        private static ConversationQueueControl DecodeControl(JournalRecord record)
        {
            if (record.Kind == "chat.committed") return DecodeChatCommit(record).Control ?? throw new InvalidDataException("Missing queue control.");
            var fact = DeliveryData<ConversationQueueControl>(record);
            if (record.Kind != "queue.control" || fact == null || record.Fingerprint != TextPatternEngine.Sha256(JsonConvert.SerializeObject(fact)))
                throw new InvalidDataException("Invalid queue control fact.");
            return fact;
        }
    }

    internal enum QueueControlAction { UserPause, RecoveryPause, UserResume, AgentPauseRequested, AgentPauseAcknowledged, AgentPauseReleased }

    internal sealed class ConversationQueueControl
    {
        public string ChatId { get; private set; }
        public long Revision { get; private set; }
        public string PreviousHash { get; private set; }
        public QueueControlAction Action { get; private set; }
        public string RunId { get; private set; }
        public string InvocationId { get; private set; }
        public string PauseId { get; private set; }
        public string RunHash { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string Reason { get; private set; }
        [JsonIgnore] internal bool Paused { get { return Action != QueueControlAction.UserResume && Action != QueueControlAction.AgentPauseReleased; } }
        [JsonIgnore] internal bool PeerControl { get { return Action == QueueControlAction.AgentPauseRequested || Action == QueueControlAction.AgentPauseAcknowledged || Action == QueueControlAction.AgentPauseReleased; } }
        [JsonConstructor]
        internal ConversationQueueControl(string chatId, long revision, string previousHash, QueueControlAction action,
            string runId, string invocationId, string pauseId, string runHash, string reason = null)
        {
            Action = action; Reason = reason;
            if (string.IsNullOrWhiteSpace(chatId) || revision < 1 || !Enum.IsDefined(typeof(QueueControlAction), action) ||
                (revision == 1 ? previousHash != null : string.IsNullOrWhiteSpace(previousHash)) ||
                (PeerControl ? string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(invocationId) || string.IsNullOrWhiteSpace(pauseId) || string.IsNullOrWhiteSpace(runHash)
                    : runId != null || invocationId != null || pauseId != null || runHash != null)) throw new ArgumentException("Invalid queue control metadata.");
            ChatId = chatId; Revision = revision; PreviousHash = previousHash; RunId = runId; InvocationId = invocationId; PauseId = pauseId; RunHash = runHash;
        }
    }

    internal sealed class QueueControlReceipt
    {
        internal readonly string Operation, Fingerprint;
        internal readonly ConversationQueueControl Fact;
        internal readonly JournalCursor Position;
        internal readonly bool ProjectionAvailable;
        internal QueueControlReceipt(string operation, string fingerprint, ConversationQueueControl fact, JournalCursor position, bool projected)
        { Operation = operation; Fingerprint = fingerprint; Fact = fact; Position = position; ProjectionAvailable = projected; }
    }
}
