using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ConversationJournal
    {
        private bool DeleteDocument(string host, string key)
        {
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(key)) return false;
            RequireTextRoot();
            using (RuntimeStorageActivity.Acquire(_paths))
            using (AcquireChatScope())
            {
                JournalRecord deletion = null; string witness = null; Exception failure = null;
                try
                {
                    var found = Access((db, cursor) => {
                        var ids = DocumentChatsForChange(db, host, key);
                        if (ids.Count == 0) return false;
                        witness = ids[0];
                        var fact = new ConversationDocumentDeletion(host, key, cursor.Sequence, cursor.Hash, witness, ids.Count, DateTime.UtcNow);
                        deletion = DeliveryRecord("document-delete:" + cursor.Hash, "document.deleted", fact,
                            TextPatternEngine.Sha256(JsonConvert.SerializeObject(fact)));
                        DecodeDocumentDeletion(deletion);
                        // All guards ran under this append admission before one frame
                        // is written. No per-chat durable deletion or partial batch.
                        var position = Journal.Append(cursor, deletion.Operation, deletion.Fingerprint, deletion.Kind, deletion.Data);
                        Fault?.Invoke("before_index");
                        ApplyChatFact(db, deletion, position);
                        return true;
                    }, CancellationToken.None);
                    if (!found) return false;
                }
                catch (Exception error) when (deletion != null && (error is IOException || error is SqliteException))
                { failure = error; }
                RequireDeletionReceipt(deletion, witness, failure);
                return true;
            }
        }

        private List<string> DocumentChatsForChange(SqliteConnection db, string host, string key)
        {
            var live = new List<string>();
            using (var command = Command(db, "SELECT chat FROM chat_card WHERE stream=$stream AND alias IS NOT NULL ORDER BY chat"))
            using (var reader = command.ExecuteReader()) while (reader.Read()) live.Add(reader.GetString(0));
            var selected = new List<string>();
            foreach (var id in live)
            {
                var current = TextChat(db, id);
                if (!MatchesAddress(current, host, key)) continue;
                RequireIdleChat(db, current);
                selected.Add(id);
            }
            return selected;
        }

        private static ConversationDocumentDeletion DecodeDocumentDeletion(JournalRecord record)
        {
            var fact = DeliveryData<ConversationDocumentDeletion>(record);
            if (record.Kind != "document.deleted" || fact == null || string.IsNullOrWhiteSpace(fact.Host) ||
                string.IsNullOrWhiteSpace(fact.DocumentKey) || string.IsNullOrWhiteSpace(fact.WitnessChat) ||
                fact.PreviousSequence < 1 || string.IsNullOrWhiteSpace(fact.PreviousHash) || fact.ChatCount < 1 ||
                fact.DeletedUtc == default(DateTime) || record.Operation != "document-delete:" + fact.PreviousHash ||
                record.Fingerprint != TextPatternEngine.Sha256(JsonConvert.SerializeObject(fact)))
                throw new InvalidDataException("Invalid document deletion.");
            return fact;
        }

        private List<string> ValidateDocumentDeletion(SqliteConnection db, JournalRecord record)
        {
            if (OperationExists(db, record.Operation)) throw new InvalidDataException("Duplicate document deletion.");
            var fact = DecodeDocumentDeletion(record);
            RequireScopeBoundary(db, record, fact.PreviousSequence, fact.PreviousHash);
            var ids = DocumentChatsForChange(db, fact.Host, fact.DocumentKey);
            if (ids.Count != fact.ChatCount || ids[0] != fact.WitnessChat)
                throw new ChatConcurrencyException("Document deletion set changed.");
            return ids;
        }

        private void ApplyDocumentDeletion(SqliteConnection db, JournalRecord record, JournalCursor position)
        {
            var ids = ValidateDocumentDeletion(db, record);
            using (var tx = db.BeginTransaction())
            {
                RegisterOperation(db, tx, record.Operation, record.Kind);
                foreach (var id in ids)
                {
                    RemoveLiveChat(db, tx, record, position, id);
                    Fault?.Invoke("after_document_delete_row");
                }
                SaveCursor(db, position, tx, record); tx.Commit();
            }
        }

        private void ValidateDeletedDocumentCard(SqliteConnection db, string chat, JournalRecord deletion)
        {
            var fact = DecodeDocumentDeletion(deletion);
            // The retained last metadata write proves inclusion in the predicate
            // at the pinned boundary, without a full target list in the frame.
            using (var command = Command(db, "SELECT operation,fingerprint,segment,start_offset,end_offset,sequence,hash,first_sequence FROM chat_write WHERE stream=$stream AND chat=$chat ORDER BY sequence DESC LIMIT 1"))
            {
                command.Parameters.AddWithValue("$chat", chat);
                JournalCursor position; var prior = LocatedRecord(command, out position);
                var card = prior == null ? null : ReadChatWrite(db, prior, position, chat).Card;
                if (card?.ChatId != chat || position.Sequence > fact.PreviousSequence ||
                    card.Address?.Host != fact.Host || card.Address.DocumentKey != fact.DocumentKey)
                    throw new InvalidDataException("Foreign document deletion locator.");
            }
        }
    }

    internal sealed class ConversationDocumentDeletion
    {
        public string Host { get; private set; }
        public string DocumentKey { get; private set; }
        public long PreviousSequence { get; private set; }
        public string PreviousHash { get; private set; }
        public string WitnessChat { get; private set; }
        public int ChatCount { get; private set; }
        public DateTime DeletedUtc { get; private set; }

        [JsonConstructor]
        public ConversationDocumentDeletion(string host, string documentKey, long previousSequence, string previousHash,
            string witnessChat, int chatCount, DateTime deletedUtc)
        {
            Host = host; DocumentKey = documentKey; PreviousSequence = previousSequence; PreviousHash = previousHash;
            WitnessChat = witnessChat; ChatCount = chatCount; DeletedUtc = deletedUtc;
        }
    }
}
