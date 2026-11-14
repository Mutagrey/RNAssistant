using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ConversationJournal
    {
        private static void RequireForkScope(ChatSession session)
        {
            // Shared resources retain their published owner; local snapshots inherit
            // original locators. Other local graphs still need their publication boundary.
            var metadata = ConversationMetadata.From(session);
            if (session.Artifacts.Any(item => !IsPublishedSnapshot(metadata, ChatResourceUri.CreateArtifactRevision(session, item)) &&
                    (item.Kind != ChatArtifactKinds.Compaction && item.Kind != ChatArtifactKinds.TaskList || !IsLocalSnapshot(metadata, ChatResourceUri.CreateArtifactRevision(session, item)))) ||
                session.ResourceCopies.Count != 0 ||
                session.Context?.Notes.Any(note => note.Role == ContextNoteRole.SuppliedData) == true)
                throw TextStoreScope();
        }

        private ConversationFork PrepareFork(SqliteConnection db, ChatSession session, ConversationCommit commit,
            ConversationChatReceipt retry, ref ConversationTextEntry[] messages, out PayloadRef history)
        {
            history = null;
            var current = retry ?? TextChat(db, session.Id);
            if (commit.JournalRevision != 0 || session.ParentSessionId == null)
            {
                var retained = current?.Fact.Fork;
                if (session.ParentSessionId != retained?.Parent.Id || session.ParentSessionRevision != retained?.Parent.Revision ||
                    session.ForkedThroughMessageId != retained?.ThroughMessageId)
                    throw new InvalidOperationException("A commit cannot replace fork provenance.");
                return retained;
            }
            RequireForkScope(session);
            if (commit is ConversationRunCommit || commit.RemovedMessageIds.Count != 0 ||
                session.LastRun != null || session.LastContextReceipt != null || session.AgentCreation != null)
                throw new InvalidOperationException("Fork creation cannot resume a run, confirmation or peer assignment.");
            ConversationFork fork;
            if (retry != null)
            {
                fork = retry.Fact.Fork ?? throw new InvalidOperationException("Operation is not a fork.");
                if (session.ParentSessionId != fork.Parent.Id || session.ParentSessionRevision != fork.Parent.Revision ||
                    session.ForkedThroughMessageId != fork.ThroughMessageId)
                    throw new InvalidOperationException("Fork operation was reused with another source.");
                history = retry.Fact.History;
            }
            else
            {
                var source = TextChat(db, session.ParentSessionId);
                if (source == null || source.Position.Sequence != session.ParentSessionRevision)
                    throw new ChatConcurrencyException("Fork requires the exact source revision.");
                RequireIdleChat(db, source);
                fork = new ConversationFork(ChatMetadata(source), source.Operation, source.Position.Hash,
                    session.ForkedThroughMessageId, Convert.ToInt64(Scalar(db, "SELECT next_t FROM chat_sequence WHERE stream=$stream AND chat=$chat", source.Fact.Card.ChatId) ?? 1L),
                    Convert.ToInt64(Scalar(db, "SELECT next_e FROM chat_sequence WHERE stream=$stream AND chat=$chat", source.Fact.Card.ChatId) ?? 1L));
                history = StoreChatJson(new ConversationHistoryRewrite(ForkPrefix(db, fork), new string[0]));
            }
            var membership = ReadHistory(history);
            if (!membership.Entries.Select(item => item.Id).SequenceEqual(messages.Select(item => item.Header.Id)))
                throw new InvalidOperationException("Fork must retain the exact selected prefix.");
            var writes = membership.Entries.Select(item => item.Operation).Distinct(StringComparer.Ordinal)
                .ToDictionary(operation => operation, operation => FindChatWrite(db, operation));
            for (var index = 0; index < messages.Length; index++)
            {
                var entry = membership.Entries[index]; var write = writes[entry.Operation];
                if (write == null || !InheritedFact(db, fork, write.Fact.Card.ChatId, write.Position.Sequence))
                    throw new InvalidDataException("Foreign fork history locator.");
                var original = write.Fact.Messages.Single(item => item.Header.Id == entry.Id);
                RequireForkMessage(write.Fact.Card.Address, original);
                if (!SameInheritedText(ProjectInheritedText(original, write.Fact.Card.Address, session.Id), messages[index]))
                    throw new InvalidOperationException("Fork cannot rewrite source evidence.");
            }
            messages = new ConversationTextEntry[0];
            return fork;
        }

        private static void RequireForkMessage(ConversationMetadata owner, ConversationTextEntry entry)
        {
            if (entry.Header.Attachments.Count != 0 && owner.DocumentAuthorityId == null ||
                entry.Header.HtmlWorkspaceCheckpoint != null && !IsHtmlSnapshot(owner, entry.Header.HtmlWorkspaceCheckpoint) ||
                entry.Header.ResourceRefs.Any(reference => {
                    string scope, id; int revision;
                    return ChatResourceUri.TryParseArtifactRevision(reference, out scope, out id, out revision) &&
                        !IsPublishedSnapshot(owner, ChatResourceUri.ArtifactSnapshot(reference)) && !IsLocalSnapshot(owner, reference);
                })) throw TextStoreScope();
        }

        private static ConversationTextEntry ProjectInheritedText(ConversationTextEntry original, ConversationMetadata owner, string chat)
        {
            if (owner.Id == chat || !original.Header.ResourceRefs.Any(item => IsLocalSnapshot(owner, item))) return original;
            var header = original.Header.CopyForContext();
            // Only presentation links follow the child. Claims, payloads and tool
            // evidence keep their canonical source ownership and original locators.
            header.ResourceRefs = header.ResourceRefs.Select(item => IsLocalSnapshot(owner, item)
                ? ChatResourceUri.RebaseArtifactRevision(item, chat) : item).ToList();
            return new ConversationTextEntry(header, original.Body, original.Reasoning, original.Analysis);
        }

        private static bool SameInheritedText(ConversationTextEntry original, ConversationTextEntry proposed)
        {
            // Prefix cuts can exclude incomplete exchanges in the disposable model
            // view. Retain the original locator even during a later child rewrite.
            if (ToolProtocolMessages.IsExchange(original.Header) && proposed.Header.ExcludeFromModelContext &&
                !original.Header.ExcludeFromModelContext) proposed.Header.ExcludeFromModelContext = false;
            return SameRunValue(original, proposed);
        }

        private List<ConversationHistoryEntry> ForkPrefix(SqliteConnection db, ConversationFork fork)
        {
            var source = VisibleHistory(db, fork.Parent.Id);
            if (fork.ThroughMessageId == null) return new List<ConversationHistoryEntry>();
            var boundary = source.FindIndex(item => item.Id == fork.ThroughMessageId);
            if (boundary < 0) throw new InvalidOperationException("Fork boundary is not visible in its source.");
            return source.Take(boundary + 1).ToList();
        }

        private ConversationHistoryRewrite ValidateFork(SqliteConnection db, ConversationChatCommit fact)
        {
            var fork = fact.Fork; var source = TextChat(db, fork.Parent.Id);
            RequireChatRevision(fork.Parent, source); RequireIdleChat(db, source);
            if (fork.Parent.Id == fact.Card.ChatId || source.Operation != fork.ParentOperation || source.Position.Hash != fork.ParentHash ||
                fork.Parent.Host != fact.Card.Address.Host || fork.Parent.DocumentKey != fact.Card.Address.DocumentKey ||
                fork.Parent.WorkspaceId != fact.Card.Address.WorkspaceId || fork.Parent.DocumentAuthorityId != fact.Card.Address.DocumentAuthorityId ||
                fact.ExpectedRevision != 0 || fact.History == null || fact.Messages.Count != 0 || fact.Run != null ||
                fact.Checkpoint != null || fact.Tools != null || fact.Input != null || fact.Control != null || fact.ModelEvent != null ||
                fact.ContextReceipt != null || fact.Card.AgentCreation != null || (fact.Snapshots?.Count ?? 0) != 0 ||
                fork.NextCall != Convert.ToInt64(Scalar(db, "SELECT next_t FROM chat_sequence WHERE stream=$stream AND chat=$chat", fork.Parent.Id) ?? 1L) ||
                fork.NextEvidence != Convert.ToInt64(Scalar(db, "SELECT next_e FROM chat_sequence WHERE stream=$stream AND chat=$chat", fork.Parent.Id) ?? 1L))
                throw new InvalidDataException("Invalid atomic fork provenance.");
            var resources = ReadResourceSet(fact.Resources, fact.Card.Address);
            var retained = ReadResourceSet(source.Fact.Resources, fork.Parent);
            var membership = new HashSet<string>(resources.References.Select(item => item.Uri), StringComparer.Ordinal);
            var parentMembership = new HashSet<string>(retained.References.Select(item => item.Uri), StringComparer.Ordinal);
            if (resources.References.Any(item => !IsPublishedSnapshot(fact.Card.Address, item) &&
                    (!IsLocalSnapshot(fact.Card.Address, item) || !parentMembership.Contains(ChatResourceUri.RebaseArtifactRevision(item, fork.Parent.Id).Uri))) ||
                !SameRunValue(resources.ActiveHtml, retained.ActiveHtml) || !SameRunValue(resources.ActivePlan, retained.ActivePlan) ||
                !SameRunValue(resources.Links, retained.Links) ||
                retained.References.Where(item => IsPublishedSnapshot(fork.Parent, item))
                    .Any(item => !membership.Contains(item.Uri)))
                throw new InvalidDataException("Fork must preserve shared membership and inherit only retained local snapshots.");
            var history = ReadHistory(fact.History); var expected = ForkPrefix(db, fork);
            if (history.Removed.Count != 0 || fact.MessageCount != expected.Count || !SameRunValue(history.Entries, expected))
                throw new InvalidDataException("Fork membership disagrees with its frozen prefix.");
            var inherited = new Dictionary<string, ChatMessage>(StringComparer.Ordinal);
            foreach (var group in expected.GroupBy(item => item.Operation))
            {
                var write = FindChatWrite(db, group.Key); RequireHistoryOwner(db, fork.Parent.Id, write);
                foreach (var entry in group)
                {
                    var original = write.Fact.Messages.Single(item => item.Header.Id == entry.Id);
                    RequireForkMessage(write.Fact.Card.Address, original);
                    var header = ProjectInheritedText(original, write.Fact.Card.Address, fact.Card.ChatId).Header;
                    inherited.Add(entry.Id, header);
                    if (header.ResourceRefs
                        .Any(item => IsLocalSnapshot(fact.Card.Address, item) && !membership.Contains(item.Uri)))
                        throw new InvalidDataException("Fork omitted an inherited local snapshot reference.");
                }
            }
            var tasks = resources.References.Where(item => IsTaskSnapshot(fact.Card.Address, item))
                .Select(item => LocalSnapshot(db, fork.Parent.Id, ResourceUri.Parse(item.Uri).Segments[2])).ToArray();
            var taskId = TaskListService.SelectForMessages(expected.Select(item => inherited[item.Id]), tasks);
            var taskSelection = taskId == null ? null : ChatResourceUri.CreateArtifactRevision(fact.Card.ChatId, tasks.Single(item => item.Id == taskId));
            if (!SameRunValue(taskSelection, resources.ActiveTask))
                throw new InvalidDataException("Fork task selection disagrees with its exact prefix.");
            var ids = new HashSet<string>(expected.Select(item => item.Id), StringComparer.Ordinal);
            var active = retained.ActiveContext == null ? null : LocalSnapshot(db, fork.Parent.Id, ResourceUri.Parse(retained.ActiveContext.Uri).Segments[2]);
            var selected = active != null && ids.Contains(active.SourceMessageId) && ids.Contains(CheckpointMetadata(active).ThroughMessageId)
                ? ChatResourceUri.RebaseArtifactRevision(retained.ActiveContext, fact.Card.ChatId) : null;
            if (!SameRunValue(selected, resources.ActiveContext))
                throw new InvalidDataException("Fork must preserve only the source's applicable selected checkpoint.");
            return history;
        }

        private void ApplyForkRows(SqliteConnection db, SqliteTransaction tx, ConversationChatCommit fact)
        {
            if (fact.ExpectedRevision != 0 || fact.Fork == null) return;
            // Derived obligations retain original operation/run/sequence. No child
            // call_fact, result_fact, effect proof or runtime checkpoint is invented.
            using (var command = Command(db,
                "INSERT INTO chat_sequence VALUES($stream,$chat,$next_e,$next_t); " +
                "INSERT INTO chat_reference SELECT stream,$chat,scope_kind,scope_id,uri,revision,ordinal,version,operation FROM chat_reference WHERE stream=$stream AND chat=$source; " +
                "INSERT INTO context_required SELECT stream,$chat,operation,run,sequence,unknown_effect,0,uri,view,read_scope,signature,repeat_guard FROM context_required WHERE stream=$stream AND chat=$source AND sequence<=$revision AND (unknown_effect=1 OR repeat_guard=1)", tx))
            {
                command.Parameters.AddWithValue("$chat", fact.Card.ChatId); command.Parameters.AddWithValue("$source", fact.Fork.Parent.Id);
                command.Parameters.AddWithValue("$revision", fact.Fork.Parent.Revision);
                command.Parameters.AddWithValue("$next_t", fact.Fork.NextCall); command.Parameters.AddWithValue("$next_e", fact.Fork.NextEvidence);
                command.ExecuteNonQuery();
            }
            Fault?.Invoke("after_fork_index");
        }

        private bool InheritedFact(SqliteConnection db, string chat, string origin, long sequence)
        {
            if (chat == origin) return true;
            return InheritedFact(db, CurrentChat(db, chat)?.Fact.Fork, origin, sequence);
        }

        private bool InheritedFact(SqliteConnection db, ConversationFork fork, string origin, long sequence)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (fork != null)
            {
                if (!seen.Add(fork.Parent.Id)) throw new InvalidDataException("Cyclic fork provenance.");
                if (origin == fork.Parent.Id) return sequence <= fork.Parent.Revision;
                var parent = FindChatWrite(db, fork.ParentOperation, fork.Parent.Id);
                if (parent == null || parent.Position.Sequence > fork.Parent.Revision)
                    throw new InvalidDataException("Fork lost its retained source provenance.");
                fork = parent.Fact.Fork;
            }
            return false;
        }

        private void RequireHistoryOwner(SqliteConnection db, string chat, ConversationChatReceipt write)
        {
            if (write == null || !InheritedFact(db, chat, write.Fact.Card.ChatId, write.Position.Sequence))
                throw new InvalidDataException("Foreign history locator.");
        }
    }

    internal sealed class ConversationFork
    {
        public ConversationMetadata Parent { get; private set; }
        public string ParentOperation { get; private set; }
        public string ParentHash { get; private set; }
        public string ThroughMessageId { get; private set; }
        public long NextCall { get; private set; }
        public long NextEvidence { get; private set; }
        [JsonConstructor] internal ConversationFork(ConversationMetadata parent, string parentOperation, string parentHash,
            string throughMessageId, long nextCall, long nextEvidence)
        {
            if (parent == null || parent.Revision < 1 || string.IsNullOrWhiteSpace(parentOperation) ||
                string.IsNullOrWhiteSpace(parentHash) || nextCall < 1 || nextEvidence < 1)
                throw new ArgumentException("Fork requires a frozen source and reserved reference sequences.");
            Parent = parent; ParentOperation = parentOperation; ParentHash = parentHash;
            ThroughMessageId = throughMessageId; NextCall = nextCall; NextEvidence = nextEvidence;
        }
    }
}
