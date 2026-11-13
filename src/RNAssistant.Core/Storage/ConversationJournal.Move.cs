using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ConversationJournal
    {
        private static bool IsChatMove(string kind) { return kind == "chat.moved" || kind == "document.moved"; }

        private ChatSession MoveChat(ChatSession session, string host, string key, string title)
        {
            if (session == null) return null;
            RequireTextRoot();
            var address = ConversationMetadata.From(session);
            if (session.Revision == 0)
            {
                // A draft changes only in memory; a stale/deleted identity is never recreated.
                Access((db, cursor) => {
                    if (CardRecord(db, session.Id) != null) throw new ChatConcurrencyException("The draft identity is already retained.");
                    ValidateChatAddress(MoveCard(new ConversationCard(session.Id, session.Title,
                        ConversationRunPolicy.ForSession(session), session.CreatedUtc, address: new ConversationMetadata(
                            session.Id, 0, session.Host, session.DocumentKey, session.WorkspaceId, session.DocumentAuthorityId),
                        previousDocumentKeys: session.PreviousDocumentKeys), host, key, title, session.DocumentPath));
                    return true;
                }, CancellationToken.None);
                ChatSessionNormalizer.RecordDocumentKeyMigration(session, session.DocumentKey, key);
                session.Host = host; session.DocumentKey = key; session.DocumentTitle = title;
                if (session.Context != null) { session.Context.Host = host; session.Context.DocumentKey = key; }
                return session;
            }
            var receipt = MoveChats(address.Host, address.DocumentKey, host, key, title, session.DocumentPath, address);
            var card = receipt.Fact.Card;
            ApplyChatReceipt(session, receipt);
            session.Host = card.Address.Host; session.DocumentKey = card.Address.DocumentKey;
            session.DocumentTitle = card.DocumentTitle; session.DocumentPath = card.DocumentPath;
            session.PreviousDocumentKeys = (card.PreviousDocumentKeys ?? new string[0]).ToList();
            if (session.Context != null) { session.Context.Host = session.Host; session.Context.DocumentKey = session.DocumentKey; }
            return session;
        }

        private ConversationChatReceipt MoveChats(string oldHost, string oldKey, string host, string key,
            string title, string path, ConversationMetadata expected = null)
        {
            if (string.IsNullOrWhiteSpace(oldHost) || string.IsNullOrWhiteSpace(oldKey) ||
                string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Exact document addresses are required.");
            RequireTextRoot();
            using (RuntimeStorageActivity.Acquire(_paths))
            using (AcquireChatScope())
            {
                JournalRecord move = null; string witness = null; Exception failure = null;
                try
                {
                    var found = Access((db, cursor) => {
                        var ids = expected == null ? DocumentChatsForChange(db, oldHost, oldKey) : new List<string> { expected.Id };
                        if (ids.Count == 0) return false;
                        if (expected != null) RequireChatRevision(expected, TextChat(db, expected.Id));
                        witness = ids[0];
                        var fact = new ConversationChatMove(oldHost, oldKey, host, key, title,
                            string.IsNullOrWhiteSpace(path) ? null : path.Trim(), expected?.Id,
                            cursor.Sequence, cursor.Hash, witness, ids.Count, DateTime.UtcNow);
                        move = DeliveryRecord("move:" + cursor.Hash, expected == null ? "document.moved" : "chat.moved",
                            fact, TextPatternEngine.Sha256(JsonConvert.SerializeObject(fact)));
                        var current = ValidateChatMove(db, move);
                        if (expected == null && current.All(item => SameRunValue(item.Fact.Card,
                            MoveCard(item.Fact.Card, host, key, title, fact.DocumentPath)))) { move = null; return false; }
                        var position = Journal.Append(cursor, move.Operation, move.Fingerprint, move.Kind, move.Data);
                        Fault?.Invoke("before_index");
                        ApplyChatMove(db, move, position);
                        return true;
                    }, CancellationToken.None);
                    if (!found) return null;
                }
                catch (Exception error) when (move != null && (error is IOException || error is SqliteException)) { failure = error; }
                // The original immutable operation, not the latest card, proves acceptance.
                Fault?.Invoke("before_move_readback");
                var actual = Access((db, cursor) => {
                    using (var command = Command(db, "SELECT operation,fingerprint,segment,start_offset,end_offset,sequence,hash,first_sequence FROM chat_write WHERE stream=$stream AND operation=$operation AND chat=$chat"))
                    {
                        command.Parameters.AddWithValue("$operation", move.Operation); command.Parameters.AddWithValue("$chat", witness);
                        JournalCursor position; var accepted = LocatedRecord(command, out position);
                        return accepted == null || accepted.Kind != move.Kind || accepted.Fingerprint != move.Fingerprint ? null :
                            new ConversationChatReceipt(ReadChatWrite(db, accepted, position, witness), accepted.Operation, position);
                    }
                }, CancellationToken.None);
                if (actual == null) throw new IOException("Перенос не подтверждён. Обновите чат перед повтором команды.", failure);
                return actual;
            }
        }

        private static ConversationChatMove DecodeChatMove(JournalRecord record)
        {
            var fact = DeliveryData<ConversationChatMove>(record);
            if (!IsChatMove(record.Kind) || fact == null || string.IsNullOrWhiteSpace(fact.Host) ||
                string.IsNullOrWhiteSpace(fact.DocumentKey) || string.IsNullOrWhiteSpace(fact.PreviousHost) ||
                string.IsNullOrWhiteSpace(fact.PreviousDocumentKey) || string.IsNullOrWhiteSpace(fact.WitnessChat) ||
                fact.PreviousSequence < 1 || string.IsNullOrWhiteSpace(fact.PreviousHash) || fact.ChatCount < 1 ||
                fact.MovedUtc.Kind != DateTimeKind.Utc || record.Operation != "move:" + fact.PreviousHash ||
                (record.Kind == "chat.moved" ? fact.ChatId != fact.WitnessChat || fact.ChatCount != 1 : fact.ChatId != null) ||
                record.Fingerprint != TextPatternEngine.Sha256(JsonConvert.SerializeObject(fact)))
                throw new InvalidDataException("Invalid chat address change.");
            return fact;
        }

        private List<ConversationChatReceipt> ValidateChatMove(SqliteConnection db, JournalRecord record)
        {
            if (OperationExists(db, record.Operation)) throw new InvalidDataException("Duplicate chat address change.");
            var fact = DecodeChatMove(record);
            RequireScopeBoundary(db, record, fact.PreviousSequence, fact.PreviousHash);
            var ids = fact.ChatId == null ? DocumentChatsForChange(db, fact.PreviousHost, fact.PreviousDocumentKey) : new List<string> { fact.ChatId };
            if (ids.Count != fact.ChatCount || ids[0] != fact.WitnessChat) throw new ChatConcurrencyException("Relocation set changed.");
            return ids.Select(id => {
                var current = TextChat(db, id);
                if (!MatchesAddress(current, fact.PreviousHost, fact.PreviousDocumentKey)) throw new ChatConcurrencyException("Chat address changed.");
                RequireIdleChat(db, current);
                ValidateChatAddress(MoveCard(current.Fact.Card, fact.Host, fact.DocumentKey, fact.DocumentTitle, fact.DocumentPath));
                return current;
            }).ToList();
        }

        private void ApplyChatMove(SqliteConnection db, JournalRecord record, JournalCursor position)
        {
            var current = ValidateChatMove(db, record); var move = DecodeChatMove(record);
            var activity = current.ToDictionary(item => item.Fact.Card.ChatId, item => Convert.ToInt64(Scalar(db,
                "SELECT last_activity_ticks FROM chat_state WHERE stream=$stream AND chat=$chat", item.Fact.Card.ChatId)));
            using (var tx = db.BeginTransaction())
            {
                RegisterOperation(db, tx, record.Operation, record.Kind);
                foreach (var prior in current)
                {
                    SaveChatMetadata(db, tx, record, position, MovedChat(prior.Fact, move), activity[prior.Fact.Card.ChatId]);
                    Fault?.Invoke("after_move_row");
                }
                if (move.ChatId != null)
                    using (var command = Command(db, "INSERT OR REPLACE INTO chat_selection VALUES($stream,$chat)", tx))
                    { command.Parameters.AddWithValue("$chat", move.ChatId); command.ExecuteNonQuery(); }
                SaveCursor(db, position, tx, record); tx.Commit();
            }
        }

        private ConversationChatCommit ReadChatWrite(SqliteConnection db, JournalRecord record, JournalCursor position, string chat)
        {
            // Walk only address changes since the last metadata commit, never messages.
            // Iteration avoids a stack limit after repeated Save As operations.
            var moves = new Stack<ConversationChatMove>();
            while (IsChatMove(record.Kind))
            {
                var move = DecodeChatMove(record);
                if (string.IsNullOrWhiteSpace(chat) || move.ChatId != null && move.ChatId != chat || position.Sequence != move.PreviousSequence + 1)
                    throw new InvalidDataException("Foreign chat relocation locator.");
                moves.Push(move);
                using (var command = Command(db, "SELECT operation,fingerprint,segment,start_offset,end_offset,sequence,hash,first_sequence FROM chat_write WHERE stream=$stream AND chat=$chat AND sequence<=$before ORDER BY sequence DESC LIMIT 1"))
                {
                    command.Parameters.AddWithValue("$chat", chat); command.Parameters.AddWithValue("$before", move.PreviousSequence);
                    record = LocatedRecord(command, out position) ?? throw new InvalidDataException("Missing relocation predecessor.");
                }
            }
            var fact = DecodeChatCommit(record);
            if (chat != null && fact.Card.ChatId != chat) throw new InvalidDataException("Foreign chat metadata locator.");
            while (moves.Count != 0)
            {
                var move = moves.Pop();
                if (fact.Card.Address?.Host != move.PreviousHost || fact.Card.Address.DocumentKey != move.PreviousDocumentKey)
                    throw new InvalidDataException("Relocation predecessor belongs to another document.");
                fact = MovedChat(fact, move); ValidateChatAddress(fact.Card);
            }
            return fact;
        }

        private static ConversationChatCommit MovedChat(ConversationChatCommit prior, ConversationChatMove move)
        {
            return new ConversationChatCommit(MoveCard(prior.Card, move.Host, move.DocumentKey, move.DocumentTitle, move.DocumentPath),
                move.PreviousSequence, prior.MessageCount, new ConversationTextEntry[0], prior.Context, move.MovedUtc,
                prior.Run, prior.RunState, contextReceipt: prior.ContextReceipt, attachments: prior.Attachments);
        }

        private static ConversationCard MoveCard(ConversationCard prior, string host, string key, string title, string path)
        {
            return new ConversationCard(prior.ChatId, prior.Title, prior.Policy, prior.CreatedUtc, prior.Model,
                new ConversationMetadata(prior.ChatId, 0, host, key, prior.Address.WorkspaceId, prior.Address.DocumentAuthorityId),
                title, path ?? prior.DocumentPath, prior.SelectedModel, prior.ReasoningEnabled, prior.TitleAssignedByAgent,
                prior.AgentCreation, ChatSessionNormalizer.DocumentKeysAfterMove(prior.PreviousDocumentKeys, prior.Address.DocumentKey, key));
        }
    }

    internal sealed class ConversationChatMove
    {
        public string PreviousHost { get; private set; }
        public string PreviousDocumentKey { get; private set; }
        public string Host { get; private set; }
        public string DocumentKey { get; private set; }
        public string DocumentTitle { get; private set; }
        public string DocumentPath { get; private set; }
        public string ChatId { get; private set; }
        public long PreviousSequence { get; private set; }
        public string PreviousHash { get; private set; }
        public string WitnessChat { get; private set; }
        public int ChatCount { get; private set; }
        public DateTime MovedUtc { get; private set; }
        [JsonConstructor]
        internal ConversationChatMove(string previousHost, string previousDocumentKey, string host, string documentKey,
            string documentTitle, string documentPath, string chatId, long previousSequence, string previousHash,
            string witnessChat, int chatCount, DateTime movedUtc)
        {
            PreviousHost = previousHost; PreviousDocumentKey = previousDocumentKey; Host = host; DocumentKey = documentKey;
            DocumentTitle = documentTitle; DocumentPath = documentPath; ChatId = chatId; PreviousSequence = previousSequence;
            PreviousHash = previousHash; WitnessChat = witnessChat; ChatCount = chatCount; MovedUtc = movedUtc;
        }
    }
}
