using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ConversationJournal
    {
        // A rewrite changes visibility, never accepted calls, results or authority.
        // Its potentially large membership list lives in CAS; page locators still
        // point directly to the original bounded message frames.
        private ConversationHistoryRewrite ReadHistory(PayloadRef body)
        {
            var history = JsonConvert.DeserializeObject<ConversationHistoryRewrite>(ReadChatBody(body));
            if (history?.Entries == null || history.Removed == null ||
                history.Entries.Any(item => item == null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Operation)) ||
                history.Entries.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != history.Entries.Count ||
                history.Removed.Any(string.IsNullOrWhiteSpace) || history.Removed.Distinct(StringComparer.Ordinal).Count() != history.Removed.Count ||
                history.Removed.Intersect(history.Entries.Select(item => item.Id), StringComparer.Ordinal).Any())
                throw new InvalidDataException("Invalid history membership manifest.");
            return history;
        }

        private List<ConversationHistoryEntry> VisibleHistory(SqliteConnection db, string chat)
        {
            var entries = new List<ConversationHistoryEntry>();
            using (var command = Command(db, "SELECT id,operation FROM chat_message WHERE stream=$stream AND chat=$chat AND ordinal>=0 ORDER BY ordinal"))
            {
                command.Parameters.AddWithValue("$chat", chat);
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) entries.Add(new ConversationHistoryEntry(reader.GetString(0), reader.GetString(1)));
            }
            return entries;
        }

        private PayloadRef PrepareHistory(SqliteConnection db, ChatSession session, ConversationCommit commit,
            ConversationChatReceipt retry, ref ConversationTextEntry[] messages)
        {
            if (!commit.RewritesHistory) return null;
            if (retry != null)
            {
                if (retry.Fact.History == null) throw new InvalidOperationException("Operation is not a history rewrite.");
                var saved = ReadHistory(retry.Fact.History);
                if (!saved.Entries.Select(item => item.Id).SequenceEqual(messages.Select(item => item.Header.Id)) ||
                    !new HashSet<string>(saved.Removed, StringComparer.Ordinal).SetEquals(commit.RemovedMessageIds))
                    throw new InvalidOperationException("History operation was reused with different membership.");
                var writes = saved.Entries.Select(item => item.Operation).Distinct(StringComparer.Ordinal)
                    .ToDictionary(operation => operation, operation => operation == retry.Operation ? retry : FindChatWrite(db, operation));
                for (var i = 0; i < messages.Length; i++)
                {
                    var entry = saved.Entries[i]; var write = writes[entry.Operation];
                    RequireHistoryOwner(db, session.Id, write);
                    var original = ProjectInheritedText(write.Fact.Messages.Single(item => item.Header.Id == entry.Id), write.Fact.Card.Address, session.Id);
                    if (!(write.Fact.Card.ChatId == session.Id ? SameRunValue(messages[i], original) : SameInheritedText(original, messages[i])))
                        throw new InvalidOperationException("History operation was reused with different text.");
                }
                messages = retry.Fact.Messages.ToArray();
                return retry.Fact.History;
            }
            var current = TextChat(db, session.Id);
            if (current == null || current.Position.Sequence != commit.JournalRevision)
                throw new ChatConcurrencyException("History rewrite requires its exact existing chat revision.");
            RequireIdleChat(db, current);
            var previous = VisibleHistory(db, session.Id);
            var byId = previous.ToDictionary(item => item.Id, StringComparer.Ordinal);
            var retained = new HashSet<string>(messages.Select(item => item.Header.Id), StringComparer.Ordinal);
            var removed = previous.Where(item => !retained.Contains(item.Id)).Select(item => item.Id).ToArray();
            if (!new HashSet<string>(removed, StringComparer.Ordinal).SetEquals(commit.RemovedMessageIds))
                throw new InvalidOperationException("History rewrite omitted its exact removals.");
            var writesByOperation = new Dictionary<string, ConversationChatReceipt>(StringComparer.Ordinal);
            var changed = new List<ConversationTextEntry>();
            var entries = new List<ConversationHistoryEntry>();
            foreach (var message in messages)
            {
                ConversationHistoryEntry prior;
                if (byId.TryGetValue(message.Header.Id, out prior))
                {
                    ConversationChatReceipt write;
                    if (!writesByOperation.TryGetValue(prior.Operation, out write))
                        writesByOperation.Add(prior.Operation, write = FindChatWrite(db, prior.Operation));
                    RequireHistoryOwner(db, session.Id, write);
                    var original = ProjectInheritedText(write.Fact.Messages.Single(item => item.Header.Id == prior.Id), write.Fact.Card.Address, session.Id);
                    if (write.Fact.Card.ChatId == session.Id ? SameRunValue(message, original) : SameInheritedText(original, message))
                    { entries.Add(prior); continue; }
                }
                changed.Add(message); entries.Add(new ConversationHistoryEntry(message.Header.Id, commit.OperationId));
            }
            messages = changed.ToArray();
            return StoreChatJson(new ConversationHistoryRewrite(entries, removed));
        }

        private ConversationHistoryRewrite ValidateHistory(SqliteConnection db, JournalRecord record,
            ConversationChatCommit fact, ConversationChatReceipt current)
        {
            if (current == null || fact.Checkpoint != null || fact.Tools != null || fact.Control != null || fact.Input != null || fact.ModelEvent != null)
                throw new InvalidOperationException("History membership is separate from runtime acceptance.");
            RequireIdleChat(db, current);
            var history = ReadHistory(fact.History);
            var previous = VisibleHistory(db, fact.Card.ChatId);
            var retained = new HashSet<string>(history.Entries.Select(item => item.Id), StringComparer.Ordinal);
            if (fact.MessageCount != history.Entries.Count || !new HashSet<string>(history.Removed, StringComparer.Ordinal)
                .SetEquals(previous.Where(item => !retained.Contains(item.Id)).Select(item => item.Id)))
                throw new InvalidDataException("History membership/count does not match its exact delta.");
            var indexed = previous.Select((entry, ordinal) => new { entry, ordinal }).ToDictionary(item => item.entry.Id, StringComparer.Ordinal);
            var changes = fact.Messages.ToDictionary(item => item.Header.Id, StringComparer.Ordinal);
            var lastOrdinal = -1; var added = false;
            foreach (var entry in history.Entries)
            {
                var existing = indexed.ContainsKey(entry.Id) ? indexed[entry.Id] : null;
                if (existing != null)
                {
                    if (added || existing.ordinal <= lastOrdinal) throw new InvalidDataException("History cannot reorder retained occurrences.");
                    lastOrdinal = existing.ordinal;
                }
                else
                {
                    if (added || ChatMessageOperation(db, fact.Card.ChatId, entry.Id, includeHidden: true) != null)
                        throw new InvalidDataException("History cannot restore removed occurrences or append a batch.");
                    added = true; // Workspace edit appends one new user occurrence.
                }
                if (entry.Operation == record.Operation)
                {
                    ConversationTextEntry changed;
                    if (!changes.TryGetValue(entry.Id, out changed)) throw new InvalidDataException("History changed entry has no body.");
                    ValidateHistoryText(existing == null ? null : ChatEntry(db, fact.Card.ChatId, entry.Id), changed);
                    changes.Remove(entry.Id);
                }
                else if (existing == null || existing.entry.Operation != entry.Operation)
                    throw new InvalidDataException("History cannot replace an accepted occurrence's locator.");
            }
            if (changes.Count != 0) throw new InvalidDataException("History contains unselected text writes.");
            return history;
        }

        private static void ValidateHistoryText(ConversationTextEntry prior, ConversationTextEntry next)
        {
            var message = next.Header;
            if (message.Role != "user" || message.ProtocolMessage || message.RuntimeProjection || message.Activity != null ||
                message.ToolCallId != null || message.AcceptedCallOrigin != null || message.AgentCommunication != null ||
                prior != null && (prior.Header.Role != "user" || prior.Header.ProtocolMessage || prior.Header.AgentCommunication != null ||
                    prior.Header.CreatedUtc != message.CreatedUtc || !SameRunValue(prior.Header.Attachments, message.Attachments) || !SameRunValue(prior.Header.ResourceRefs, message.ResourceRefs) ||
                    !SameRunValue(prior.Header.ResourceModelReferences, message.ResourceModelReferences)))
                throw new InvalidOperationException("History can edit user text, never runtime evidence or peer provenance.");
        }

        private void ApplyHistory(SqliteConnection db, SqliteTransaction tx, string chat, ConversationHistoryRewrite history)
        {
            using (var command = Command(db, "UPDATE chat_message SET ordinal=NULL WHERE stream=$stream AND chat=$chat", tx))
            { command.Parameters.AddWithValue("$chat", chat); command.ExecuteNonQuery(); }
            for (var ordinal = 0; ordinal < history.Entries.Count; ordinal++)
            {
                var entry = history.Entries[ordinal];
                using (var command = Command(db, "INSERT INTO chat_message VALUES($stream,$chat,$id,$ordinal,$operation) ON CONFLICT(stream,chat,id) DO UPDATE SET ordinal=$ordinal,operation=$operation", tx))
                {
                    command.Parameters.AddWithValue("$chat", chat); command.Parameters.AddWithValue("$id", entry.Id);
                    command.Parameters.AddWithValue("$ordinal", ordinal); command.Parameters.AddWithValue("$operation", entry.Operation); command.ExecuteNonQuery();
                }
            }
            Fault?.Invoke("after_history_index");
        }
    }

    internal sealed class ConversationHistoryEntry
    {
        public string Id { get; private set; }
        public string Operation { get; private set; }
        [JsonConstructor] internal ConversationHistoryEntry(string id, string operation) { Id = id; Operation = operation; }
    }

    internal sealed class ConversationHistoryRewrite
    {
        public IReadOnlyList<ConversationHistoryEntry> Entries { get; private set; }
        public IReadOnlyList<string> Removed { get; private set; }
        [JsonConstructor] internal ConversationHistoryRewrite(IReadOnlyList<ConversationHistoryEntry> entries, IReadOnlyList<string> removed)
        { Entries = entries; Removed = removed; }
    }
}
