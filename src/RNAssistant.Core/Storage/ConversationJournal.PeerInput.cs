using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ConversationJournal
    {
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
            AgentMessageAccepted fact, ConversationCard sender, ConversationCard recipient)
        {
            if (sender.Address == null && recipient.Address == null) return fact;
            if (sender.Address == null || recipient.Address == null || string.IsNullOrWhiteSpace(fact.RecipientRuntimeKey))
                throw new InvalidOperationException("Peer input requires two integrated chats and the exact recipient binding.");
            var source = TextChat(db, sender.ChatId); var target = TextChat(db, recipient.ChatId);
            var prior = QueueControl(db, recipient.ChatId);
            if (prior?.Fact.PeerControl == true) throw TextStoreScope();
            var header = PeerInputHeader(operation, fingerprint, fact, sender, Card(db, fact.Root)?.Item1);
            var input = new ConversationInputState(recipient.ChatId, fact.MessageId, 1, ConversationInputStatus.Pending, null, header, fact.Body);
            var control = new ConversationQueueControl(recipient.ChatId, (prior?.Fact.Revision ?? 0) + 1, prior?.Position.Hash,
                prior?.Fact.Paused == true ? QueueControlAction.UserPause : QueueControlAction.UserResume,
                null, null, null, null, prior?.Fact.Reason);
            return new AgentMessageAccepted(fact.Sender, fact.Recipient, fact.RunId, fact.CallId, fact.Root, fact.ChainId,
                fact.ParticipantLimit, fact.MessageId, fact.Body, fact.ExpectReply, fact.ReplyTo, fact.Assignment, fact.Resolution,
                fact.Policy, fact.CreatedUtc, fact.RecipientRuntimeKey, input, control, source.Position.Sequence, target.Position.Sequence);
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
                TextChat(db, fact.Recipient)?.Position.Sequence != fact.RecipientRevision)
                throw new ChatConcurrencyException("Peer delivery requires both observed chat heads.");
            var prior = QueueControl(db, fact.Recipient);
            if (prior?.Fact.PeerControl == true || fact.Control.PeerControl || fact.Control.ChatId != fact.Recipient ||
                fact.Control.Paused != (prior?.Fact.Paused == true) || fact.Control.Reason != prior?.Fact.Reason)
                throw new InvalidOperationException("Peer delivery cannot change queue authority.");
            ValidateControl(db, record.Operation, fact.Control);
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
                Data = incoming ? JObject.FromObject(new ConversationInputEvent { Input = receipt.Fact.Input.Header,
                    Paused = receipt.Fact.Control.Paused, PauseReason = receipt.Fact.Control.Reason }) : JObject.FromObject(receipt.Fact) };
        }

        private const string ChatEventQuery = "SELECT operation,sequence FROM chat_write WHERE stream=$stream AND chat=$chat AND sequence>$after AND sequence<=$through" +
            " UNION ALL SELECT operation,sequence FROM agent_delivery WHERE stream=$stream AND sender=$chat AND sequence>$after AND sequence<=$through" +
            " UNION ALL SELECT operation,sequence FROM agent_delivery WHERE stream=$stream AND recipient=$chat AND sequence>$after AND sequence<=$through";

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
            if (write != null) return write.Fact.Card.ChatId == chat && write.Position.Sequence == sequence ? write.Position : null;
            var peer = FindDelivery(db, "operation", operation);
            return DeliveryAffects(peer, chat) && peer.Position.Sequence == sequence ? peer.Position : null;
        }
    }
}
