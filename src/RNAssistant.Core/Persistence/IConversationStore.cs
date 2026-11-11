using System.Collections.Generic;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Persistence
{
    public interface IConversationStore
    {
        ChatSession LoadOrCreateActive(string host, string documentKey, string documentTitle);
        ChatSession CreateTransient(string host, string documentKey, string documentTitle, string title);
        ChatSession Load(string host, string documentKey, string sessionId);
        ChatSession Load(string sessionId);
        ConversationMetadata ReadMetadata(string sessionId);
        void Commit(ChatSession session, ConversationCommit commit);
        void CommitRun(ConversationRunCommit commit);
        void SetTitle(ChatSession session, string title);
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
