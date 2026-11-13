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
                if (Card(db, chat)?.Item1.Address != null) throw TextStoreScope();
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

        private void ValidateControl(SqliteConnection db, string operation, ConversationQueueControl fact, bool freshChat = false, bool preservePause = false)
        {
            if (string.IsNullOrWhiteSpace(operation) || OperationExists(db, operation) || (Card(db, fact.ChatId) == null) != freshChat)
                throw new InvalidOperationException("Control operation/chat identity conflicts.");
            var prior = QueueControl(db, fact.ChatId);
            if (fact.PreviousHash != prior?.Position.Hash || fact.Revision != (prior?.Fact.Revision ?? 0) + 1)
                throw new InvalidOperationException("Control revision changed.");
            if (preservePause)
            {
                if (!SameControlState(fact, prior?.Fact)) throw new InvalidOperationException("Input cannot replace queue authority.");
                return;
            }
            if (!fact.PeerControl && !fact.Cancellation) return;
            var run = CurrentRun(db, fact.ChatId);
            if (run == null || run.Position.Hash != fact.RunHash || run.Fact.RunId != fact.RunId || run.Fact.InvocationId != fact.InvocationId ||
                TerminalRun(run.Fact.Lifecycle)) throw new InvalidOperationException("Control does not address the exact current invocation.");
            if (fact.Cancellation)
            {
                if (fact.LiveInvocationId == null && run.Fact.Lifecycle != RunLifecycle.Waiting)
                    throw new InvalidOperationException("Cold cancellation requires a saved wait.");
                return;
            }
            if (fact.Action == QueueControlAction.AgentPauseRequested)
            {
                var userReplacement = fact.RequiresUserResume && prior?.Fact.PeerControl == true &&
                    prior.Fact.LiveInvocationId == fact.LiveInvocationId && prior.Fact.RunId == fact.RunId;
                if (prior?.Fact.Paused == true && !userReplacement ||
                    run.Fact.Lifecycle != RunLifecycle.Running && !(fact.LiveInvocationId != null &&
                        run.Fact.Lifecycle == RunLifecycle.Waiting && (run.Fact.PauseId == null || userReplacement)) ||
                    (fact.LiveInvocationId == null || fact.Origin != null) &&
                        fact.PauseId != TextPatternEngine.Sha256(_scope + "\n" + fact.ChatId + "\n" + operation))
                    throw new InvalidOperationException("An existing user/recovery/agent pause cannot be replaced.");
            }
            else
            {
                var previous = prior?.Fact;
                if (previous?.PeerControl != true || !previous.Paused || previous.RunId != fact.RunId ||
                    (fact.LiveInvocationId == null ? previous.InvocationId != fact.InvocationId :
                        previous.LiveInvocationId != fact.LiveInvocationId) || previous.PauseId != fact.PauseId ||
                    previous.RequiresUserResume != fact.RequiresUserResume || fact.Origin != null && previous.RequiresUserResume)
                    throw new InvalidOperationException("Peer control cannot release/acknowledge a different or protected pause.");
                if (fact.Action == QueueControlAction.AgentPauseAcknowledged &&
                    (previous.Action != QueueControlAction.AgentPauseRequested || run.Fact.Lifecycle != RunLifecycle.Waiting || run.Fact.PauseId != fact.PauseId))
                    throw new InvalidOperationException("Pause acknowledgement requires the exact kernel safe boundary.");
            }
        }

        private void ApplyControl(SqliteConnection db, JournalRecord record, JournalCursor position)
        {
            var fact = DecodeControl(record); ValidateControl(db, record.Operation, fact);
            ValidatePeerControl(db, record, fact);
            using (var tx = db.BeginTransaction())
            {
                RegisterOperation(db, tx, record.Operation, record.Kind);
                ApplyControlRows(db, tx, record, position, fact);
                if (fact.NativeChat) ApplyPeerControlHeads(db, tx, record, position, fact);
                SaveCursor(db, position, tx, record); tx.Commit();
            }
        }

        private void ApplyControlRows(SqliteConnection db, SqliteTransaction tx, JournalRecord record,
            JournalCursor position, ConversationQueueControl fact)
        {
            using (var command = Command(db, "INSERT INTO control_fact VALUES($stream,$operation,$chat,$fingerprint,$segment,$start,$end,$sequence,$hash,$first,$sender,$source_run,$source_call,$native); INSERT INTO control_current VALUES($stream,$chat,$operation) ON CONFLICT(stream,chat) DO UPDATE SET operation=$operation", tx))
            {
                command.Parameters.AddWithValue("$native", fact.NativeChat ? 1 : 0);
                command.Parameters.AddWithValue("$sender", (object)fact.Origin?.Sender ?? DBNull.Value);
                command.Parameters.AddWithValue("$source_run", (object)fact.Origin?.RunId ?? DBNull.Value);
                command.Parameters.AddWithValue("$source_call", (object)fact.Origin?.CallId ?? DBNull.Value);
                command.Parameters.AddWithValue("$chat", fact.ChatId); AddDeliveryPosition(command, record, position); command.ExecuteNonQuery();
            }
        }

        private static ConversationQueueControl DecodeControl(JournalRecord record)
        {
            if (record.Kind == "chat.committed") return DecodeChatCommit(record).Control ?? throw new InvalidDataException("Missing queue control.");
            if (record.Kind == "agent.accepted" || record.Kind == "agent.spawned") return DecodeDelivery(record).Control ?? throw new InvalidDataException("Missing peer input control.");
            var fact = DeliveryData<ConversationQueueControl>(record);
            if (record.Kind != "queue.control" || fact == null || record.Fingerprint != TextPatternEngine.Sha256(JsonConvert.SerializeObject(fact)))
                throw new InvalidDataException("Invalid queue control fact.");
            return fact;
        }
    }

    internal enum QueueControlAction { UserPause, RecoveryPause, UserResume, AgentPauseRequested, AgentPauseAcknowledged, AgentPauseReleased, AgentCancelRequested }

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
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string LiveInvocationId { get; private set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public bool RequiresUserResume { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public ConversationPeerControlOrigin Origin { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public long? ChatRevision { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public DateTime? CreatedUtc { get; private set; }
        [JsonIgnore] internal bool NativeChat { get { return Origin != null || ChatRevision.HasValue; } }
        [JsonIgnore] internal long ObservedRevision { get { return Origin?.TargetRevision ?? ChatRevision.Value; } }
        [JsonIgnore] internal DateTime ChangedUtc { get { return Origin?.CreatedUtc ?? CreatedUtc.Value; } }
        [JsonIgnore] internal bool Paused { get { return Action != QueueControlAction.UserResume && Action != QueueControlAction.AgentPauseReleased; } }
        [JsonIgnore] internal bool PeerControl { get { return Action == QueueControlAction.AgentPauseRequested || Action == QueueControlAction.AgentPauseAcknowledged || Action == QueueControlAction.AgentPauseReleased; } }
        [JsonIgnore] internal bool Cancellation { get { return Action == QueueControlAction.AgentCancelRequested; } }
        [JsonConstructor]
        internal ConversationQueueControl(string chatId, long revision, string previousHash, QueueControlAction action,
            string runId, string invocationId, string pauseId, string runHash, string reason = null,
            string liveInvocationId = null, bool requiresUserResume = false, ConversationPeerControlOrigin origin = null,
            long? chatRevision = null, DateTime? createdUtc = null)
        {
            Action = action; Reason = reason; LiveInvocationId = liveInvocationId; RequiresUserResume = requiresUserResume; Origin = origin;
            ChatRevision = chatRevision; CreatedUtc = createdUtc;
            if (string.IsNullOrWhiteSpace(chatId) || revision < 1 || !Enum.IsDefined(typeof(QueueControlAction), action) ||
                (revision == 1 ? previousHash != null : string.IsNullOrWhiteSpace(previousHash)) ||
                (PeerControl || Cancellation ? string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(invocationId) ||
                    string.IsNullOrWhiteSpace(runHash) || (Cancellation ? pauseId != null || origin == null || requiresUserResume : string.IsNullOrWhiteSpace(pauseId))
                    : runId != null || invocationId != null || pauseId != null || runHash != null || liveInvocationId != null || requiresUserResume) ||
                origin != null && (chatRevision.HasValue || !Cancellation && liveInvocationId == null || requiresUserResume ||
                    !Cancellation && action != QueueControlAction.AgentPauseRequested && action != QueueControlAction.AgentPauseReleased) ||
                chatRevision.HasValue != createdUtc.HasValue || chatRevision.HasValue && (chatRevision < 1 || createdUtc.Value.Kind != DateTimeKind.Utc))
                throw new ArgumentException("Invalid queue control metadata.");
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
