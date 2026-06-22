using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Services
{
    public sealed class ChatSessionService
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly ChatStore _chatStore;
        private string _activeSessionId;
        private string _activeHost;
        private string _activeDocumentKey;
        private string _activeRuntimeDocumentKey;

        public ChatSessionService(IOfficeApplicationAdapter adapter, ChatStore chatStore)
        {
            _adapter = adapter;
            _chatStore = chatStore;
        }

        public void Reset()
        {
            _activeSessionId = null;
            _activeHost = null;
            _activeDocumentKey = null;
            _activeRuntimeDocumentKey = null;
        }

        public ChatSession LoadSession(string requestedSessionId)
        {
            return LoadSession(requestedSessionId, false);
        }

        public ChatSession LoadSession(string requestedSessionId, bool allowMissingRequestedFallback)
        {
            var host = _adapter.HostName;
            var documentKey = _adapter.DocumentKey;
            var runtimeKey = _adapter.RuntimeDocumentKey;
            var legacyDocumentKey = _adapter.LegacyDocumentKey;
            var title = _adapter.DocumentTitle;

            if (!string.IsNullOrWhiteSpace(_activeSessionId) &&
                string.Equals(_activeHost, host, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_activeRuntimeDocumentKey, runtimeKey, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(_activeDocumentKey, documentKey, StringComparison.OrdinalIgnoreCase))
            {
                var oldDocumentKey = _activeDocumentKey;
                _chatStore.MoveDocument(_activeHost, oldDocumentKey, host, documentKey, title);
                _activeHost = host;
                _activeDocumentKey = documentKey;
                _activeRuntimeDocumentKey = runtimeKey;
            }

            MigrateLegacyDocument(host, legacyDocumentKey, documentKey, title);

            ChatSession session = null;
            if (!string.IsNullOrWhiteSpace(requestedSessionId))
            {
                session = _chatStore.Load(host, documentKey, requestedSessionId);
                if (session == null && !allowMissingRequestedFallback)
                {
                    throw new InvalidOperationException("Chat session was not found.");
                }
            }
            else if (!string.IsNullOrWhiteSpace(_activeSessionId) &&
                     string.Equals(_activeHost, host, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(_activeDocumentKey, documentKey, StringComparison.OrdinalIgnoreCase))
            {
                session = _chatStore.Load(host, documentKey, _activeSessionId);
            }

            if (session == null)
            {
                session = _chatStore.LoadOrCreateActive(host, documentKey, title);
            }

            SetActiveSession(session);
            return session;
        }

        private void MigrateLegacyDocument(string host, string legacyDocumentKey, string documentKey, string title)
        {
            if (string.IsNullOrWhiteSpace(legacyDocumentKey) ||
                string.Equals(legacyDocumentKey, documentKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_chatStore.List(host, legacyDocumentKey, title).Count == 0)
            {
                return;
            }

            _chatStore.MoveDocument(host, legacyDocumentKey, host, documentKey, title);
        }

        public ChatSession CreateChat(string title)
        {
            LoadSession(null);
            var session = _chatStore.Create(
                _adapter.HostName,
                _adapter.DocumentKey,
                _adapter.DocumentTitle,
                string.IsNullOrWhiteSpace(title) ? "New chat" : title.Trim());
            SetActiveSession(session);
            return session;
        }

        public ChatSession DeleteAndSelectNext(string sessionId)
        {
            _chatStore.Delete(_adapter.HostName, _adapter.DocumentKey, sessionId);
            var next = _chatStore.List(_adapter.HostName, _adapter.DocumentKey, _adapter.DocumentTitle).FirstOrDefault();
            if (next == null)
            {
                next = _chatStore.Create(_adapter.HostName, _adapter.DocumentKey, _adapter.DocumentTitle, "New chat");
            }

            SetActiveSession(next);
            return next;
        }

        public void SetActiveSession(ChatSession session)
        {
            if (session == null)
            {
                return;
            }

            _activeSessionId = ChatStore.GetSessionId(session);
            _activeHost = session.Host;
            _activeDocumentKey = session.DocumentKey;
            _activeRuntimeDocumentKey = _adapter.RuntimeDocumentKey;
            _chatStore.SaveActiveSessionId(session.Host, session.DocumentKey, _activeSessionId);
        }

        public IReadOnlyList<ChatSessionSummary> GetChatSummaries(string activeId)
        {
            return _chatStore.List(_adapter.HostName, _adapter.DocumentKey, _adapter.DocumentTitle)
                .Select(s => new ChatSessionSummary
                {
                    Id = ChatStore.GetSessionId(s),
                    Host = s.Host,
                    DocumentKey = s.DocumentKey,
                    DocumentTitle = s.DocumentTitle,
                    Title = s.Title,
                    Model = s.Model,
                    HtmlModeEnabled = s.HtmlModeEnabled,
                    HasHtmlWorkspace = HasHtmlWorkspace(s.HtmlWorkspace),
                    HtmlFileCount = s.HtmlWorkspace == null || s.HtmlWorkspace.Files == null ? 0 : s.HtmlWorkspace.Files.Count,
                    HtmlDataSourceCount = s.HtmlWorkspace == null || s.HtmlWorkspace.DataSources == null ? 0 : s.HtmlWorkspace.DataSources.Count,
                    CreatedUtc = s.CreatedUtc,
                    UpdatedUtc = s.UpdatedUtc,
                    MessageCount = s.Messages == null ? 0 : s.Messages.Count
                })
                .ToList();
        }

        private static bool HasHtmlWorkspace(HtmlWorkspace workspace)
        {
            return workspace != null &&
                ((workspace.Files != null && workspace.Files.Count > 0) ||
                 (workspace.DataSources != null && workspace.DataSources.Count > 0));
        }

        public static string BuildForkTitle(ChatSession source)
        {
            var title = source == null || string.IsNullOrWhiteSpace(source.Title) ? "Chat" : source.Title.Trim();
            if (title.EndsWith(" fork", StringComparison.OrdinalIgnoreCase))
            {
                return title;
            }

            return (title.Length > 52 ? title.Substring(0, 52).TrimEnd() : title) + " fork";
        }
    }
}
