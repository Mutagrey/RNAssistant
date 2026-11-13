using System;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ConversationJournal
    {
        private bool DeleteChat(string host, string key, string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            RequireTextRoot();
            using (RuntimeStorageActivity.Acquire(_paths))
            // Same lease as group membership. Do not delete the shared journal or CAS.
            using (AcquireDeletionScope())
            {
                JournalRecord deletion = null; Exception failure = null;
                try
                {
                    var found = Access((db, cursor) => {
                        var current = TextChat(db, id);
                        if (!MatchesAddress(current, host, key)) return false;
                        var fact = new ConversationChatDeletion(ChatMetadata(current), current.Position.Hash, DateTime.UtcNow);
                        deletion = DeliveryRecord("chat-delete:" + id, "chat.deleted", fact,
                            TextPatternEngine.Sha256(JsonConvert.SerializeObject(fact)));
                        ValidateChatFact(db, deletion);
                        var position = Journal.Append(cursor, deletion.Operation, deletion.Fingerprint, deletion.Kind, deletion.Data);
                        Fault?.Invoke("before_index");
                        ApplyChatFact(db, deletion, position);
                        return true;
                    }, CancellationToken.None);
                    if (!found) return false;
                }
                catch (Exception error) when (deletion != null && (error is IOException || error is SqliteException))
                { failure = error; }
                RequireDeletionReceipt(deletion, id, failure);
                return true;
            }
        }

        private IDisposable AcquireDeletionScope()
        {
            var separator = _scope.IndexOf(':');
            return new ResourceJournal(_paths, _protector).AcquireScope(
                new ResourceAuthorityScopeId(_scope.Substring(0, separator), _scope.Substring(separator + 1)));
        }

        private static bool IsChatDeletion(string kind) { return kind == "chat.deleted" || kind == "document.deleted"; }

        private void RequireDeletionReceipt(JournalRecord deletion, string witness, Exception failure)
        {
            // Absence in a projection cannot prove acceptance. Never redispatch.
            Fault?.Invoke("before_delete_readback");
            var actual = Access((db, cursor) => CardRecord(db, witness), CancellationToken.None);
            if (actual?.Kind != deletion.Kind || actual.Operation != deletion.Operation || actual.Fingerprint != deletion.Fingerprint)
                throw new IOException("Удаление не подтверждено. Обновите список перед повтором команды.", failure);
        }

        private static ConversationChatDeletion DecodeChatDeletion(JournalRecord record)
        {
            var fact = DeliveryData<ConversationChatDeletion>(record);
            if (record.Kind != "chat.deleted" || fact?.Address == null || string.IsNullOrWhiteSpace(fact.Address.Id) ||
                fact.Address.Revision < 1 || string.IsNullOrWhiteSpace(fact.PreviousHash) || fact.DeletedUtc == default(DateTime) ||
                record.Operation != "chat-delete:" + fact.Address.Id ||
                record.Fingerprint != TextPatternEngine.Sha256(JsonConvert.SerializeObject(fact)))
                throw new InvalidDataException("Invalid chat deletion.");
            return fact;
        }

        private void ValidateChatDeletion(SqliteConnection db, JournalRecord record)
        {
            var fact = DecodeChatDeletion(record);
            var current = TextChat(db, fact.Address.Id);
            RequireChatRevision(fact.Address, current);
            if (current.Position.Hash != fact.PreviousHash)
                throw new ChatConcurrencyException("Chat changed before deletion.");
            RequireIdleForDeletion(db, current);
        }

        private void RequireIdleForDeletion(SqliteConnection db, ConversationChatReceipt current)
        {
            var run = CurrentRun(db, current.Fact.Card.ChatId);
            if (current.Fact.Run != null && (run == null || run.Fact.RunId != current.Fact.Run.RunId || !TerminalRun(run.Fact.Lifecycle)))
                throw new InvalidOperationException("Stop or resolve the active run before deleting this chat.");
        }

        private void ApplyChatDeletion(SqliteConnection db, SqliteTransaction tx, JournalRecord record, JournalCursor position)
        {
            RemoveLiveChat(db, tx, record, position, DecodeChatDeletion(record).Address.Id);
        }

        private void RemoveLiveChat(SqliteConnection db, SqliteTransaction tx, JournalRecord record, JournalCursor position, string id)
        {
            // Reuse the card locator as a durable deletion receipt. The retained
            // identity prevents every creation path from resurrecting this ID.
            using (var command = Command(db, "UPDATE chat_card SET alias=NULL,operation=$operation,fingerprint=$fingerprint,segment=$segment,start_offset=$start,end_offset=$end,sequence=$sequence,hash=$hash,first_sequence=$first WHERE stream=$stream AND chat=$chat", tx))
            {
                command.Parameters.AddWithValue("$chat", id); AddDeliveryPosition(command, record, position);
                if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("Deleted chat has no card.");
            }
            using (var command = Command(db, "DELETE FROM chat_state WHERE stream=$stream AND chat=$chat; DELETE FROM chat_selection WHERE stream=$stream AND chat=$chat", tx))
            { command.Parameters.AddWithValue("$chat", id); command.ExecuteNonQuery(); }
        }
    }

    internal sealed class ConversationChatDeletion
    {
        public ConversationMetadata Address { get; private set; }
        public string PreviousHash { get; private set; }
        public DateTime DeletedUtc { get; private set; }

        [JsonConstructor]
        public ConversationChatDeletion(ConversationMetadata address, string previousHash, DateTime deletedUtc)
        { Address = address; PreviousHash = previousHash; DeletedUtc = deletedUtc; }
    }
}
