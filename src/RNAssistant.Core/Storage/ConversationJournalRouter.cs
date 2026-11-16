using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    // Permanent composition for multiple exact conversation scopes over the one
    // canonical index. It owns no facts and delegates every operation to the
    // existing per-scope ConversationJournal.
    internal sealed class ConversationJournalRouter : IConversationStore, IEventStore
    {
        private readonly AppDataPaths _paths;
        private readonly StorageProtector _protector;
        private readonly Func<string, string, string> _resolveScope;
        private readonly ConcurrentDictionary<string, ConversationJournal> _journals =
            new ConcurrentDictionary<string, ConversationJournal>(StringComparer.Ordinal);
        private int _indexDiscoveryAttempted;

        internal ConversationJournalRouter(AppDataPaths paths, Func<string, string, string> resolveScope,
            StorageProtector protector = null)
        {
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            _resolveScope = resolveScope ?? throw new ArgumentNullException(nameof(resolveScope));
            _protector = protector;
            JournalStorageRoot.Ensure(paths);
        }

        private ConversationJournal Journal(string scope)
        {
            if (string.IsNullOrWhiteSpace(scope)) throw new ArgumentException("An exact conversation scope is required.");
            return _journals.GetOrAdd(scope, value => new ConversationJournal(_paths, value, _protector));
        }

        private string AddressScope(ConversationMetadata address)
        {
            if (address == null) throw new ArgumentNullException(nameof(address));
            if (!string.IsNullOrWhiteSpace(address.WorkspaceId)) return "workspace:" + address.WorkspaceId;
            if (!string.IsNullOrWhiteSpace(address.DocumentAuthorityId)) return "document:" + address.DocumentAuthorityId;
            throw new InvalidOperationException("Conversation address has no canonical document/workspace scope.");
        }

        private ConversationJournal ForAddress(ConversationMetadata address)
        { return Journal(AddressScope(address)); }
        private ConversationJournal For(ChatSession session)
        { return ForAddress(ConversationMetadata.From(session)); }

        private static IConversationStore Conversations(ConversationJournal journal)
        { return journal; }

        private static IEventStore Events(ConversationJournal journal)
        { return journal; }

        private string Resolve(string host, string key)
        {
            var scope = _resolveScope(host, key);
            if (string.IsNullOrWhiteSpace(scope)) throw new InvalidOperationException("Conversation scope could not be resolved.");
            return scope;
        }

        private ConversationJournal For(string host, string key)
        { return Journal(Resolve(host, key)); }

        private ConversationJournal ForChat(string chatId)
        {
            if (string.IsNullOrWhiteSpace(chatId)) return null;
            var scope = FindScope(chatId);
            return scope == null ? null : Journal(scope);
        }

        private string FindScope(string chatId)
        {
            var repaired = false;
            while (true)
            {
                try
                {
                    using (var db = JournalIndex.Open(_paths))
                    using (var command = db.CreateCommand())
                    {
                        command.CommandText = "SELECT stream FROM chat_card WHERE chat=$chat LIMIT 1";
                        command.Parameters.AddWithValue("$chat", chatId);
                        var scope = command.ExecuteScalar() as string;
                        if (scope != null) return scope;
                    }
                    if (repaired) return null;
                    if (Interlocked.Exchange(ref _indexDiscoveryAttempted, 1) != 0) return null;
                    if (!StorageJournal.Inventory(_paths).Any(stream => !stream.Resources)) return null;
                    JournalIndex.Repair(_paths, _protector ?? StorageProtector.None, true, System.Threading.CancellationToken.None);
                    repaired = true;
                }
                catch (SqliteException error) when (!repaired && JournalIndex.IsDamaged(error))
                {
                    Interlocked.Exchange(ref _indexDiscoveryAttempted, 1);
                    JournalIndex.Repair(_paths, _protector ?? StorageProtector.None, true, System.Threading.CancellationToken.None);
                    repaired = true;
                }
                catch (JournalIndex.IndexVersionMismatch) when (!repaired)
                {
                    Interlocked.Exchange(ref _indexDiscoveryAttempted, 1);
                    JournalIndex.Repair(_paths, _protector ?? StorageProtector.None, true, System.Threading.CancellationToken.None);
                    repaired = true;
                }
            }
        }

        private IReadOnlyList<ConversationJournal> AllJournals()
        {
            return StorageJournal.Inventory(_paths).Where(stream => !stream.Resources)
                .Select(stream => Journal(stream.Identity)).ToArray();
        }

        public ChatSession LoadOrCreateActive(string host, string documentKey, string documentTitle)
        { return Conversations(For(host, documentKey)).LoadOrCreateActive(host, documentKey, documentTitle); }

        public ChatSession CreateTransient(string host, string documentKey, string documentTitle, string title)
        { return Conversations(For(host, documentKey)).CreateTransient(host, documentKey, documentTitle, title); }

        public ChatSession Load(string host, string documentKey, string sessionId)
        {
            var journal = ForChat(sessionId);
            return journal == null ? null : Conversations(journal).Load(host, documentKey, sessionId);
        }

        public ChatSession Load(string sessionId)
        {
            var journal = ForChat(sessionId);
            return journal == null ? null : Conversations(journal).Load(sessionId);
        }

        public ConversationMetadata ReadMetadata(string sessionId)
        {
            var journal = ForChat(sessionId);
            return journal == null ? null : Conversations(journal).ReadMetadata(sessionId);
        }

        public ChatSessionHeader ReadHeader(string sessionId, bool includeStorageUsage = false)
        {
            var journal = ForChat(sessionId);
            return journal == null ? null : Conversations(journal).ReadHeader(sessionId, includeStorageUsage);
        }

        public ChatOverview ReadOverview(ChatSession scope, string sessionId, bool metadataOnly = false)
        {
            var owner = For(scope);
            var target = ForChat(sessionId);
            if (target == null || !ReferenceEquals(owner, target))
                throw new InvalidOperationException("Chat overview target belongs to another conversation scope.");
            return Conversations(owner).ReadOverview(scope, sessionId, metadataOnly);
        }

        public void SetAgentTitle(ChatSession session, string title)
        { Conversations(For(session)).SetAgentTitle(session, title); }

        public bool LoadArtifactBody(ChatSession session, string artifactId)
        { return Conversations(For(session)).LoadArtifactBody(session, artifactId); }

        public void Commit(ChatSession session, ConversationCommit commit)
        { Conversations(For(session)).Commit(session, commit); }

        public void CommitRun(ConversationRunCommit commit)
        {
            var journal = ForChat(commit?.SessionId ?? string.Empty);
            if (journal == null) throw new InvalidOperationException("Runtime commit belongs to an unknown chat.");
            Conversations(journal).CommitRun(commit);
        }

        public void SetTitle(ChatSession session, string title)
        { Conversations(For(session)).SetTitle(session, title); }

        public void SetModel(ChatSession session, string model)
        { Conversations(For(session)).SetModel(session, model); }

        public void SetReasoningEnabled(ChatSession session, bool enabled)
        { Conversations(For(session)).SetReasoningEnabled(session, enabled); }

        public ConversationMessageWindow ReadMessageWindow(string chatId, int beforeIndex, int pageSize)
        {
            var journal = ForChat(chatId);
            return journal == null ? null : Conversations(journal).ReadMessageWindow(chatId, beforeIndex, pageSize);
        }

        public ConversationMessageWindow ReadMessageWindow(string host, string documentKey, string chatId,
            int beforeIndex, int pageSize)
        {
            var journal = ForChat(chatId);
            return journal == null ? null : Conversations(journal).ReadMessageWindow(host, documentKey, chatId, beforeIndex, pageSize);
        }

        public bool IsPersisted(ChatSession session)
        { return session != null && Conversations(For(session)).IsPersisted(session); }

        public IReadOnlyList<ChatSessionHeader> ListHeaders(bool includeStorageUsage = true)
        {
            return AllJournals().SelectMany(journal => Conversations(journal).ListHeaders(includeStorageUsage))
                .OrderByDescending(header => header.UpdatedUtc).ToArray();
        }

        public IReadOnlyList<ChatSessionHeader> ListHeaders(string host, string documentKey, string documentTitle)
        { return Conversations(For(host, documentKey)).ListHeaders(host, documentKey, documentTitle); }

        public ChatSession Move(ChatSession session, string host, string documentKey, string documentTitle)
        { return Conversations(For(session)).Move(session, host, documentKey, documentTitle); }

        public void MoveDocument(string oldHost, string oldDocumentKey, string newHost, string newDocumentKey,
            string documentTitle, string documentPath)
        {
            Conversations(For(oldHost, oldDocumentKey)).MoveDocument(oldHost, oldDocumentKey, newHost, newDocumentKey,
                documentTitle, documentPath);
        }

        public bool Delete(string host, string documentKey, string sessionId)
        {
            var journal = ForChat(sessionId);
            return journal != null && Conversations(journal).Delete(host, documentKey, sessionId);
        }

        public bool DeleteDocument(string host, string documentKey)
        { return Conversations(For(host, documentKey)).DeleteDocument(host, documentKey); }

        public string LoadActiveSessionId(string host, string documentKey)
        { return Conversations(For(host, documentKey)).LoadActiveSessionId(host, documentKey); }

        public void SaveActiveSessionId(string host, string documentKey, string sessionId)
        { Conversations(For(host, documentKey)).SaveActiveSessionId(host, documentKey, sessionId); }

        public bool PrepareInterruptedRunRecovery(ChatSession session, string runId)
        { return Conversations(For(session)).PrepareInterruptedRunRecovery(session, runId); }

        internal ResourceMutationOrigin MutationOrigin(ChatSession session, ToolExecutionContext context)
        { return For(session).MutationOrigin(session, context); }

        public bool RetainedReplayGuards { get { return true; } }
        public bool AtomicAgentDelivery { get { return true; } }

        public ToolExecutionProgress ReadReplayGuard(ConversationMetadata session, ToolCall call)
        { return Events(ForAddress(session)).ReadReplayGuard(session, call); }

        public AgentChatAddress FindAgent(ConversationMetadata source, string agentId)
        { return Events(ForAddress(source)).FindAgent(source, agentId); }

        public ConversationInputRecord AcceptAgentDelivery(ChatSession source, ChatSession recipient,
            AgentDeliveryCommand command, Action dispatch)
        { return Events(For(source)).AcceptAgentDelivery(source, recipient, command, dispatch); }

        public ConversationInputRecord AcceptAgentSpawn(ChatSession source, ChatSession child,
            AgentSpawnCommand command, Action dispatch)
        { return Events(For(source)).AcceptAgentSpawn(source, child, command, dispatch); }

        public ConversationInputRecord AcceptAgentControl(ChatSession source, ChatSession target,
            AgentControlCommand command, Action dispatch)
        { return Events(For(source)).AcceptAgentControl(source, target, command, dispatch); }

        public SessionEvent Append(ChatSession session, SessionEventWrite write)
        { return Events(For(session)).Append(session, write); }

        public IReadOnlyList<SessionEvent> ReadToolPackAdmissions(ConversationMetadata session, IReadOnlyList<string> runs)
        { return Events(ForAddress(session)).ReadToolPackAdmissions(session, runs); }

        public IReadOnlyList<SessionEvent> Read(ConversationMetadata session, SessionEventReadMode mode)
        { return Events(ForAddress(session)).Read(session, mode); }

        public IReadOnlyList<AgentRequestReference> ReadAgentRequests(ConversationMetadata session,
            IReadOnlyList<string> messageIds)
        { return Events(ForAddress(session)).ReadAgentRequests(session, messageIds); }

        public AgentAssignmentPage ReadAgentAssignments(ConversationMetadata session, bool incoming, string afterRequest)
        { return Events(ForAddress(session)).ReadAgentAssignments(session, incoming, afterRequest); }

        public FinishEvidenceSnapshot ReadFinishEvidence(ChatSession session, string runId,
            IReadOnlyList<string> references)
        { return Events(For(session)).ReadFinishEvidence(session, runId, references); }

        public void ValidateInputDelivery(ChatSession session, IReadOnlyList<SessionEvent> facts,
            IReadOnlyList<ConversationInputRecord> records)
        { Events(For(session)).ValidateInputDelivery(session, facts, records); }

        public ISessionEventReader OpenReader(ConversationMetadata session)
        { return Events(ForAddress(session)).OpenReader(session); }

        public string ReadPayload(ConversationMetadata session, SessionEvent sessionEvent)
        { return Events(ForAddress(session)).ReadPayload(session, sessionEvent); }
    }
}
