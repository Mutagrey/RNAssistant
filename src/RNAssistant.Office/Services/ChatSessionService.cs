using System;
using System.Collections.Generic;
using System.IO;
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
                if (session == null &&
                    (!allowMissingRequestedFallback ||
                     (string.IsNullOrWhiteSpace(_activeRuntimeDocumentKey) &&
                      string.Equals(requestedSessionId, _activeSessionId, StringComparison.OrdinalIgnoreCase))))
                {
                    session = _chatStore.Load(requestedSessionId);
                }
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
            UpdateCurrentDocumentMetadata(session);
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
            UpdateCurrentDocumentMetadata(session);
            SetActiveSession(session);
            return session;
        }

        public ChatSession DeleteAndSelectNext(string sessionId)
        {
            var current = _chatStore.Load(sessionId);
            var host = current == null ? _adapter.HostName : current.Host;
            var documentKey = current == null ? _adapter.DocumentKey : current.DocumentKey;
            var documentTitle = current == null ? _adapter.DocumentTitle : current.DocumentTitle;
            _chatStore.Delete(host, documentKey, sessionId);
            var next = _chatStore.List(host, documentKey, documentTitle).FirstOrDefault();
            if (next == null)
            {
                next = _chatStore.List(_adapter.HostName, _adapter.DocumentKey, _adapter.DocumentTitle).FirstOrDefault()
                    ?? _chatStore.Create(_adapter.HostName, _adapter.DocumentKey, _adapter.DocumentTitle, "New chat");
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
            _activeRuntimeDocumentKey = IsCurrentDocument(session) ? _adapter.RuntimeDocumentKey : null;
            _chatStore.SaveActiveSessionId(session.Host, session.DocumentKey, _activeSessionId);
        }

        public IReadOnlyList<ChatSessionSummary> GetChatSummaries(string activeId)
        {
            return _chatStore.List()
                .Select(s => new ChatSessionSummary
                {
                    Id = ChatStore.GetSessionId(s),
                    Host = s.Host,
                    DocumentKey = s.DocumentKey,
                    DocumentTitle = s.DocumentTitle,
                    DocumentPath = ResolveDocumentPath(s),
                    Title = s.Title,
                    Model = s.Model,
                    HtmlModeEnabled = s.HtmlModeEnabled,
                    HasHtmlWorkspace = HasHtmlWorkspace(s.HtmlWorkspace),
                    HtmlFileCount = s.HtmlWorkspace == null || s.HtmlWorkspace.Files == null ? 0 : s.HtmlWorkspace.Files.Count,
                    HtmlDataSourceCount = s.HtmlWorkspace == null || s.HtmlWorkspace.DataSources == null ? 0 : s.HtmlWorkspace.DataSources.Count,
                    CreatedUtc = s.CreatedUtc,
                    UpdatedUtc = s.UpdatedUtc,
                    MessageCount = s.Messages == null ? 0 : s.Messages.Count,
                    IsCurrentDocument = IsCurrentDocument(s)
                })
                .ToList();
        }

        public bool IsCurrentDocument(ChatSession session)
        {
            return session != null &&
                string.Equals(session.Host, _adapter.HostName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(session.DocumentKey, _adapter.DocumentKey, StringComparison.OrdinalIgnoreCase);
        }

        public string GetDocumentPath(ChatSession session)
        {
            return ResolveDocumentPath(session);
        }

        private void UpdateCurrentDocumentMetadata(ChatSession session)
        {
            if (!IsCurrentDocument(session))
            {
                return;
            }

            var provider = _adapter as IOfficeContextProvider;
            var officeContext = provider == null ? null : provider.GetOfficeContext();
            var path = officeContext == null ? string.Empty : officeContext.DocumentPath;
            if (!string.IsNullOrWhiteSpace(path) && !string.Equals(session.DocumentPath, path, StringComparison.OrdinalIgnoreCase))
            {
                session.DocumentPath = path;
                _chatStore.Save(session);
            }
        }

        private static string ResolveDocumentPath(ChatSession session)
        {
            if (session == null)
            {
                return string.Empty;
            }
            if (!string.IsNullOrWhiteSpace(session.DocumentPath))
            {
                return session.DocumentPath;
            }

            var marker = ":Path:";
            var key = session.DocumentKey ?? string.Empty;
            var markerIndex = key.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                return key.Substring(markerIndex + marker.Length);
            }
            return Path.IsPathRooted(key) ? key : string.Empty;
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
