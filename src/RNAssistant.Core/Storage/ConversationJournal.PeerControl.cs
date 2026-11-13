using System;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ConversationJournal
    {
        private SessionEvent CommitChatControl(ChatSession session, ConversationInputEvent change)
        {
            var operation = Guid.NewGuid().ToString("N");
            using (RuntimeStorageActivity.Acquire(_paths))
            {
                var receipt = Access((db, cursor) => {
                    RequireChatRevision(ConversationMetadata.From(session), TextChat(db, session.Id));
                    var control = PrepareChatControl(db, session, change, QueueControl(db, session.Id));
                    var fact = new ConversationQueueControl(control.ChatId, control.Revision, control.PreviousHash, control.Action,
                        control.RunId, control.InvocationId, control.PauseId, control.RunHash, control.Reason,
                        control.LiveInvocationId, control.RequiresUserResume, chatRevision: session.Revision, createdUtc: DateTime.UtcNow);
                    var record = DeliveryRecord(operation, "queue.control", fact, TextPatternEngine.Sha256(JsonConvert.SerializeObject(fact)));
                    ValidateControl(db, operation, fact); ValidatePeerControl(db, record, fact);
                    var position = Journal.Append(cursor, operation, record.Fingerprint, record.Kind, record.Data);
                    try { Fault?.Invoke("before_index"); ApplyControl(db, record, position); }
                    catch (Exception error) when (error is IOException || error is SqliteException) { }
                    return new QueueControlReceipt(operation, record.Fingerprint, fact, position, false);
                }, CancellationToken.None);
                receipt = Access((db, cursor) => FindControl(db, operation), CancellationToken.None)
                    ?? throw new InvalidDataException("Queue control acceptance is unavailable.");
                ApplyPeerCursor(session, receipt.Position, receipt.Fact.ChangedUtc, receipt.Fact.ObservedRevision);
                return ProjectControlEvent(receipt, session.Id);
            }
        }

        ConversationInputRecord IEventStore.AcceptAgentControl(ChatSession source, ChatSession target, AgentControlCommand command, Action dispatch)
        {
            if (command == null || dispatch == null) throw new ArgumentNullException(nameof(command));
            command = JsonConvert.DeserializeObject<AgentControlCommand>(JsonConvert.SerializeObject(command));
            var context = command.Execution;
            var cancel = command.Action == AgentControlAction.Cancel;
            var resume = command.Action == AgentControlAction.Resume;
            var tool = cancel ? AgentCommunicationToolCatalog.Cancel : resume ? AgentCommunicationToolCatalog.Resume : AgentCommunicationToolCatalog.Pause;
            if (source?.LastRun?.KernelState == null || target?.LastRun?.KernelState == null || source.Id == target.Id ||
                !Enum.IsDefined(typeof(AgentControlAction), command.Action) || context?.Call.Name != tool ||
                !SameRunValue(source.LastRun.KernelState.InFlightTool, context) || source.LastRun.RunId != context.RunId ||
                source.LastRun.TurnId != context.TurnId || target.LastRun.RunId != command.TargetRunId ||
                command.AgentId != AgentChatAddress.IdFor(target.Id) || !cancel && string.IsNullOrWhiteSpace(command.LiveInvocationId) ||
                string.IsNullOrWhiteSpace(command.RuntimeKey) || resume && string.IsNullOrWhiteSpace(command.PauseId))
                throw new InvalidOperationException("Peer control requires its exact entered call and live target.");
            var action = cancel ? QueueControlAction.AgentCancelRequested : resume ? QueueControlAction.AgentPauseReleased : QueueControlAction.AgentPauseRequested;
            var pause = cancel ? null : resume ? command.PauseId : TextPatternEngine.Sha256(_scope + "\n" + target.Id + "\n" + command.OperationId);
            QueueControlReceipt accepted;
            using (RuntimeStorageActivity.Acquire(_paths))
            {
                try
                {
                    accepted = Access((db, cursor) => {
                        var own = FindControl(db, command.OperationId);
                        if (own != null && cancel) throw new InvalidOperationException("A recorded cancel request cannot be dispatched again.");
                        if (own != null) return own;
                        RequireChatRevision(ConversationMetadata.From(source), TextChat(db, source.Id));
                        RequireChatRevision(ConversationMetadata.From(target), TextChat(db, target.Id));
                        RequirePeerEntry(db, source.Id, context);
                        var previous = QueueControl(db, target.Id); var run = CurrentRun(db, target.Id);
                        var origin = new ConversationPeerControlOrigin(source.Id, context.RunId, context.Call.Id, context.StepId,
                            source.Revision, target.Revision, command.RuntimeKey, DateTime.UtcNow);
                        var fact = new ConversationQueueControl(target.Id, (previous?.Fact.Revision ?? 0) + 1, previous?.Position.Hash,
                            action, command.TargetRunId, run?.Fact.InvocationId, pause, run?.Position.Hash,
                            reason: cancel ? "Остановлено другим агентом" : null,
                            liveInvocationId: command.LiveInvocationId, origin: origin);
                        var record = DeliveryRecord(command.OperationId, "queue.control", fact, TextPatternEngine.Sha256(JsonConvert.SerializeObject(fact)));
                        ValidateControl(db, record.Operation, fact); ValidatePeerControl(db, record, fact);
                        dispatch();
                        var position = Journal.Append(cursor, record.Operation, record.Fingerprint, record.Kind, record.Data);
                        var projected = true;
                        try { Fault?.Invoke("before_index"); ApplyControl(db, record, position); }
                        catch (Exception error) when (error is IOException || error is SqliteException) { projected = false; }
                        return new QueueControlReceipt(record.Operation, record.Fingerprint, fact, position, projected);
                    }, CancellationToken.None);
                }
                catch (IOException)
                {
                    accepted = Access((db, cursor) => FindControl(db, command.OperationId), CancellationToken.None);
                    if (accepted == null) throw;
                }
                Fault?.Invoke("before_peer_control_readback");
                accepted = Access((db, cursor) => FindControl(db, accepted.Operation), CancellationToken.None)
                    ?? throw new InvalidDataException("Peer control acceptance is unavailable.");
                var saved = accepted.Fact; var proof = saved.Origin;
                if (proof == null || saved.ChatId != target.Id || saved.Action != action || saved.PauseId != pause ||
                    saved.RunId != command.TargetRunId || saved.LiveInvocationId != command.LiveInvocationId || saved.RequiresUserResume ||
                    proof.Sender != source.Id || proof.RunId != context.RunId || proof.CallId != context.Call.Id ||
                    proof.StepId != context.StepId || proof.RuntimeKey != command.RuntimeKey)
                    throw new InvalidDataException("Peer control receipt differs from its exact request.");
                ApplyPeerCursor(source, accepted.Position, proof.CreatedUtc, proof.SenderRevision);
                ApplyPeerCursor(target, accepted.Position, proof.CreatedUtc, proof.TargetRevision);
                return new ConversationInputRecord(ProjectControlEvent(accepted, target.Id), ProjectQueueControl(saved));
            }
        }

        private QueueControlReceipt ControlForCall(SqliteConnection db, string sender, string run, string call)
        {
            using (var command = Command(db, "SELECT operation FROM control_fact WHERE stream=$stream AND sender=$sender AND source_run=$run AND source_call=$call"))
            {
                command.Parameters.AddWithValue("$sender", sender); command.Parameters.AddWithValue("$run", run); command.Parameters.AddWithValue("$call", call);
                var operation = command.ExecuteScalar() as string;
                return operation == null ? null : FindControl(db, operation);
            }
        }

        private void ValidatePeerControl(SqliteConnection db, JournalRecord record, ConversationQueueControl fact)
        {
            var proof = fact.Origin;
            if (proof == null)
            {
                if (!fact.NativeChat)
                { if (Card(db, fact.ChatId)?.Item1.Address != null) throw TextStoreScope(); return; }
                if (TextChat(db, fact.ChatId)?.Position.Sequence != fact.ObservedRevision ||
                    fact.PeerControl && (fact.LiveInvocationId == null || !fact.RequiresUserResume && fact.Action != QueueControlAction.AgentPauseAcknowledged))
                    throw new InvalidOperationException("Local control requires its observed chat and user/acknowledgement authority.");
                return;
            }
            var source = TextChat(db, proof.Sender); var target = TextChat(db, fact.ChatId);
            if (source == null || target == null || proof.Sender == fact.ChatId || source.Position.Sequence != proof.SenderRevision ||
                target.Position.Sequence != proof.TargetRevision || ControlForCall(db, proof.Sender, proof.RunId, proof.CallId) != null)
                throw new ChatConcurrencyException("Peer control requires both observed heads and an unused accepted call.");
            RequirePeerEntry(db, proof.Sender, proof.RunId, proof.CallId, proof.StepId,
                fact.Cancellation ? AgentCommunicationToolCatalog.Cancel :
                    fact.Action == QueueControlAction.AgentPauseReleased ? AgentCommunicationToolCatalog.Resume : AgentCommunicationToolCatalog.Pause);
        }

        private void ApplyPeerControlHeads(SqliteConnection db, SqliteTransaction tx, JournalRecord record,
            JournalCursor position, ConversationQueueControl fact)
        {
            using (var command = Command(db, "UPDATE chat_state SET head_operation=$operation,last_activity_ticks=$activity WHERE stream=$stream AND (chat=$sender OR chat=$target)", tx))
            {
                command.Parameters.AddWithValue("$operation", record.Operation); command.Parameters.AddWithValue("$activity", fact.ChangedUtc.Ticks);
                command.Parameters.AddWithValue("$sender", (object)fact.Origin?.Sender ?? DBNull.Value); command.Parameters.AddWithValue("$target", fact.ChatId);
                if (command.ExecuteNonQuery() != (fact.Origin == null ? 1 : 2)) throw new InvalidDataException("Queue control lost a chat head.");
            }
        }

        private ConversationQueueControl PrepareChatControl(SqliteConnection db, ChatSession session,
            ConversationInputEvent change, QueueControlReceipt previous)
        {
            var old = previous?.Fact;
            if (change.Input != null && old?.PeerControl == true) return PreserveControl(old, previous.Position.Hash);
            var pause = change.AgentPause;
            if (pause != null)
            {
                if (string.IsNullOrWhiteSpace(pause.LiveInvocationId)) throw new InvalidOperationException("Pause requires its live owner lifetime.");
                if (!pause.RequiresUserResume && (old?.PauseId != pause.PauseId || !change.Paused))
                    throw new InvalidOperationException("A peer request requires its source acceptance; local writes only acknowledge it.");
                var run = CurrentRun(db, session.Id);
                return new ConversationQueueControl(session.Id, (old?.Revision ?? 0) + 1, previous?.Position.Hash,
                    change.Paused && old?.PauseId == pause.PauseId ? QueueControlAction.AgentPauseAcknowledged : QueueControlAction.AgentPauseRequested,
                    pause.RunId, run?.Fact.InvocationId, pause.PauseId, run?.Position.Hash, change.PauseReason,
                    pause.LiveInvocationId, pause.RequiresUserResume);
            }
            return new ConversationQueueControl(session.Id, (old?.Revision ?? 0) + 1, previous?.Position.Hash,
                change.Paused ? QueueControlAction.UserPause : QueueControlAction.UserResume, null, null, null, null, change.PauseReason);
        }

        private static ConversationQueueControl PreserveControl(ConversationQueueControl previous, string hash)
        { return new ConversationQueueControl(previous.ChatId, previous.Revision + 1, hash, previous.Action,
            previous.RunId, previous.InvocationId, previous.PauseId, previous.RunHash, previous.Reason,
            previous.LiveInvocationId, previous.RequiresUserResume); }

        private static bool SameControlState(ConversationQueueControl current, ConversationQueueControl previous)
        { return previous != null && current.Action == previous.Action && current.RunId == previous.RunId &&
            current.InvocationId == previous.InvocationId && current.PauseId == previous.PauseId && current.RunHash == previous.RunHash &&
            current.Reason == previous.Reason && current.LiveInvocationId == previous.LiveInvocationId && current.RequiresUserResume == previous.RequiresUserResume; }

        private static ConversationInputEvent ProjectQueueControl(ConversationQueueControl control, ConversationInput input = null)
        { return new ConversationInputEvent { Input = input, Paused = control.Paused && control.Action != QueueControlAction.AgentPauseRequested,
            PauseReason = control.Reason, AgentPause = control.PeerControl && control.Action != QueueControlAction.AgentPauseReleased
                ? new ConversationAgentPause { RunId = control.RunId, PauseId = control.PauseId,
                    LiveInvocationId = control.LiveInvocationId, RequiresUserResume = control.RequiresUserResume } : null }; }

        private static bool ControlAffects(QueueControlReceipt receipt, string chat)
        { return receipt?.Fact.NativeChat == true && (receipt.Fact.ChatId == chat || receipt.Fact.Origin?.Sender == chat); }

        private static SessionEvent ProjectControlEvent(QueueControlReceipt receipt, string chat)
        {
            if (!ControlAffects(receipt, chat)) throw new InvalidDataException("Control is outside this chat.");
            var incoming = receipt.Fact.ChatId == chat;
            return new SessionEvent { SessionId = chat, Sequence = receipt.Position.Sequence, EventId = receipt.Operation,
                Hash = receipt.Position.Hash, CreatedUtc = receipt.Fact.ChangedUtc,
                Type = incoming ? SessionEventTypes.ConversationInputChanged : "agent.control",
                RunId = incoming ? receipt.Fact.RunId : receipt.Fact.Origin.RunId,
                Data = incoming ? JObject.FromObject(ProjectQueueControl(receipt.Fact)) : JObject.FromObject(receipt.Fact) };
        }
    }

    internal sealed class ConversationPeerControlOrigin
    {
        public string Sender { get; private set; }
        public string RunId { get; private set; }
        public string CallId { get; private set; }
        public string StepId { get; private set; }
        public long SenderRevision { get; private set; }
        public long TargetRevision { get; private set; }
        public string RuntimeKey { get; private set; }
        public DateTime CreatedUtc { get; private set; }
        [JsonConstructor] internal ConversationPeerControlOrigin(string sender, string runId, string callId, string stepId,
            long senderRevision, long targetRevision, string runtimeKey, DateTime createdUtc)
        {
            if (string.IsNullOrWhiteSpace(sender) || string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(callId) ||
                string.IsNullOrWhiteSpace(stepId) || string.IsNullOrWhiteSpace(runtimeKey) || senderRevision < 1 || targetRevision < 1 ||
                createdUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("Invalid peer control origin.");
            Sender = sender; RunId = runId; CallId = callId; StepId = stepId; SenderRevision = senderRevision;
            TargetRevision = targetRevision; RuntimeKey = runtimeKey; CreatedUtc = createdUtc;
        }
    }
}
