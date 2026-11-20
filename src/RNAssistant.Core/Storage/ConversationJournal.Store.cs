using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    // Direct Store integration on the existing journal owner. Composition remains
    // isolated until all Store/resource/runtime operations pass the cutover gate.
    internal sealed partial class ConversationJournal : IConversationStore, IConversationInputReadStore,
        IConversationArtifactBodyReader
    {
        private bool _textStoreRootChecked;
        private void RequireTextRoot()
        {
            if (_textStoreRootChecked) return;
            JournalStorageRoot.Ensure(_paths);
            _textStoreRootChecked = true;
        }

        private ConversationChatReceipt ReadChat(string id)
        {
            RequireTextRoot();
            return Access((db, cursor) => TextChat(db, id), CancellationToken.None);
        }

        private ConversationChatReceipt TextChat(SqliteConnection db, string id)
        {
            var chat = CurrentChat(db, id);
            if (chat == null && Card(db, id) != null) throw TextStoreScope();
            if (chat != null && Scalar(db, "SELECT 1 FROM input_state WHERE stream=$stream AND recipient=$chat AND request_id IS NULL LIMIT 1", id) != null)
                throw TextStoreScope();
            var run = chat == null ? null : CurrentRun(db, id);
            if (run != null && (chat.Fact.Run == null ? !TerminalRun(run.Fact.Lifecycle) :
                (run.Fact.RunId == chat.Fact.Run.RunId ? run.Fact.Body.Sha256 != chat.Fact.RunState?.Sha256 : !TerminalRun(run.Fact.Lifecycle))))
                throw TextStoreScope();
            return chat;
        }

        private static ConversationMetadata ChatMetadata(ConversationChatReceipt receipt)
        {
            var address = receipt.Fact.Card.Address;
            return new ConversationMetadata(address.Id, receipt.Position.Sequence, address.Host, address.DocumentKey,
                address.WorkspaceId, address.DocumentAuthorityId);
        }

        private static bool MatchesAddress(ConversationChatReceipt receipt, string host, string key)
        { return receipt != null && receipt.Fact.Card.Address.Host == host && receipt.Fact.Card.Address.DocumentKey == key; }

        ChatSession IConversationStore.CreateTransient(string host, string key, string documentTitle, string title,
            string documentAuthorityId)
        {
            if (documentAuthorityId != null && (string.IsNullOrWhiteSpace(documentAuthorityId) ||
                _scope != "document:" + documentAuthorityId || string.Equals(host, "Workspace", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The draft authority belongs to another conversation scope.");
            RequireTextRoot();
            var session = new ChatSession { Host = host, DocumentKey = key, DocumentTitle = documentTitle,
                Title = string.IsNullOrWhiteSpace(title) ? "New chat" : title,
                WorkspaceId = _scope.StartsWith("workspace:", StringComparison.Ordinal) ? _scope.Substring(10) : null,
                DocumentAuthorityId = _scope.StartsWith("document:", StringComparison.Ordinal) ? _scope.Substring(9) : null };
            ChatSessionNormalizer.Normalize(session, host, key, documentTitle);
            return session;
        }

        void IConversationStore.Commit(ChatSession session, ConversationCommit commit)
        {
            RequireTextRoot();
            try
            {
                var receipt = CommitChat(session, commit); ApplyChatReceipt(session, receipt); ApplyMessageReferences(commit, receipt);
                if (receipt.Fact.Fork != null && receipt.Fact.ExpectedRevision == 0) LoadChatReferences(session);
            }
            catch (IOException)
            {
                // Recover only this operation's receipt; a lost acknowledgement
                // remains an exception. Retrying the same command cannot append twice.
                var own = Access((db, cursor) => FindChatWrite(db, commit.OperationId), CancellationToken.None);
                if (own?.Fact.Card.ChatId == session.Id) { ApplyChatReceipt(session, own); ApplyMessageReferences(commit, own); }
                throw;
            }
        }

        private static void ApplyChatReceipt(ChatSession session, ConversationChatReceipt receipt)
        {
            if (session.Revision > receipt.Position.Sequence)
                throw new ChatConcurrencyException("An earlier receipt cannot rewind the current chat projection.");
            if (session.Revision < receipt.Position.Sequence) session.ResourceReferenceIndex.AddRange(receipt.Fact.References ?? new ModelResourceReference[0]);
            session.ToolCallReferenceSequence = Math.Max(session.ToolCallReferenceSequence, (receipt.Fact.Fork?.NextCall ?? 1) - 1);
            session.EvidenceReferenceSequence = Math.Max(session.EvidenceReferenceSequence, (receipt.Fact.Fork?.NextEvidence ?? 1) - 1);
            foreach (var message in receipt.Fact.Messages.Select(item => item.Header).Concat(receipt.Fact.Tools?.Terminal == null ? new ChatMessage[0] : new[] { receipt.Fact.Tools.Terminal.Result }))
            {
                session.ToolCallReferenceSequence = Math.Max(session.ToolCallReferenceSequence, message.ToolCallReference?.Ordinal ?? 0);
                session.EvidenceReferenceSequence = Math.Max(session.EvidenceReferenceSequence, message.EvidenceReference?.Ordinal ?? 0);
            }
            session.Revision = receipt.Position.Sequence; session.UpdatedUtc = receipt.UpdatedUtc;
            session.StorageHeadHash = receipt.Position.Hash; session.StorageByteLength = receipt.Position.End;
            session.StorageTailByteOffset = receipt.Position.Start;
            foreach (var artifact in session.Artifacts.Where(item => item.Kind == ChatArtifactKinds.TaskList && item.Id != session.ActiveTaskListArtifactId))
                artifact.InlineText = null;
            foreach (var artifact in session.Artifacts.Where(item => item.Kind == ChatArtifactKinds.Compaction)) artifact.InlineText = null;
        }

        ConversationMetadata IConversationStore.ReadMetadata(string id)
        { var receipt = ReadChat(id); return receipt == null ? null : ChatMetadata(receipt); }

        bool IConversationStore.IsPersisted(ChatSession session)
        { return session != null && MatchesAddress(ReadChat(session.Id), session.Host, session.DocumentKey); }

        ConversationMessageWindow IConversationStore.ReadMessageWindow(string id, int beforeIndex, int pageSize)
        { return ReadChatWindow(id, null, null, beforeIndex, pageSize); }

        ConversationMessageWindow IConversationStore.ReadMessageWindow(string host, string key, string id, int beforeIndex, int pageSize)
        { return ReadChatWindow(id, host, key, beforeIndex, pageSize); }

        private ConversationMessageWindow ReadChatWindow(string id, string host, string key, int beforeIndex, int pageSize)
        {
            ValidateMessageWindow(beforeIndex, pageSize);
            RequireTextRoot();
            using (RuntimeStorageActivity.Acquire(_paths))
            {
                var selected = SelectChatWindow(id, host, key, beforeIndex, pageSize);
                return selected == null ? null : ProjectChatWindow(selected.Item1, selected.Item2,
                    beforeIndex, pageSize, includeActiveState: false).Item1;
            }
        }

        // A polling read gets one exact revision of the header/state and the latest
        // bounded page. It never materializes earlier message pages or their bodies.
        internal ConversationStateWindow ReadStateWindow(string id, string host, string key, int pageSize)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            if (pageSize < 1 || pageSize > 100) throw new ArgumentOutOfRangeException(nameof(pageSize));
            RequireTextRoot();
            using (RuntimeStorageActivity.Acquire(_paths))
            {
                var selected = Access((db, cursor) => {
                    var current = TextChat(db, id);
                    if (current == null || host != null && !MatchesAddress(current, host, key)) return null;
                    return Tuple.Create(current, ReadWindowEntries(db, id, current.Fact.MessageCount, pageSize),
                        ReadPendingDeliveryEntries(db, id));
                }, CancellationToken.None);
                if (selected == null) return null;
                var projected = ProjectChatWindow(selected.Item1, selected.Item2,
                    selected.Item1.Fact.MessageCount, pageSize, includeActiveState: true);
                var window = projected.Item1;
                var resources = projected.Item2;
                var session = CreateSessionShell(selected.Item1);
                ApplyChatReceipt(session, selected.Item1);
                var pageIds = new HashSet<string>(window.PageMessages.Select(item => item.Id), StringComparer.Ordinal);
                session.Messages.AddRange(selected.Item3.Where(item => !pageIds.Contains(item.Header.Id))
                    .Select(item => item.Header.CopyForContext()));
                session.Messages.AddRange(window.PageMessages);
                session.Artifacts.AddRange(window.Artifacts.GroupBy(item => item.Id, StringComparer.Ordinal).Select(group => group.First()));
                session.ArtifactLinks = resources.Links.ToList();
                session.ActivePlanDocumentArtifactId = window.ActivePlanDocumentArtifactId;
                session.ActiveTaskListArtifactId = window.ActiveTaskListArtifactId;
                session.ActiveHtmlArtifactId = window.ActiveHtmlArtifactId;
                session.ActiveContextCheckpointId = resources.ActiveContext == null ? null :
                    ResourceUri.Parse(resources.ActiveContext.Uri).Segments[2];
                RestoreCheckpointProjection(session);
                TaskListPublication.HydrateActive(session, _bodies);
                return new ConversationStateWindow(session, window, window.TotalCount);
            }
        }

        private Tuple<ConversationChatReceipt, ConversationTextEntry[]> SelectChatWindow(string id,
            string host, string key, int beforeIndex, int pageSize)
        {
            return Access((db, cursor) => {
                var current = TextChat(db, id);
                if (current == null || host != null && !MatchesAddress(current, host, key)) return null;
                if (beforeIndex > current.Fact.MessageCount) throw new ArgumentOutOfRangeException(nameof(beforeIndex));
                return Tuple.Create(current, ReadWindowEntries(db, id, beforeIndex, pageSize));
            }, CancellationToken.None);
        }

        private ConversationTextEntry[] ReadWindowEntries(SqliteConnection db, string id,
            int beforeIndex, int pageSize)
        {
            var ids = new List<Tuple<string, string>>();
            using (var command = Command(db, "SELECT id,operation FROM chat_message WHERE stream=$stream AND chat=$chat AND ordinal >= $start AND ordinal < $before ORDER BY ordinal"))
            {
                command.Parameters.AddWithValue("$chat", id);
                command.Parameters.AddWithValue("$start", Math.Max(0, beforeIndex - pageSize));
                command.Parameters.AddWithValue("$before", beforeIndex);
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) ids.Add(Tuple.Create(reader.GetString(0), reader.GetString(1)));
            }
            if (ids.Count != Math.Min(beforeIndex, pageSize)) throw new InvalidDataException("Message page contains missing ordinals.");
            var commits = ids.Select(item => item.Item2).Distinct().ToDictionary(operation => operation, operation => FindChatWrite(db, operation));
            var importedChunks = new Dictionary<string, ConversationTextEntry[]>();
            return ids.Select(item => {
                var write = commits[item.Item2];
                RequireHistoryOwner(db, id, write);
                return ProjectInheritedText(SavedChatEntry(db, write, item.Item1, importedChunks), write.Fact.Card.Address, id);
            }).ToArray();
        }

        private ConversationTextEntry[] ReadPendingDeliveryEntries(SqliteConnection db, string id)
        {
            var messageIds = new List<string>();
            using (var command = Command(db, "SELECT i.message FROM input_state i JOIN chat_message m ON m.stream=i.stream AND m.chat=i.recipient AND m.id=i.message WHERE i.stream=$stream AND i.recipient=$chat AND i.status='Delivering' AND m.ordinal>=0 ORDER BY m.ordinal LIMIT 129"))
            {
                command.Parameters.AddWithValue("$chat", id);
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) messageIds.Add(reader.GetString(0));
            }
            if (messageIds.Count > 128) throw new InvalidDataException("Pending delivery projection exceeds its bound.");
            var importedChunks = new Dictionary<string, ConversationTextEntry[]>();
            var entries = new List<ConversationTextEntry>();
            foreach (var messageId in messageIds)
            {
                var operation = ChatMessageOperation(db, id, messageId);
                if (operation == null) continue;
                var write = FindChatWrite(db, operation);
                RequireHistoryOwner(db, id, write);
                entries.Add(ProjectInheritedText(SavedChatEntry(db, write, messageId, importedChunks),
                    write.Fact.Card.Address, id));
            }
            return entries.ToArray();
        }

        private Tuple<ConversationMessageWindow, ConversationResourceSet> ProjectChatWindow(
            ConversationChatReceipt receipt, ConversationTextEntry[] entries, int beforeIndex, int pageSize,
            bool includeActiveState)
        {
            var metadata = ChatMetadata(receipt);
            var state = ReadResourceSet(receipt.Fact.Resources, metadata);
            var pageReferences = entries.SelectMany(item =>
                (item.Header.ResourceRefs ?? new List<ResourceRef>()).Concat(new[] { item.Header.HtmlWorkspaceCheckpoint }));
            var references = includeActiveState ? pageReferences.Concat(new[] {
                state.ActivePlan, state.ActiveTask, state.ActiveContext }).Where(item => item != null) : pageReferences;
            var artifacts = ReadPageArtifacts(metadata, state, references);
            var sourceIds = new HashSet<string>(entries.Select(entry => entry.Header.Id));
            var removed = new HashSet<string>(artifacts.Where(item => item.RemovedFromDocument).Select(item => item.Id));
            var activeHtml = state.ActiveHtml == null ? null : ResourceUri.Parse(state.ActiveHtml.Uri).Segments[2];
            var window = new ConversationMessageWindow {
                Metadata = metadata,
                PageMessages = entries.Select(ReadText).ToArray(), Artifacts = artifacts,
                DetachedArtifactIds = new HashSet<string>(artifacts.Where(item => ArtifactWorkingSet.IsDetached(metadata, state?.Links, item)).Select(item => item.Id)),
                ApplicableTombstoneIds = new HashSet<string>(artifacts.Where(item => PlanDocumentService.IsApplicableTombstone(metadata,
                    sourceIds, item)).Select(item => item.Id)),
                ActiveHtmlArtifactId = removed.Contains(activeHtml) ? null : activeHtml,
                ActiveTaskListArtifactId = state.ActiveTask == null ? null : ResourceUri.Parse(state.ActiveTask.Uri).Segments[2],
                ActivePlanDocumentArtifactId = state.ActivePlan == null ? null : ResourceUri.Parse(state.ActivePlan.Uri).Segments[2],
                StartIndex = Math.Max(0, beforeIndex - pageSize), TotalCount = receipt.Fact.MessageCount };
            return Tuple.Create(window, state);
        }

        private static void ValidateMessageWindow(int beforeIndex, int pageSize)
        {
            if (pageSize < 1 || pageSize > 100 || beforeIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        ChatSession IConversationStore.Load(string id) { return LoadTextChat(id, null, null); }
        ChatSession IConversationStore.Load(string host, string key, string id) { return LoadTextChat(id, host, key); }

        private ChatSession LoadTextChat(string id, string host, string key)
        {
            for (var attempt = 0; ; attempt++)
            {
                try { return LoadTextChatOnce(id, host, key); }
                catch (ChatConcurrencyException error) when (attempt < 2 &&
                    (error.Message.StartsWith("The event read requires", StringComparison.Ordinal) ||
                     error.Message.StartsWith("Chat changed during explicit full load", StringComparison.Ordinal)))
                { Thread.Sleep(1); }
            }
        }

        private ChatSession LoadTextChatOnce(string id, string host, string key)
        {
            using (RuntimeStorageActivity.Acquire(_paths))
            {
                var receipt = ReadChat(id);
                if (receipt == null || host != null && !MatchesAddress(receipt, host, key)) return null;
                var session = CreateSessionShell(receipt);
                for (var before = Math.Min(100, receipt.Fact.MessageCount); before > 0; before = Math.Min(before + 100, receipt.Fact.MessageCount))
                {
                    var start = session.Messages.Count;
                    var page = ReadChatWindow(id, host, key, before, before - start);
                    if (page?.Metadata.Revision != receipt.Position.Sequence) throw new ChatConcurrencyException("Chat changed during explicit full load.");
                    session.Messages.AddRange(page.PageMessages);
                    if (before == receipt.Fact.MessageCount) break;
                }
                if (receipt.Fact.Fork != null) ToolProtocolMessages.ExcludeUnmatchedToolCalls(session.Messages);
                ApplyChatReceipt(session, receipt);
                var metadata = ChatMetadata(receipt);
                var resourceSet = ReadResourceSet(receipt.Fact.Resources, metadata);
                session.Artifacts = ReadArtifacts(metadata, resourceSet.References).ToList();
                session.ArtifactLinks = resourceSet.Links.ToList();
                session.ActivePlanDocumentArtifactId = resourceSet.ActivePlan == null ? null : ResourceUri.Parse(resourceSet.ActivePlan.Uri).Segments[2];
                session.ActiveTaskListArtifactId = resourceSet.ActiveTask == null ? null : ResourceUri.Parse(resourceSet.ActiveTask.Uri).Segments[2];
                session.ActiveHtmlArtifactId = resourceSet.ActiveHtml == null ? null : ResourceUri.Parse(resourceSet.ActiveHtml.Uri).Segments[2];
                session.ActiveContextCheckpointId = resourceSet.ActiveContext == null ? null : ResourceUri.Parse(resourceSet.ActiveContext.Uri).Segments[2];
                RestoreCheckpointProjection(session);
                if (session.ActiveHtmlArtifactId != null) RestoreHtmlProjection(session);
                TaskListPublication.HydrateActive(session, _bodies);
                LoadChatReferences(session); return session;
            }
        }

        // Read-only actor for saved material views. No history, run recovery, or selection side effects.
        internal ChatSession ReadArtifactActor(string id, string resourceUri)
        {
            IReadOnlyList<ResourceRef> ignored;
            return ReadArtifactActor(id, resourceUri, true, out ignored);
        }

        internal ChatSession ReadArtifactActor(string id, string resourceUri, bool hydrate, out IReadOnlyList<ResourceRef> membership)
        {
            membership = new ResourceRef[0];
            using (RuntimeStorageActivity.Acquire(_paths))
            {
                var receipt = ReadChat(id);
                if (receipt == null) return null;
                var card = receipt.Fact.Card;
                var address = ChatMetadata(receipt);
                var resources = ReadResourceSet(receipt.Fact.Resources, address);
                membership = resources.References;
                var selected = resourceUri == null ? resources.References : resources.References.Where(item => item.Uri == resourceUri);
                return new ChatSession { Id = id, Revision = address.Revision, Host = address.Host, DocumentKey = address.DocumentKey,
                    WorkspaceId = address.WorkspaceId, DocumentAuthorityId = address.DocumentAuthorityId, Title = card.Title,
                    DocumentTitle = card.DocumentTitle, DocumentPath = card.DocumentPath, Artifacts = hydrate ? ReadArtifacts(address, selected).ToList() : new List<ChatArtifact>(),
                    ArtifactLinks = resources.Links.ToList(), ActiveHtmlArtifactId = resources.ActiveHtml == null ? null : ResourceUri.Parse(resources.ActiveHtml.Uri).Segments[2],
                    ActivePlanDocumentArtifactId = resources.ActivePlan == null ? null : ResourceUri.Parse(resources.ActivePlan.Uri).Segments[2] };
            }
        }

        internal bool HasActiveHtml(string id, string artifactId)
        {
            var receipt = ReadChat(id);
            if (receipt == null) return false;
            var html = ReadResourceSet(receipt.Fact.Resources, ChatMetadata(receipt)).ActiveHtml;
            return html != null && ResourceUri.Parse(html.Uri).Segments[2] == artifactId;
        }

        private ChatSession CreateSessionShell(ConversationChatReceipt receipt)
        {
            var card = receipt.Fact.Card;
            var address = card.Address;
            var session = new ChatSession {
                Id = card.ChatId, Host = address.Host, DocumentKey = address.DocumentKey,
                WorkspaceId = address.WorkspaceId, DocumentAuthorityId = address.DocumentAuthorityId,
                Title = card.Title, DocumentTitle = card.DocumentTitle, DocumentPath = card.DocumentPath,
                CreatedUtc = card.CreatedUtc, Model = card.SelectedModel, ReasoningEnabled = card.ReasoningEnabled,
                TitleAssignedByAgent = card.TitleAssignedByAgent, AgentCreation = card.AgentCreation,
                ParentSessionId = receipt.Fact.Fork?.Parent.Id, ParentSessionRevision = receipt.Fact.Fork?.Parent.Revision,
                ForkedThroughMessageId = receipt.Fact.Fork?.ThroughMessageId,
                ResourceCopies = ReadForkCopies(receipt.Fact.Fork, card.ChatId),
                PreviousDocumentKeys = (card.PreviousDocumentKeys ?? new string[0]).ToList(),
                Mode = card.Policy.Mode, MutationPolicy = card.Policy.MutationPolicy,
                Context = JsonConvert.DeserializeObject<DocumentContext>(ReadChatBody(receipt.Fact.Context)),
                LastRun = LoadChatRun(receipt.Fact),
                LastContextReceipt = receipt.Fact.ContextReceipt == null ? null :
                    JsonConvert.DeserializeObject<ContextReceipt>(ReadChatBody(receipt.Fact.ContextReceipt)) };
            session.Context.Host = address.Host;
            session.Context.DocumentKey = address.DocumentKey;
            return session;
        }

        internal IReadOnlyList<ChatSessionHeader> ReadChatHeaders(string host = null, string key = null, bool includeStorageUsage = false,
            string selectedId = null, ChatBlobStore.StorageSizeSnapshot sharedSizes = null)
        {
            RequireTextRoot();
            var sizes = includeStorageUsage ? sharedSizes ?? _bodies.CreateStorageSizeSnapshot() : null;
            return Access((db, cursor) => {
                var ids = new List<string>();
                if (selectedId != null) ids.Add(selectedId);
                else using (var command = Command(db, "SELECT chat FROM chat_card WHERE stream=$stream AND alias IS NOT NULL ORDER BY chat"))
                using (var reader = command.ExecuteReader()) while (reader.Read()) ids.Add(reader.GetString(0));
                return (IReadOnlyList<ChatSessionHeader>)ids.Select(id => TextChat(db, id))
                    .Where(receipt => receipt != null && (host == null || MatchesAddress(receipt, host, key))).Select(receipt => {
                        var card = receipt.Fact.Card; var address = card.Address;
                        var header = new ChatSessionHeader { Id = card.ChatId, Revision = receipt.Position.Sequence,
                            Host = address.Host, DocumentKey = address.DocumentKey, WorkspaceId = address.WorkspaceId,
                            DocumentAuthorityId = address.DocumentAuthorityId, DocumentTitle = card.DocumentTitle,
                            DocumentPath = card.DocumentPath, Title = card.Title, Model = card.SelectedModel, Mode = card.Policy.Mode,
                            ReasoningEnabled = card.ReasoningEnabled, CreatedUtc = card.CreatedUtc, UpdatedUtc = receipt.UpdatedUtc,
                            LastActivityUtc = new DateTime(Convert.ToInt64(Scalar(db, "SELECT last_activity_ticks FROM chat_state WHERE stream=$stream AND chat=$chat", card.ChatId)), DateTimeKind.Utc),
                            MessageCount = receipt.Fact.MessageCount, CreatorChatId = card.AgentCreation?.Creator?.ChatId,
                            RunId = receipt.Fact.Run?.RunId,
                            RunRuntimeId = receipt.Fact.Run?.RuntimeId, RunStatus = receipt.Fact.Run?.Status,
                            RunPhase = receipt.Fact.Run?.Phase, RunStartedUtc = receipt.Fact.Run?.StartedUtc };
                        ReadStorageUsage(db, header, sizes); return header;
                    }).OrderByDescending(header => header.UpdatedUtc).ToArray();
            }, CancellationToken.None);
        }

        ChatSessionHeader IConversationStore.ReadHeader(string id, bool includeStorageUsage)
        { return string.IsNullOrWhiteSpace(id) ? null : ReadChatHeaders(selectedId: id, includeStorageUsage: includeStorageUsage).SingleOrDefault(); }

        IReadOnlyList<ChatSessionHeader> IConversationStore.ListHeaders(bool includeStorageUsage, bool documentsOnly)
        { return documentsOnly && !_scope.StartsWith("document:", StringComparison.Ordinal) ? new ChatSessionHeader[0] : ReadChatHeaders(includeStorageUsage: includeStorageUsage); }
        IReadOnlyList<ChatSessionHeader> IConversationStore.ListHeaders(string host, string key, string title) { return ReadChatHeaders(host, key, true); }
        void IConversationStore.SetTitle(ChatSession session, string title) { session.Title = title; ((IConversationStore)this).Commit(session, new ConversationCommit()); }
        void IConversationStore.SetModel(ChatSession session, string model) { session.Model = model; ((IConversationStore)this).Commit(session, new ConversationCommit()); }
        void IConversationStore.SetReasoningEnabled(ChatSession session, bool enabled) { session.ReasoningEnabled = enabled; ((IConversationStore)this).Commit(session, new ConversationCommit()); }

        void IConversationStore.SaveActiveSessionId(string host, string key, string id)
        {
            RequireTextRoot();
            Access((db, cursor) => {
                if (string.IsNullOrWhiteSpace(id))
                {
                    string selected;
                    using (var command = Command(db, "SELECT chat FROM chat_selection WHERE stream=$stream")) selected = command.ExecuteScalar() as string;
                    if (selected == null || !MatchesAddress(TextChat(db, selected), host, key)) return false;
                }
                else if (!MatchesAddress(TextChat(db, id), host, key)) throw new InvalidOperationException("Selection belongs to another document/workspace.");
                var fact = new ConversationChatSelection(string.IsNullOrWhiteSpace(id) ? null : id);
                var record = DeliveryRecord(Guid.NewGuid().ToString("N"), "chat.selected", fact, TextPatternEngine.Sha256(JsonConvert.SerializeObject(fact)));
                ValidateChatFact(db, record);
                var position = Journal.Append(cursor, record.Operation, record.Fingerprint, record.Kind, record.Data);
                ApplyChatFact(db, record, position); return true;
            }, CancellationToken.None);
        }

        string IConversationStore.LoadActiveSessionId(string host, string key)
        {
            RequireTextRoot();
            return Access((db, cursor) => {
                string id;
                using (var command = Command(db, "SELECT chat FROM chat_selection WHERE stream=$stream")) id = command.ExecuteScalar() as string;
                return id != null && MatchesAddress(TextChat(db, id), host, key) ? id : null;
            }, CancellationToken.None);
        }

        ChatSession IConversationStore.LoadOrCreateActive(string host, string key, string title)
        {
            var store = (IConversationStore)this; var id = store.LoadActiveSessionId(host, key) ?? ReadChatHeaders(host, key).FirstOrDefault()?.Id;
            return id == null ? store.CreateTransient(host, key, title, "New chat") : store.Load(host, key, id);
        }

        ChatSession IConversationStore.Move(ChatSession session, string host, string key, string title) { return MoveChat(session, host, key, title); }
        void IConversationStore.MoveDocument(string oldHost, string oldKey, string host, string key, string title, string path)
        { MoveChats(oldHost, oldKey, host, key, title, path); }
        bool IConversationStore.Delete(string host, string key, string id) { return DeleteChat(host, key, id); }
        bool IConversationStore.DeleteDocument(string host, string key) { return DeleteDocument(host, key); }
        bool IConversationStore.PrepareInterruptedRunRecovery(ChatSession session, string runId)
        {
            RequireTextRoot();
            return Access((db, cursor) => {
                var current = TextChat(db, session.Id);
                RequireChatRevision(ConversationMetadata.From(session), current);
                if (current.Fact.Run?.RunId != runId || session.LastRun?.RunId != runId)
                    throw new ChatConcurrencyException("Recovery requires the observed run.");
                // Model/tool boundaries close with the atomic interruption checkpoint.
                return session.LastRun.KernelState?.InFlightTool != null;
            }, CancellationToken.None);
        }
    }
}
