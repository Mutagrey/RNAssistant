using System.Collections.Generic;
using System.Threading;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Persistence
{
    public interface IConversationStore
    {
        ChatSession LoadOrCreateActive(string host, string documentKey, string documentTitle);
        // A supplied Office authority is resolved by the runtime before creating
        // the draft; it is never inferred from a different active document.
        ChatSession CreateTransient(string host, string documentKey, string documentTitle, string title,
            string documentAuthorityId = null);
        ChatSession Load(string host, string documentKey, string sessionId);
        ChatSession Load(string sessionId);
        ConversationMetadata ReadMetadata(string sessionId);
        // Addressed metadata; presentation may request fresh advisory CAS sizes.
        ChatSessionHeader ReadHeader(string sessionId, bool includeStorageUsage = false);
        ChatOverview ReadOverview(ChatSession scope, string sessionId, bool metadataOnly = false);
        void Commit(ChatSession session, ConversationCommit commit);
        void CommitRun(ConversationRunCommit commit);
        void SetTitle(ChatSession session, string title);
        void SetAgentTitle(ChatSession session, string title);
        void SetModel(ChatSession session, string model);
        void SetReasoningEnabled(ChatSession session, bool enabled);
        ConversationMessageWindow ReadMessageWindow(string chatId, int beforeIndex, int pageSize);
        ConversationMessageWindow ReadMessageWindow(string host, string documentKey, string chatId, int beforeIndex, int pageSize);
        bool IsPersisted(ChatSession session);
        // Recovery/identity scans do not need advisory CAS file sizes. Catalogs do.
        IReadOnlyList<ChatSessionHeader> ListHeaders(bool includeStorageUsage = true);
        IReadOnlyList<ChatSessionHeader> ListHeaders(string host, string documentKey, string documentTitle);
        ChatSession Move(ChatSession session, string host, string documentKey, string documentTitle);
        void MoveDocument(
            string oldHost,
            string oldDocumentKey,
            string newHost,
            string newDocumentKey,
            string documentTitle,
            string documentPath);
        bool Delete(string host, string documentKey, string sessionId);
        bool DeleteDocument(string host, string documentKey);
        string LoadActiveSessionId(string host, string documentKey);
        void SaveActiveSessionId(string host, string documentKey, string sessionId);

        // Closes the storage-owned open step boundary for an interrupted run and
        // reports whether the retained stream contained an open tool execution.
        // Projection repair and the final conversation save remain application policy.
        bool PrepareInterruptedRunRecovery(ChatSession session, string runId);
    }

    // Bounded context preparation over the canonical conversation index. This is
    // a read port on the existing Store owner, not another cache or prompt store.
    public interface IConversationContextStore
    {
        ContextWorkingSet RestoreWorkingSet(ChatSession session, int recentResults,
            int workingBudget, CancellationToken token = default(CancellationToken));
    }

    // Artifact-body preparation is a separate storage read port. Conversation
    // consumers do not gain artifact/CAS internals through IConversationStore.
    public interface IConversationArtifactBodyReader
    {
        // Loads only on an already loaded, revision-fenced chat. It does not
        // reload the transcript or substitute a newer resource revision.
        bool LoadArtifactBody(ChatSession session, string artifactId);
    }

    // A read address and observed revision, not a partial writable session or
    // authority to execute a run. No history, artifacts or continuation is retained.
    public sealed class ConversationMetadata
    {
        public string Id { get; private set; }
        public long Revision { get; private set; }
        public string Host { get; private set; }
        public string DocumentKey { get; private set; }
        public string WorkspaceId { get; private set; }
        public string DocumentAuthorityId { get; private set; }

        public ConversationMetadata(string id, long revision, string host, string documentKey,
            string workspaceId = null, string documentAuthorityId = null)
        {
            Id = id; Revision = revision; Host = host; DocumentKey = documentKey;
            WorkspaceId = workspaceId; DocumentAuthorityId = documentAuthorityId;
        }

        public static ConversationMetadata From(ChatSession session)
        {
            return session == null ? null : new ConversationMetadata(session.Id, session.Revision,
                session.Host, session.DocumentKey, session.WorkspaceId, session.DocumentAuthorityId);
        }
    }

    // A read-only state projection for one bounded message page. Session.Messages
    // contains only that page, so callers must use MessageCount and never submit
    // this projection as a writable full session.
    internal sealed class ConversationStateWindow
    {
        internal ChatSession Session { get; private set; }
        internal ConversationMessageWindow Window { get; private set; }
        internal int MessageCount { get; private set; }

        internal ConversationStateWindow(ChatSession session, ConversationMessageWindow window, int messageCount)
        {
            Session = session ?? throw new System.ArgumentNullException(nameof(session));
            Window = window;
            MessageCount = messageCount;
        }
    }

    public sealed class ConversationMessageWindow
    {
        public ConversationMetadata Metadata { get; set; }
        public IReadOnlyList<ChatMessage> PageMessages { get; set; }
        public IReadOnlyList<ChatArtifact> Artifacts { get; set; }
        public ISet<string> DetachedArtifactIds { get; set; }
        public ISet<string> ApplicableTombstoneIds { get; set; }
        public string ActiveHtmlArtifactId { get; set; }
        public string ActivePlanDocumentArtifactId { get; set; }
        public string ActiveTaskListArtifactId { get; set; }
        public int StartIndex { get; set; }
        public int TotalCount { get; set; }
    }
}
