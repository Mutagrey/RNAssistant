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
        void Save(ChatSession session);
        bool IsPersisted(ChatSession session);
        IReadOnlyList<ChatSessionHeader> ListHeaders();
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
}
