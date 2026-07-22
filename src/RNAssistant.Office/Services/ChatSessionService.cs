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
        private ChatSession _activeSession;
        private bool _activeSessionPersisted;
        internal Func<string, ChatRunSnapshot> RunStateProvider { get; set; }
        internal Func<IReadOnlyList<ChatSession>> RunSessionsProvider { get; set; }

        public ChatSessionService(IOfficeApplicationAdapter adapter, ChatStore chatStore)
        {
            _adapter = adapter;
            _chatStore = chatStore;
        }

        public void ReconcileInterruptedRuns(string runtimeId)
        {
            foreach (var session in _chatStore.List())
            {
                var run = session == null ? null : session.LastRun;
                var unfinished = run != null &&
                    (string.Equals(run.Status, "running", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(run.Status, "cancelling", StringComparison.OrdinalIgnoreCase));
                if (!unfinished || string.Equals(run.RuntimeId, runtimeId, StringComparison.Ordinal))
                {
                    continue;
                }

                run.Status = "cancelled";
                run.Phase = "cancelled";
                run.CurrentAction = "Приложение было перезапущено.";
                session.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    RunId = run.RunId,
                    Content = "Предыдущий запуск был прерван перезапуском приложения.",
                    Activity = new ChatActivity
                    {
                        RunId = run.RunId,
                        Kind = "diagnostic",
                        Title = "Запуск прерван",
                        Status = "cancelled",
                        ExecutionStatus = "application_restarted",
                        ResultMessage = "Приложение было перезапущено до завершения запроса."
                    }
                });
                _chatStore.Save(session);
            }
        }

        public void Reset()
        {
            _activeSessionId = null;
            _activeHost = null;
            _activeDocumentKey = null;
            _activeRuntimeDocumentKey = null;
            _activeSession = null;
            _activeSessionPersisted = false;
        }

        public ChatSession LoadSession(string requestedSessionId)
        {
            return LoadSession(requestedSessionId, false);
        }

        public ChatSession LoadAddressedSession(string requestedSessionId)
        {
            return string.IsNullOrWhiteSpace(requestedSessionId)
                ? LoadSession(null)
                : LoadSession(requestedSessionId, false);
        }

        public ChatSession LoadSession(string requestedSessionId, bool allowMissingRequestedFallback)
        {
            var host = _adapter.HostName;
            var documentKey = _adapter.DocumentKey;
            var runtimeKey = _adapter.RuntimeDocumentKey;
            var title = _adapter.DocumentTitle;

            if (!string.IsNullOrWhiteSpace(_activeSessionId) &&
                string.Equals(_activeHost, host, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_activeRuntimeDocumentKey, runtimeKey, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(_activeDocumentKey, documentKey, StringComparison.OrdinalIgnoreCase))
            {
                var oldDocumentKey = _activeDocumentKey;
                if (_chatStore.IsPersisted(_activeSession))
                {
                    var activeSessionId = _activeSessionId;
                    _chatStore.MoveDocument(_activeHost, oldDocumentKey, host, documentKey, title);
                    _activeSession = _chatStore.Load(host, documentKey, activeSessionId) ?? _activeSession;
                }
                else if (_activeSession != null)
                {
                    _activeSession.Host = host;
                    _activeSession.DocumentKey = documentKey;
                    _activeSession.DocumentTitle = title;
                    if (_activeSession.Context != null)
                    {
                        _activeSession.Context.Host = host;
                        _activeSession.Context.DocumentKey = documentKey;
                    }
                }
                _activeHost = host;
                _activeDocumentKey = documentKey;
                _activeRuntimeDocumentKey = runtimeKey;
            }

            ChatSession session = null;
            if (!string.IsNullOrWhiteSpace(requestedSessionId))
            {
                if (RunStateProvider != null)
                {
                    var running = RunStateProvider(requestedSessionId);
                    if (running != null) session = running.Session;
                }
                if (_activeSession != null &&
                    session == null &&
                    string.Equals(requestedSessionId, _activeSessionId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(_activeHost, host, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(_activeDocumentKey, documentKey, StringComparison.OrdinalIgnoreCase))
                {
                    session = _activeSession;
                }
                if (session == null)
                {
                    session = _chatStore.Load(host, documentKey, requestedSessionId);
                }
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
                session = _activeSession ?? _chatStore.Load(host, documentKey, _activeSessionId);
            }

            if (session == null)
            {
                session = _chatStore.LoadOrCreateActive(host, documentKey, title);
            }

            session.Mode = ChatModes.Normalize(session.Mode);
            SetActiveSession(session);
            UpdateCurrentDocumentMetadata(session);
            return session;
        }

        public ChatSession CreateChat(string title)
        {
            LoadSession(null);
            var session = _chatStore.CreateTransient(
                _adapter.HostName,
                _adapter.DocumentKey,
                _adapter.DocumentTitle,
                string.IsNullOrWhiteSpace(title) ? "New chat" : title.Trim());
            UpdateCurrentDocumentMetadata(session);
            SetActiveSession(session);
            return session;
        }

        public ChatSession CreateChatForDocument(string title, string host, string documentKey, string documentTitle, string documentPath)
        {
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(documentKey) ||
                string.Equals(host, _adapter.HostName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(documentKey, _adapter.DocumentKey, StringComparison.OrdinalIgnoreCase))
            {
                return CreateChat(title);
            }

            var session = _chatStore.CreateTransient(
                host.Trim(),
                documentKey.Trim(),
                string.IsNullOrWhiteSpace(documentTitle) ? "Document" : documentTitle.Trim(),
                string.IsNullOrWhiteSpace(title) ? "New chat" : title.Trim());
            session.DocumentPath = string.IsNullOrWhiteSpace(documentPath) ? null : documentPath.Trim();
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
                    ?? _chatStore.CreateTransient(_adapter.HostName, _adapter.DocumentKey, _adapter.DocumentTitle, "New chat");
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
            _activeSession = session;
            _activeHost = session.Host;
            _activeDocumentKey = session.DocumentKey;
            _activeRuntimeDocumentKey = IsCurrentDocument(session) ? _adapter.RuntimeDocumentKey : null;
            _activeSessionPersisted = _chatStore.IsPersisted(session);
            if (_activeSessionPersisted)
            {
                _chatStore.SaveActiveSessionId(session.Host, session.DocumentKey, _activeSessionId);
            }
        }

        public void NotifySaved(ChatSession session)
        {
            if (session == null ||
                !string.Equals(ChatStore.GetSessionId(session), _activeSessionId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            _activeSession = session;
            _activeSessionPersisted = true;
            _chatStore.SaveActiveSessionId(session.Host, session.DocumentKey, _activeSessionId);
        }

        internal bool TryApplyGeneratedTitle(
            string host,
            string documentKey,
            string sessionId,
            string expectedCurrentTitle,
            string generatedTitle)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(generatedTitle))
            {
                return false;
            }

            var running = RunStateProvider == null ? null : RunStateProvider(sessionId);
            var session = running == null ? null : running.Session;
            if (session == null && _activeSession != null &&
                string.Equals(_activeSessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            {
                session = _activeSession;
            }
            if (session == null)
            {
                session = _chatStore.Load(host, documentKey, sessionId) ?? _chatStore.Load(sessionId);
            }
            if (!ChatTitleBuilder.CanReplaceAutoTitle(session, expectedCurrentTitle))
            {
                return false;
            }

            session.Title = generatedTitle.Trim();
            if (running == null)
            {
                _chatStore.Save(session);
            }
            if (string.Equals(_activeSessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            {
                _activeSession = session;
                _activeSessionPersisted = _chatStore.IsPersisted(session);
            }
            return true;
        }

        public ChatSession GetActiveSession()
        {
            if (_activeSession == null)
            {
                return null;
            }

            if (RunStateProvider != null)
            {
                var running = RunStateProvider(_activeSessionId);
                if (running != null && running.Session != null)
                {
                    _activeSession = running.Session;
                    return _activeSession;
                }
            }

            if (_activeSessionPersisted)
            {
                var stored = _chatStore.Load(_activeSessionId);
                if (stored == null)
                {
                    Reset();
                    return null;
                }
                _activeSession = stored;
            }

            return _activeSession;
        }

        public IReadOnlyList<ChatSessionSummary> GetChatSummaries(string activeId)
        {
            var sessions = _chatStore.List().ToList();
            foreach (var running in RunSessionsProvider == null ? new ChatSession[0] : RunSessionsProvider())
            {
                var runningId = ChatStore.GetSessionId(running);
                var storedIndex = sessions.FindIndex(item =>
                    string.Equals(ChatStore.GetSessionId(item), runningId, StringComparison.OrdinalIgnoreCase));
                if (storedIndex >= 0)
                {
                    sessions[storedIndex] = running;
                }
                else
                {
                    sessions.Insert(0, running);
                }
            }
            if (_activeSession != null && !_activeSessionPersisted &&
                string.Equals(ChatStore.GetSessionId(_activeSession), activeId, StringComparison.OrdinalIgnoreCase) &&
                sessions.All(item => !string.Equals(ChatStore.GetSessionId(item), activeId, StringComparison.OrdinalIgnoreCase)))
            {
                sessions.Insert(0, _activeSession);
            }

            return sessions.Select(ToSummary).ToList();
        }

        private ChatSessionSummary ToSummary(ChatSession session)
        {
            var id = ChatStore.GetSessionId(session);
            var run = RunStateProvider == null ? null : RunStateProvider(id);
            return new ChatSessionSummary
            {
                Id = id,
                Host = session.Host,
                DocumentKey = session.DocumentKey,
                DocumentTitle = session.DocumentTitle,
                DocumentPath = ResolveDocumentPath(session),
                Title = session.Title,
                Model = session.Model,
                Mode = ChatModes.Normalize(session.Mode),
                HtmlModeEnabled = session.HtmlModeEnabled,
                HasHtmlWorkspace = HasHtmlWorkspace(session.HtmlWorkspace),
                HtmlFileCount = session.HtmlWorkspace == null || session.HtmlWorkspace.Files == null ? 0 : session.HtmlWorkspace.Files.Count,
                HtmlDataSourceCount = session.HtmlWorkspace == null || session.HtmlWorkspace.DataSources == null ? 0 : session.HtmlWorkspace.DataSources.Count,
                CreatedUtc = session.CreatedUtc,
                UpdatedUtc = session.UpdatedUtc,
                MessageCount = session.Messages == null ? 0 : session.Messages.Count,
                IsCurrentDocument = IsCurrentDocument(session),
                RunId = run == null ? (session.LastRun == null ? null : session.LastRun.RunId) : run.RunId,
                RunStatus = run == null ? (session.LastRun == null ? null : session.LastRun.Status) : run.Status,
                RunPhase = run == null ? (session.LastRun == null ? null : session.LastRun.Phase) : run.Phase,
                RunStartedUtc = run == null ? (session.LastRun == null ? (DateTime?)null : session.LastRun.StartedUtc) : run.StartedUtc
            };
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
                if (_chatStore.IsPersisted(session))
                {
                    _chatStore.Save(session);
                }
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
