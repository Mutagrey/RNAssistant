using System;
using System.Collections.Generic;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;

namespace RNAssistant.Core.Storage
{
    public sealed class ChatConversationStoreAdapter : IConversationStore
    {
        private readonly ChatStore _store;

        public ChatConversationStoreAdapter(ChatStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public ChatSession LoadOrCreateActive(string host, string documentKey, string documentTitle)
        {
            return _store.LoadOrCreateActive(host, documentKey, documentTitle);
        }

        public ChatSession CreateTransient(string host, string documentKey, string documentTitle, string title)
        {
            return _store.CreateTransient(host, documentKey, documentTitle, title);
        }

        public ChatSession Load(string host, string documentKey, string sessionId)
        {
            return _store.Load(host, documentKey, sessionId);
        }

        public ChatSession Load(string sessionId)
        {
            return _store.Load(sessionId);
        }

        public void Save(ChatSession session)
        {
            _store.Save(session);
        }

        public bool IsPersisted(ChatSession session)
        {
            return _store.IsPersisted(session);
        }

        public IReadOnlyList<ChatSessionHeader> ListHeaders()
        {
            return _store.ListHeaders();
        }

        public IReadOnlyList<ChatSessionHeader> ListHeaders(
            string host,
            string documentKey,
            string documentTitle)
        {
            return _store.ListHeaders(host, documentKey, documentTitle);
        }

        public ChatSession Move(ChatSession session, string host, string documentKey, string documentTitle)
        {
            return _store.Move(session, host, documentKey, documentTitle);
        }

        public void MoveDocument(
            string oldHost,
            string oldDocumentKey,
            string newHost,
            string newDocumentKey,
            string documentTitle,
            string documentPath)
        {
            _store.MoveDocument(
                oldHost,
                oldDocumentKey,
                newHost,
                newDocumentKey,
                documentTitle,
                documentPath);
        }

        public bool Delete(string host, string documentKey, string sessionId)
        {
            return _store.Delete(host, documentKey, sessionId);
        }

        public bool DeleteDocument(string host, string documentKey)
        {
            return _store.DeleteDocument(host, documentKey);
        }

        public string LoadActiveSessionId(string host, string documentKey)
        {
            return _store.LoadActiveSessionId(host, documentKey);
        }

        public void SaveActiveSessionId(string host, string documentKey, string sessionId)
        {
            _store.SaveActiveSessionId(host, documentKey, sessionId);
        }

        public bool PrepareInterruptedRunRecovery(ChatSession session, string runId)
        {
            var openToolExecution = _store.HasOpenToolExecution(session, runId);
            _store.CloseOpenSteps(
                session,
                runId,
                "interrupted",
                "Runtime stopped before the model step reached a terminal event.");
            return openToolExecution;
        }
    }
}
