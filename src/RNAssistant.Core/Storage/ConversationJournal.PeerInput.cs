using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ConversationJournal
    {
        bool IEventStore.AtomicAgentDelivery { get { return true; } }

        AgentChatAddress IEventStore.FindAgent(ConversationMetadata source, string agentId)
        {
            return Access((db, cursor) => {
                RequireChatRevision(source, TextChat(db, source.Id));
                var card = FindAgent(db, agentId);
                return card?.Address == null ? null : CardAddress(card);
            }, CancellationToken.None);
        }

        RNAssistant.Core.Services.ConversationInputRecord IEventStore.AcceptAgentDelivery(ChatSession source, ChatSession recipient,
            AgentDeliveryCommand command, Action dispatch)
        {
            if (command == null || dispatch == null) throw new ArgumentNullException(nameof(command));
            command = JsonConvert.DeserializeObject<AgentDeliveryCommand>(JsonConvert.SerializeObject(command));
            var context = command.Execution;
            if (source?.LastRun?.KernelState == null || recipient == null || source.Id == recipient.Id ||
                context == null || !IsPeerDelivery(context.Call.Name) ||
                source.LastRun.RunId != context.RunId || source.LastRun.TurnId != context.TurnId ||
                !SameRunValue(source.LastRun.KernelState.InFlightTool, context) ||
                command.Resolution.HasValue != (context.Call.Name == AgentCommunicationToolCatalog.Resolve) ||
                command.AgentId != AgentChatAddress.IdFor(recipient.Id))
                throw new InvalidOperationException("Peer delivery requires its exact entered call and recipient.");
            AgentMessageReceipt accepted;
            using (RuntimeStorageActivity.Acquire(_paths))
            {
                try
                {
                    accepted = SendAgent(command.OperationId, source.Id, context.RunId, context.Call.Id, command.AgentId,
                        command.RootChatId, command.ChainId, command.ParticipantLimit, command.Text, command.ExpectReply,
                        command.ReplyTo, command.Assignment, command.Resolution, recipientRuntimeKey: command.RecipientRuntimeKey,
                        validateDispatch: (db, candidate) => {
                            RequireChatRevision(ConversationMetadata.From(source), TextChat(db, source.Id));
                            RequireChatRevision(ConversationMetadata.From(recipient), TextChat(db, recipient.Id));
                            RequirePeerEntry(db, source.Id, context);
                        }, dispatch: dispatch);
                }
                catch (IOException)
                {
                    accepted = AgentReceipt(command.OperationId);
                    if (accepted?.Fact.Sender != source.Id || accepted.Fact.RunId != context.RunId || accepted.Fact.CallId != context.Call.Id)
                        throw;
                }
                Fault?.Invoke("before_peer_readback");
                // Read the accepted frame/body before publishing any live projection.
                accepted = AgentReceipt(accepted.Operation) ?? throw new InvalidDataException("Peer acceptance is unavailable.");
                var text = ReadAgentText(accepted);
                var fact = accepted.Fact;
                if (fact.Sender != source.Id || fact.Recipient != recipient.Id || fact.RunId != context.RunId ||
                    fact.CallId != context.Call.Id || fact.Root != command.RootChatId || fact.ChainId != command.ChainId ||
                    fact.ParticipantLimit != command.ParticipantLimit || fact.RecipientRuntimeKey != command.RecipientRuntimeKey ||
                    fact.ExpectReply != command.ExpectReply || fact.ReplyTo != command.ReplyTo || fact.Assignment != command.Assignment ||
                    fact.Resolution != command.Resolution || text != command.Text)
                    throw new InvalidDataException("Peer acknowledgement does not match the exact request.");
                ConversationRunPolicy.ForAcceptedRun(source).RequireSame(fact.Policy);
                ApplyPeerCursor(source, accepted, accepted.Fact.SenderRevision);
                ApplyPeerCursor(recipient, accepted, accepted.Fact.RecipientRevision);
                var change = ProjectQueueControl(accepted.Fact.Control, JsonConvert.DeserializeObject<ConversationInput>(
                    JsonConvert.SerializeObject(accepted.Fact.Input.Header)));
                change.Input.Text = text;
                return new ConversationInputRecord(ProjectPeerEvent(accepted, recipient.Id), change);
            }
        }

        private static void ApplyPeerCursor(ChatSession session, AgentMessageReceipt receipt, long? previous)
        { ApplyPeerCursor(session, receipt.Position, receipt.Fact.CreatedUtc, previous); }

        private static void ApplyPeerCursor(ChatSession session, JournalCursor position, DateTime createdUtc, long? previous)
        {
            if (session.Revision >= position.Sequence) return;
            if (session.Revision != previous) throw new ChatConcurrencyException("Peer acceptance cannot skip an unseen chat update.");
            session.Revision = position.Sequence; session.UpdatedUtc = createdUtc;
            session.StorageHeadHash = position.Hash; session.StorageByteLength = position.End;
            session.StorageTailByteOffset = position.Start;
        }

        private static bool IsPeerDelivery(string tool)
        { return tool == AgentCommunicationToolCatalog.Send || tool == AgentCommunicationToolCatalog.Resolve; }

        private static bool IsPeerMutation(string tool)
        { return IsPeerDelivery(tool) || tool == AgentCommunicationToolCatalog.Spawn || IsPeerControl(tool); }

        private static bool IsPeerControl(string tool)
        { return tool == AgentCommunicationToolCatalog.Pause || tool == AgentCommunicationToolCatalog.Resume || tool == AgentCommunicationToolCatalog.Cancel; }

        private void RequirePeerEntry(SqliteConnection db, string sender, ToolExecutionContext context)
        { RequirePeerEntry(db, sender, context.RunId, context.Call.Id, context.StepId, context.Call.Name); }

        private void RequirePeerEntry(SqliteConnection db, string sender, string runId, string callId, string stepId, string toolName)
        {
            var run = CurrentRun(db, sender);
            var call = FindCall(db, sender, runId, callId);
            var operation = StartedOperation(db, sender, runId, callId);
            var start = operation == null ? null : FindChatWrite(db, operation);
            if (run?.Fact.RunId != runId || run.Fact.Lifecycle != RunLifecycle.Running ||
                start?.Fact.Tools?.MayHaveSideEffects != true || start.Fact.Tools.Kind != AgentRunEventKind.ToolStarted ||
                start.Fact.Tools.StepId != stepId || start.Fact.Tools.CallId != callId ||
                start.Fact.Tools.ToolName != toolName || call?.Call.ToolName != toolName ||
                start.Fact.Checkpoint.InvocationId != run.Fact.InvocationId ||
                call?.Call.AcceptedCallOrigin.StepId != stepId ||
                PendingTool(db, sender, runId, callId) != null)
                throw new InvalidOperationException("Peer mutation requires its active accepted call and runtime entry.");
        }

        IReadOnlyList<AgentRequestReference> IEventStore.ReadAgentRequests(ConversationMetadata session, IReadOnlyList<string> messageIds)
        {
            if (messageIds == null || messageIds.Count < 1 || messageIds.Count > 32 || messageIds.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Expected 1–32 exact request identities.");
            return Access((db, cursor) => {
                RequireInputRevision(db, session);
                var result = new List<AgentRequestReference>();
                foreach (var id in messageIds.Distinct(StringComparer.Ordinal))
                {
                    var receipt = FindDelivery(db, "message", id);
                    if (receipt?.Fact.Sender == session.Id && receipt.Position.Sequence <= session.Revision)
                        result.Add(new AgentRequestReference(id, CardAddress(Card(db, receipt.Fact.Recipient).Item1),
                            receipt.Fact.ExpectReply, receipt.Fact.Assignment));
                }
                return (IReadOnlyList<AgentRequestReference>)result;
            }, CancellationToken.None);
        }

        private AgentMessageAccepted PreparePeerInput(SqliteConnection db, string operation, string fingerprint,
            AgentMessageAccepted fact, ConversationCard sender, ConversationCard recipient, bool spawn = false)
        {
            if (sender.Address == null && recipient.Address == null) return fact;
            if (sender.Address == null || recipient.Address == null || string.IsNullOrWhiteSpace(fact.RecipientRuntimeKey))
                throw new InvalidOperationException("Peer input requires two integrated chats and the exact recipient binding.");
            var source = TextChat(db, sender.ChatId); var target = TextChat(db, recipient.ChatId);
            var prior = QueueControl(db, recipient.ChatId);
            var header = PeerInputHeader(operation, fingerprint, fact, sender, Card(db, fact.Root)?.Item1);
            var input = new ConversationInputState(recipient.ChatId, fact.MessageId, 1, ConversationInputStatus.Pending, null, header, fact.Body);
            var control = prior?.Fact.PeerControl == true ? PreserveControl(prior.Fact, prior.Position.Hash) :
                new ConversationQueueControl(recipient.ChatId, (prior?.Fact.Revision ?? 0) + 1, prior?.Position.Hash,
                prior?.Fact.Paused == true ? QueueControlAction.UserPause : QueueControlAction.UserResume,
                null, null, null, null, prior?.Fact.Reason);
            return new AgentMessageAccepted(fact.Sender, fact.Recipient, fact.RunId, fact.CallId, fact.Root, fact.ChainId,
                fact.ParticipantLimit, fact.MessageId, fact.Body, fact.ExpectReply, fact.ReplyTo, fact.Assignment, fact.Resolution,
                fact.Policy, fact.CreatedUtc, fact.RecipientRuntimeKey, input, control, source.Position.Sequence,
                spawn ? 0 : target.Position.Sequence);
        }

        private ConversationInput PeerInputHeader(string operation, string fingerprint, AgentMessageAccepted fact,
            ConversationCard sender, ConversationCard root)
        {
            if (root == null) throw new InvalidOperationException("Peer chain root is unavailable.");
            return new ConversationInput { Id = fact.MessageId, OperationId = operation, RequestFingerprint = fingerprint,
                Revision = 1, Delivery = InputDelivery.Queue, Status = ConversationInputStatus.Pending,
                DocumentRuntimeKey = fact.RecipientRuntimeKey, CreatedUtc = fact.CreatedUtc,
                AgentCommunication = new AgentCommunicationContext { MessageId = fact.MessageId, ReplyTo = fact.ReplyTo,
                    ExpectReply = fact.ExpectReply, Assignment = fact.Assignment, Resolution = fact.Resolution,
                    ChainId = fact.ChainId, SenderRunId = fact.RunId, Sender = CardAddress(sender), Root = CardAddress(root), Policy = fact.Policy } };
        }

        private void ValidatePeerInput(SqliteConnection db, JournalRecord record, AgentMessageAccepted fact,
            ConversationCard sender, ConversationCard recipient)
        {
            if (sender.Address == null && recipient.Address == null)
            {
                if (fact.Input != null || fact.Control != null || fact.SenderRevision != null || fact.RecipientRevision != null || fact.RecipientRuntimeKey != null)
                    throw new InvalidDataException("Unbound delivery cannot carry a Store input.");
                return;
            }
            if (sender.Address == null || recipient.Address == null || fact.Input == null || fact.Control == null ||
                string.IsNullOrWhiteSpace(fact.RecipientRuntimeKey) || fact.Input.Recipient != fact.Recipient ||
                fact.Input.MessageId != fact.MessageId || fact.Input.Revision != 1 || fact.Input.Status != ConversationInputStatus.Pending ||
                fact.Input.RunId != null || !SameRunValue(fact.Input.Body, fact.Body) ||
                !SameRunValue(fact.Input.Header, PeerInputHeader(record.Operation, record.Fingerprint, fact, sender, Card(db, fact.Root)?.Item1)))
                throw new InvalidDataException("Peer input does not match its canonical delivery.");
            if (TextChat(db, fact.Sender)?.Position.Sequence != fact.SenderRevision ||
                (TextChat(db, fact.Recipient)?.Position.Sequence ?? (record.Kind == "agent.spawned" ? (long?)0 : null)) != fact.RecipientRevision)
                throw new ChatConcurrencyException("Peer delivery requires both observed chat heads.");
            var prior = QueueControl(db, fact.Recipient);
            if (fact.Control.NativeChat || fact.Control.ChatId != fact.Recipient || (prior?.Fact.PeerControl == true
                ? !SameControlState(fact.Control, prior.Fact) : fact.Control.PeerControl ||
                    fact.Control.Paused != (prior?.Fact.Paused == true) || fact.Control.Reason != prior?.Fact.Reason))
                throw new InvalidOperationException("Peer delivery cannot change queue authority.");
            ValidateControl(db, record.Operation, fact.Control, freshChat: record.Kind == "agent.spawned", preservePause: prior?.Fact.PeerControl == true);
            if (InputState(db, fact.MessageId) != null || ChatEntry(db, fact.Recipient, fact.MessageId) != null)
                throw new InvalidOperationException("Peer input identity is already in use.");
            using (var command = Command(db, "SELECT 1 FROM input_state WHERE stream=$stream AND recipient=$chat AND request_id=$request"))
            {
                command.Parameters.AddWithValue("$chat", fact.Recipient); command.Parameters.AddWithValue("$request", record.Operation);
                if (command.ExecuteScalar() != null) throw new InvalidOperationException("Peer operation already identifies another input.");
            }
        }

        private void ApplyPeerInputRows(SqliteConnection db, SqliteTransaction tx, JournalRecord record,
            JournalCursor position, AgentMessageAccepted fact)
        {
            using (var command = Command(db, "UPDATE chat_state SET head_operation=$operation,last_activity_ticks=$activity WHERE stream=$stream AND (chat=$sender OR chat=$recipient)", tx))
            {
                command.Parameters.AddWithValue("$operation", record.Operation); command.Parameters.AddWithValue("$activity", fact.CreatedUtc.Ticks);
                command.Parameters.AddWithValue("$sender", fact.Sender); command.Parameters.AddWithValue("$recipient", fact.Recipient);
                if (command.ExecuteNonQuery() != 2) throw new InvalidDataException("Peer delivery lost a chat head.");
            }
            using (var command = Command(db, "INSERT INTO input_transition VALUES($stream,$operation,$message,$fingerprint,$segment,$start,$end,$sequence,$hash,$first)", tx))
            { command.Parameters.AddWithValue("$message", fact.MessageId); AddDeliveryPosition(command, record, position); command.ExecuteNonQuery(); }
            ApplyControlRows(db, tx, record, position, fact.Control);
        }

        private void ValidatePeerMessage(SqliteConnection db, string chat, ConversationTextEntry message)
        {
            if (message.Header.AgentCommunication == null)
            {
                if (FindDelivery(db, "message", message.Header.Id) != null)
                    throw new InvalidDataException("Peer message cannot discard its accepted provenance.");
                return;
            }
            var state = InputState(db, message.Header.Id);
            if (state?.Header?.AgentCommunication == null || state.Recipient != chat || state.Status == ConversationInputStatus.Removed ||
                message.Header.Role != "user" || message.Header.ProtocolMessage ||
                !SameRunValue(message.Header.AgentCommunication, state.Header.AgentCommunication) || !SameRunValue(message.Body, state.Body))
                throw new InvalidDataException("Peer message requires its exact accepted input and body.");
        }

        private static bool DeliveryAffects(AgentMessageReceipt receipt, string chat)
        { return receipt?.Fact.Input != null && (receipt.Fact.Sender == chat || receipt.Fact.Recipient == chat); }

        private static SessionEvent ProjectPeerEvent(AgentMessageReceipt receipt, string chat)
        {
            if (!DeliveryAffects(receipt, chat)) throw new InvalidDataException("Delivery is outside this chat.");
            var incoming = receipt.Fact.Recipient == chat;
            return new SessionEvent { SessionId = chat, Sequence = receipt.Position.Sequence, EventId = receipt.Operation,
                CreatedUtc = receipt.Fact.CreatedUtc, Hash = receipt.Position.Hash,
                Type = incoming ? SessionEventTypes.ConversationInputChanged : "agent.accepted",
                RunId = incoming ? null : receipt.Fact.RunId, TurnId = incoming ? null : receipt.Fact.RunId,
                Payload = incoming ? receipt.Fact.Body.ToBlobReference() : null,
                Data = incoming ? JObject.FromObject(ProjectQueueControl(receipt.Fact.Control, receipt.Fact.Input.Header)) : JObject.FromObject(receipt.Fact) };
        }

        private const string ChatEventQuery = "SELECT operation,sequence FROM chat_write WHERE stream=$stream AND chat=$chat AND sequence>$after AND sequence<=$through" +
            " UNION ALL SELECT operation,sequence FROM agent_delivery AS d WHERE stream=$stream AND sender=$chat AND sequence>$after AND sequence<=$through AND NOT EXISTS(SELECT 1 FROM agent_spawn AS s WHERE s.stream=d.stream AND s.operation=d.operation)" +
            " UNION ALL SELECT operation,sequence FROM agent_delivery AS d WHERE stream=$stream AND recipient=$chat AND sequence>$after AND sequence<=$through AND NOT EXISTS(SELECT 1 FROM agent_spawn AS s WHERE s.stream=d.stream AND s.operation=d.operation)" +
            " UNION ALL SELECT operation,sequence FROM agent_spawn WHERE stream=$stream AND sender=$chat AND sequence>$after AND sequence<=$through" +
            " UNION ALL SELECT operation,sequence FROM control_fact WHERE stream=$stream AND sender=$chat AND sequence>$after AND sequence<=$through" +
            " UNION ALL SELECT operation,sequence FROM control_fact WHERE stream=$stream AND chat=$chat AND native=1 AND sequence>$after AND sequence<=$through";

        private JournalCursor ChatPosition(SqliteConnection db, string chat, long sequence)
        {
            string operation;
            using (var command = Command(db, ChatEventQuery))
            {
                command.Parameters.AddWithValue("$chat", chat); command.Parameters.AddWithValue("$after", sequence - 1);
                command.Parameters.AddWithValue("$through", sequence); operation = command.ExecuteScalar() as string;
            }
            if (operation == null) return null;
            var write = FindChatWrite(db, operation);
            if (write?.Fact.Card.ChatId == chat && write.Position.Sequence == sequence) return write.Position;
            var spawn = FindSpawn(db, operation);
            if (spawn?.Fact.Chat != null && spawn.Fact.Sender == chat && spawn.Position.Sequence == sequence) return spawn.Position;
            var peer = FindDelivery(db, "operation", operation);
            if (DeliveryAffects(peer, chat) && peer.Position.Sequence == sequence) return peer.Position;
            var control = FindControl(db, operation);
            return ControlAffects(control, chat) && control.Position.Sequence == sequence ? control.Position : null;
        }
    }
}
