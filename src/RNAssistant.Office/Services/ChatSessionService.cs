using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
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
            foreach (var header in _chatStore.ListHeaders())
            {
                if (!IsUnfinishedRun(header.RunStatus) || string.Equals(header.RunRuntimeId, runtimeId, StringComparison.Ordinal))
                {
                    continue;
                }

                var session = _chatStore.Load(header.Host, header.DocumentKey, header.Id);
                var run = session == null ? null : session.LastRun;
                if (run == null || !IsUnfinishedRun(run.Status) || string.Equals(run.RuntimeId, runtimeId, StringComparison.Ordinal))
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

        private static bool IsUnfinishedRun(string status)
        {
            return string.Equals(status, "running", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "cancelling", StringComparison.OrdinalIgnoreCase);
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
                    !allowMissingRequestedFallback &&
                    string.Equals(requestedSessionId, _activeSessionId, StringComparison.OrdinalIgnoreCase))
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
            var nextHeader = _chatStore.ListHeaders(host, documentKey, documentTitle).FirstOrDefault();
            var next = nextHeader == null ? null : _chatStore.Load(host, documentKey, nextHeader.Id);
            if (next == null)
            {
                nextHeader = _chatStore.ListHeaders(_adapter.HostName, _adapter.DocumentKey, _adapter.DocumentTitle).FirstOrDefault();
                next = nextHeader == null
                    ? _chatStore.CreateTransient(_adapter.HostName, _adapter.DocumentKey, _adapter.DocumentTitle, "New chat")
                    : _chatStore.Load(_adapter.HostName, _adapter.DocumentKey, nextHeader.Id);
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

            _activeSessionId = session.Id;
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
                !string.Equals(session.Id, _activeSessionId, StringComparison.OrdinalIgnoreCase))
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
            var summaries = _chatStore.ListHeaders().Select(ToSummary).ToList();
            foreach (var running in RunSessionsProvider == null ? new ChatSession[0] : RunSessionsProvider())
            {
                var runningId = running.Id;
                var storedIndex = summaries.FindIndex(item =>
                    string.Equals(item.Id, runningId, StringComparison.OrdinalIgnoreCase));
                var runningSummary = ToSummary(running);
                if (storedIndex >= 0)
                {
                    summaries[storedIndex] = runningSummary;
                }
                else
                {
                    summaries.Insert(0, runningSummary);
                }
            }
            if (_activeSession != null && !_activeSessionPersisted &&
                string.Equals(_activeSession.Id, activeId, StringComparison.OrdinalIgnoreCase) &&
                summaries.All(item => !string.Equals(item.Id, activeId, StringComparison.OrdinalIgnoreCase)))
            {
                summaries.Insert(0, ToSummary(_activeSession));
            }

            return summaries;
        }

        private ChatSessionSummary ToSummary(ChatSession session)
        {
            return ToSummary(ChatSessionHeaderFactory.Create(session));
        }

        private ChatSessionSummary ToSummary(ChatSessionHeader header)
        {
            var id = header.Id;
            var run = RunStateProvider == null ? null : RunStateProvider(id);
            return new ChatSessionSummary
            {
                Id = id,
                Host = header.Host,
                DocumentKey = header.DocumentKey,
                DocumentTitle = header.DocumentTitle,
                DocumentPath = ResolveDocumentPath(header.DocumentPath, header.DocumentKey),
                Title = header.Title,
                Model = header.Model,
                Mode = ChatModes.Normalize(header.Mode),
                HtmlModeEnabled = header.HtmlModeEnabled,
                ReasoningEnabled = header.ReasoningEnabled,
                HasHtmlWorkspace = header.HasHtmlWorkspace,
                HtmlFileCount = header.HtmlFileCount,
                HtmlDataSourceCount = header.HtmlDataSourceCount,
                CreatedUtc = header.CreatedUtc,
                UpdatedUtc = header.UpdatedUtc,
                MessageCount = header.MessageCount,
                IsCurrentDocument = IsCurrentDocument(header.Host, header.DocumentKey),
                RunId = run == null ? header.RunId : run.RunId,
                RunStatus = run == null ? header.RunStatus : run.Status,
                RunPhase = run == null ? header.RunPhase : run.Phase,
                RunStartedUtc = run == null ? header.RunStartedUtc : (DateTime?)run.StartedUtc
            };
        }

        public bool IsCurrentDocument(ChatSession session)
        {
            return session != null && IsCurrentDocument(session.Host, session.DocumentKey);
        }

        private bool IsCurrentDocument(string host, string documentKey)
        {
            return string.Equals(host, _adapter.HostName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(documentKey, _adapter.DocumentKey, StringComparison.OrdinalIgnoreCase);
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
            return session == null ? string.Empty : ResolveDocumentPath(session.DocumentPath, session.DocumentKey);
        }

        private static string ResolveDocumentPath(string documentPath, string documentKey)
        {
            if (!string.IsNullOrWhiteSpace(documentPath))
            {
                return documentPath;
            }

            var marker = ":Path:";
            var key = documentKey ?? string.Empty;
            var markerIndex = key.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                return key.Substring(markerIndex + marker.Length);
            }
            return Path.IsPathRooted(key) ? key : string.Empty;
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
