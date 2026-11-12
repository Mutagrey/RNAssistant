using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ConversationJournal
    {
        private bool RequireInputRevision(SqliteConnection db, ConversationMetadata metadata)
        {
            var current = TextChat(db, metadata.Id);
            if (current?.Position.Sequence == metadata.Revision)
            { RequireChatRevision(metadata, current); return true; }
            using (var command = Command(db, "SELECT operation FROM chat_write WHERE stream=$stream AND chat=$chat AND sequence=$through"))
            {
                command.Parameters.AddWithValue("$chat", metadata.Id); command.Parameters.AddWithValue("$through", metadata.Revision);
                var operation = command.ExecuteScalar() as string;
                RequireChatRevision(metadata, operation == null ? null : FindChatWrite(db, operation));
                return false;
            }
        }

        private ConversationInputState PrepareChatInput(SqliteConnection db, ChatSession session,
            ConversationInput input, PayloadRef body)
        {
            var operation = input.Status == ConversationInputStatus.Applied ? ChatMessageOperation(db, session.Id, input.Id) : null;
            var message = operation == null ? null : FindChatWrite(db, operation);
            return new ConversationInputState(session.Id, input.Id, input.Revision, input.Status,
                input.AcceptedRunId, input, body, message?.Position);
        }

        private void ValidateChatInput(SqliteConnection db, JournalRecord record, ConversationChatCommit frame)
        {
            var input = frame.Input;
            if (input == null) return;
            var header = input.Header;
            if (frame.Control == null || frame.Checkpoint != null || input.Recipient != frame.Card.ChatId || header == null ||
                string.IsNullOrWhiteSpace(header.OperationId) || string.IsNullOrWhiteSpace(header.RequestFingerprint) ||
                !Enum.IsDefined(typeof(InputDelivery), header.Delivery)) throw new InvalidDataException("Invalid chat input identity.");
            if ((header.Attachments?.Count ?? 0) != 0 || header.AgentCommunication?.Sender != null) throw TextStoreScope();
            var prior = InputState(db, input.MessageId);
            if (prior == null)
            {
                if (input.Revision != 1 || !(input.Status == ConversationInputStatus.Pending ||
                    input.Status == ConversationInputStatus.Delivering && !string.IsNullOrWhiteSpace(input.RunId)) ||
                    ChatEntry(db, input.Recipient, input.MessageId) != null)
                    throw new InvalidOperationException("A new input requires a fresh identity and accepted revision one.");
                using (var command = Command(db, "SELECT 1 FROM input_state WHERE stream=$stream AND recipient=$chat AND request_id=$request"))
                {
                    command.Parameters.AddWithValue("$chat", input.Recipient); command.Parameters.AddWithValue("$request", header.OperationId);
                    if (command.ExecuteScalar() != null) throw new InvalidOperationException("The input request already has a canonical receipt.");
                }
            }
            else
            {
                if (prior.Header == null || prior.Recipient != input.Recipient || input.Revision != prior.Revision + 1 ||
                    !(prior.Status == ConversationInputStatus.Pending || prior.Status == ConversationInputStatus.Delivering &&
                        (input.Status == ConversationInputStatus.Applied || input.Status == ConversationInputStatus.Pending ||
                         input.Status == ConversationInputStatus.Removed && frame.Control.Paused)))
                    throw new InvalidOperationException("Input transition does not match its accepted state.");
                ConversationInputJournal.RequireSameIdentity(prior.Header, header);
                if (prior.Header.CreatedUtc != header.CreatedUtc ||
                    (prior.Status != ConversationInputStatus.Pending || input.Status != ConversationInputStatus.Pending) &&
                    (prior.Header.Delivery != header.Delivery || !SameRunValue(prior.Body, input.Body)))
                    throw new InvalidOperationException("Only a pending edit may change input text or delivery.");
            }
            if (input.Status == ConversationInputStatus.Applied)
            {
                RequireAppliedInput(input, frame.ExpectedRevision);
                var current = ChatEntry(db, input.Recipient, input.MessageId);
                if (current == null || !SameRunValue(current.Body, input.Body))
                    throw new InvalidOperationException("Applied input requires its persisted user message.");
            }
        }

        private void RequireAppliedInput(ConversationInputState input, long through)
        {
            if (input.AppliedMessage == null || input.AppliedMessage.Sequence > through)
                throw new InvalidDataException("Input has no prior canonical message receipt.");
            var frame = DecodeChatCommit(Journal.Read(input.AppliedMessage));
            var entry = frame.Messages.SingleOrDefault(item => item.Header.Id == input.MessageId);
            if (frame.Card.ChatId != input.Recipient || entry == null || !SameRunValue(entry.Body, input.Body) ||
                entry.Header.Role != "user" || entry.Header.ProtocolMessage ||
                input.RunId != null && entry.Header.RunId != input.RunId ||
                !SameRunValue(entry.Header.AgentCommunication, input.Header.AgentCommunication))
                throw new InvalidDataException("Input receipt does not identify the accepted user message.");
        }

        private void ApplyChatInputRows(SqliteConnection db, SqliteTransaction tx, JournalRecord record,
            JournalCursor position, ConversationInputState input)
        {
            using (var command = Command(db, "INSERT INTO input_transition VALUES($stream,$operation,$message,$fingerprint,$segment,$start,$end,$sequence,$hash,$first)", tx))
            {
                command.Parameters.AddWithValue("$message", input.MessageId); AddDeliveryPosition(command, record, position); command.ExecuteNonQuery();
            }
            SaveInputState(db, tx, record, position, input, input.Revision == 1);
        }

        private ConversationInputState RequireChatInputEvent(SqliteConnection db, string chat, SessionEvent fact)
        {
            var receipt = FindChatWrite(db, fact.EventId);
            var input = receipt?.Fact.Input;
            if (input == null || receipt.Fact.Card.ChatId != chat || receipt.Position.Sequence != fact.Sequence ||
                receipt.Position.Hash != fact.Hash || fact.SessionId != chat ||
                !SameRunValue(fact.Payload, input.Body.ToBlobReference()))
                throw new InvalidDataException("Input event does not identify its canonical body.");
            return input;
        }

        void IEventStore.ValidateInputDelivery(ChatSession session, IReadOnlyList<SessionEvent> facts,
            IReadOnlyList<ConversationInputRecord> records)
        {
            Access((db, cursor) => {
                var current = RequireInputRevision(db, ConversationMetadata.From(session));
                foreach (var record in records.Where(item => item.Change.Input?.Status == ConversationInputStatus.Applied))
                {
                    var input = RequireChatInputEvent(db, session.Id, record.Event);
                    if (input.Status != ConversationInputStatus.Applied) throw new InvalidDataException("Input receipt is not applied.");
                    RequireAppliedInput(input, record.Event.Sequence - 1);
                    var present = ConversationInputJournal.HasPersistedUserMessage(session, input.Header);
                    if (current && (ChatMessageOperation(db, session.Id, input.MessageId) != null) != present)
                        throw new InvalidDataException("Input delivery and the retained message projection disagree.");
                }
                return true;
            }, CancellationToken.None);
        }
    }
}
