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
        private readonly VbaJournalStore _vbaJournalStore;
        private string _activeSessionId;
        private string _activeHost;
        private string _activeDocumentKey;
        private string _activeRuntimeDocumentKey;
        private ChatSession _activeSession;
        private bool _activeSessionPersisted;
        private string _observedHost;
        private string _observedDocumentKey;
        private string _observedRuntimeDocumentKey;
        private string _aliasReconciledHost;
        private string _aliasReconciledDocumentKey;
        private string _aliasReconciledRuntimeDocumentKey;
        private string _aliasReconciledDocumentPath;
        private bool _aliasReconciliationPending;
        internal Func<string, ChatRunSnapshot> RunStateProvider { get; set; }
        internal Func<string, ChatRunSnapshot> RunStatusProvider { get; set; }
        internal Func<IReadOnlyList<ChatSession>> RunSessionsProvider { get; set; }
        internal Func<string, bool> RunOwnershipProvider { get; set; }
        internal Func<ChatSession, IDisposable> RunRecoveryLeaseProvider { get; set; }
        internal Func<IDisposable> MaintenanceLeaseProvider { get; set; }

        public ChatSessionService(IOfficeApplicationAdapter adapter, ChatStore chatStore)
            : this(adapter, chatStore, null)
        {
        }

        public ChatSessionService(IOfficeApplicationAdapter adapter, ChatStore chatStore, VbaJournalStore vbaJournalStore)
        {
            _adapter = adapter;
            _chatStore = chatStore;
            _vbaJournalStore = vbaJournalStore;
        }

        public void ReconcileInterruptedRuns(string runtimeId)
        {
            foreach (var header in _chatStore.ListHeaders())
            {
                if (!IsUnfinishedRun(header.RunStatus))
                {
                    continue;
                }
                if (RunOwnershipProvider != null)
                {
                    if (RunOwnershipProvider(header.Id)) continue;
                }
                else if (string.Equals(header.RunRuntimeId, runtimeId, StringComparison.Ordinal))
                {
                    continue;
                }

                var session = _chatStore.Load(header.Host, header.DocumentKey, header.Id);
                var run = session == null ? null : session.LastRun;
                if (run == null || !IsUnfinishedRun(run.Status))
                {
                    continue;
                }

                IDisposable recoveryLease = null;
                try
                {
                    if (RunRecoveryLeaseProvider != null)
                    {
                        try
                        {
                            recoveryLease = RunRecoveryLeaseProvider(session);
                        }
                        catch (InvalidOperationException)
                        {
                            continue;
                        }
                        if (recoveryLease == null) continue;
                    }
                    else if (RunOwnershipProvider != null && RunOwnershipProvider(header.Id))
                    {
                        continue;
                    }

                    // Ownership may have changed between the initial scan and lease acquisition.
                    session = _chatStore.Load(header.Host, header.DocumentKey, header.Id);
                    run = session == null ? null : session.LastRun;
                    if (run == null || !IsUnfinishedRun(run.Status))
                    {
                        continue;
                    }

                    var effectMayBeUnknown = _chatStore.HasOpenToolExecution(session, run.RunId);
                    _chatStore.CloseOpenSteps(
                        session,
                        run.RunId,
                        "interrupted",
                        "Runtime stopped before the model step reached a terminal event.");
                    MarkInterruptedActivities(session, run, effectMayBeUnknown);
                    if (effectMayBeUnknown)
                    {
                        ExcludeInterruptedProtocolMessages(session, run);
                    }
                    run.Status = "interrupted";
                    run.Phase = "interrupted";
                    run.CurrentAction = effectMayBeUnknown
                        ? "Предыдущий процесс завершился во время выполнения действия."
                        : "Предыдущий процесс завершился после сохранённой границы.";
                    session.Messages.Add(new ChatMessage
                    {
                        Role = "assistant",
                        RunId = run.RunId,
                        Content = effectMayBeUnknown
                            ? "Предыдущий запуск был прерван. Результат выполнявшегося действия неизвестен; проверьте документ перед ручным повтором."
                            : "Предыдущий запуск был прерван после сохранённой границы. Сохранённые результаты оставлены в истории; автоматическое продолжение не выполнялось.",
                        Activity = new ChatActivity
                        {
                            RunId = run.RunId,
                            Kind = "diagnostic",
                            Title = "Запуск прерван",
                            Status = "failed",
                            ExecutionStatus = effectMayBeUnknown ? "interrupted_unknown" : "interrupted",
                            Retryable = false,
                            ResultMessage = effectMayBeUnknown
                                ? "Процесс завершился до сохранения окончательного результата. Автоматический повтор отключён."
                                : "Запуск завершился до финального ответа. Сохранённые результаты не повторялись автоматически."
                        }
                    });
                    try
                    {
                        _chatStore.Save(session);
                    }
                    catch (ChatConcurrencyException)
                    {
                        // Another writer updated this chat before recovery acquired canonical state.
                    }
                }
                finally
                {
                    if (recoveryLease != null) recoveryLease.Dispose();
                }
            }
        }

        private static void MarkInterruptedActivities(ChatSession session, ChatRunRecord run, bool effectMayBeUnknown)
        {
            foreach (var message in session == null || session.Messages == null
                ? new List<ChatMessage>()
                : session.Messages)
            {
                if (!BelongsToRun(message, run)) continue;
                MarkInterruptedActivity(message.Activity, effectMayBeUnknown);
            }
        }

        private static void MarkInterruptedActivity(ChatActivity activity, bool effectMayBeUnknown)
        {
            if (activity == null) return;
            if (string.Equals(activity.Status, "running", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(activity.ExecutionStatus, "running", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(activity.ExecutionStatus, "executing", StringComparison.OrdinalIgnoreCase))
            {
                activity.Status = "failed";
                activity.ExecutionStatus = effectMayBeUnknown ? "interrupted_unknown" : "interrupted";
                activity.Retryable = false;
                activity.PendingId = null;
                activity.ConfirmationCatalogSha256 = null;
                activity.ResultMessage = effectMayBeUnknown
                    ? "Execution was interrupted; the external effect is unknown."
                    : "Execution was interrupted after the last persisted result.";
            }
            foreach (var child in activity.Children ?? new List<ChatActivity>())
            {
                MarkInterruptedActivity(child, effectMayBeUnknown);
            }
        }

        private static void ExcludeInterruptedProtocolMessages(ChatSession session, ChatRunRecord run)
        {
            var messages = session == null ? null : session.Messages;
            if (messages == null || run == null || string.IsNullOrWhiteSpace(run.RunId)) return;
            foreach (var message in messages.Where(item =>
                item != null && item.ProtocolMessage && BelongsToRun(item, run)))
            {
                // A crashed run may have crossed an external side-effect boundary even when a
                // matching result was persisted. Keep it visible, but replay none of its protocol.
                message.ExcludeFromModelContext = true;
            }
        }

        private static bool BelongsToRun(ChatMessage message, ChatRunRecord run)
        {
            if (message == null || run == null) return false;
            if (string.Equals(message.RunId, run.RunId, StringComparison.OrdinalIgnoreCase)) return true;
            return string.IsNullOrWhiteSpace(message.RunId) &&
                run.StartedUtc != default(DateTime) &&
                message.CreatedUtc >= run.StartedUtc;
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
            _observedHost = null;
            _observedDocumentKey = null;
            _observedRuntimeDocumentKey = null;
            _aliasReconciledHost = null;
            _aliasReconciledDocumentKey = null;
            _aliasReconciledRuntimeDocumentKey = null;
            _aliasReconciledDocumentPath = null;
            _aliasReconciliationPending = false;
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
            var documentPath = CurrentDocumentPath();

            var activeDocumentKeyChanged = !string.IsNullOrWhiteSpace(_activeSessionId) &&
                string.Equals(_activeHost, host, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_activeRuntimeDocumentKey, runtimeKey, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(_activeDocumentKey, documentKey, StringComparison.OrdinalIgnoreCase);
            var migrationDeferred = false;
            IDisposable migrationLease = null;
            try
            {
                if (activeDocumentKeyChanged && MaintenanceLeaseProvider != null)
                {
                    migrationLease = MaintenanceLeaseProvider();
                }
                migrationDeferred = activeDocumentKeyChanged && IsDocumentRunOwned(_activeHost, _activeDocumentKey);
                if (activeDocumentKeyChanged && !migrationDeferred)
                {
                    var oldDocumentKey = _activeDocumentKey;
                    if (_vbaJournalStore != null)
                    {
                        _vbaJournalStore.MoveDocument(
                            _activeHost,
                            oldDocumentKey,
                            host,
                            documentKey,
                            runtimeKey,
                            title);
                    }
                    if (_chatStore.IsPersisted(_activeSession))
                    {
                        var activeSessionId = _activeSessionId;
                        _chatStore.MoveDocument(_activeHost, oldDocumentKey, host, documentKey, title, documentPath);
                        _activeSession = _chatStore.Load(host, documentKey, activeSessionId) ?? _activeSession;
                    }
                    else if (_activeSession != null)
                    {
                        ChatSessionNormalizer.RecordDocumentKeyMigration(
                            _activeSession,
                            oldDocumentKey,
                            documentKey);
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
            }
            finally
            {
                if (migrationLease != null) migrationLease.Dispose();
            }

            if (!AliasesReconciled(host, documentKey, runtimeKey, documentPath))
            {
                _aliasReconciliationPending = !ReconcileDocumentAliases(host, documentKey, title, documentPath);
                if (!_aliasReconciliationPending)
                {
                    _aliasReconciledHost = host;
                    _aliasReconciledDocumentKey = documentKey;
                    _aliasReconciledRuntimeDocumentKey = runtimeKey;
                    _aliasReconciledDocumentPath = documentPath;
                }
            }
            ObserveCurrentDocument(host, documentKey, runtimeKey);

            ChatSession session = null;
            if (!string.IsNullOrWhiteSpace(requestedSessionId))
            {
                if (RunStateProvider != null)
                {
                    var running = RunStateProvider(requestedSessionId);
                    if (running != null) session = running.Session;
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
                if (session == null &&
                    _activeSession != null &&
                    !allowMissingRequestedFallback &&
                    string.Equals(requestedSessionId, _activeSessionId, StringComparison.OrdinalIgnoreCase))
                {
                    session = _activeSession;
                }
                if (session == null && !allowMissingRequestedFallback)
                {
                    throw new InvalidOperationException("Chat session was not found.");
                }
            }
            else if (!string.IsNullOrWhiteSpace(_activeSessionId) &&
                     string.Equals(_activeHost, host, StringComparison.OrdinalIgnoreCase) &&
                     (string.Equals(_activeDocumentKey, documentKey, StringComparison.OrdinalIgnoreCase) || migrationDeferred))
            {
                var running = RunStateProvider == null ? null : RunStateProvider(_activeSessionId);
                session = running == null
                    ? (_activeSessionPersisted
                        ? _chatStore.Load(_activeHost, _activeDocumentKey, _activeSessionId)
                        : _activeSession)
                    : running.Session;
            }

            if (session == null)
            {
                session = _chatStore.LoadOrCreateActive(host, documentKey, title);
            }

            session.Mode = ChatModes.Normalize(session.Mode);
            if (migrationDeferred)
            {
                _activeSession = session;
                _activeSessionPersisted = _chatStore.IsPersisted(session);
            }
            else
            {
                SetActiveSession(session);
            }
            UpdateCurrentDocumentMetadata(session, documentPath);
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

            if (IsRunOwned(sessionId))
            {
                return false;
            }
            var session = _chatStore.Load(host, documentKey, sessionId) ?? _chatStore.Load(sessionId);
            if (!ChatTitleBuilder.CanReplaceAutoTitle(session, expectedCurrentTitle))
            {
                return false;
            }

            session.Title = generatedTitle.Trim();
            try
            {
                _chatStore.Save(session);
            }
            catch (ChatConcurrencyException)
            {
                return false;
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
                var stored = _chatStore.Load(_activeHost, _activeDocumentKey, _activeSessionId);
                if (stored == null)
                {
                    Reset();
                    return null;
                }
                _activeSession = stored;
            }

            return _activeSession;
        }

        public ChatSession GetActiveSessionForOfficeState()
        {
            var host = _adapter.HostName;
            var documentKey = _adapter.DocumentKey;
            var runtimeKey = _adapter.RuntimeDocumentKey;
            if (CurrentDocumentChanged(host, documentKey, runtimeKey) || _aliasReconciliationPending)
            {
                return LoadSession(null);
            }

            return GetActiveSession() ?? LoadSession(null);
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
                    CopyStorageUsage(summaries[storedIndex], runningSummary);
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
            var run = RunStatusProvider == null
                ? (RunStateProvider == null ? null : RunStateProvider(id))
                : RunStatusProvider(id);
            return new ChatSessionSummary
            {
                Id = id,
                Revision = header.Revision,
                Host = header.Host,
                DocumentKey = header.DocumentKey,
                DocumentTitle = header.DocumentTitle,
                DocumentPath = ResolveDocumentPath(header.DocumentPath, header.DocumentKey),
                Title = header.Title,
                Model = header.Model,
                Mode = ChatModes.Normalize(header.Mode),
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
                RunStartedUtc = run == null ? header.RunStartedUtc : (DateTime?)run.StartedUtc,
                JsonlByteLength = header.JsonlByteLength,
                CasBlobCount = header.CasBlobCount,
                CasLogicalByteLength = header.CasLogicalByteLength,
                CasStoredByteLength = header.CasStoredByteLength,
                CasMissingBlobCount = header.CasMissingBlobCount,
                CasReferenceIssueCount = header.CasReferenceIssueCount,
                StorageWarningLevel = string.IsNullOrWhiteSpace(header.StorageWarningLevel)
                    ? ChatStorageWarningLevels.None
                    : header.StorageWarningLevel
            };
        }

        private static void CopyStorageUsage(ChatSessionSummary source, ChatSessionSummary target)
        {
            if (source == null || target == null) return;
            target.JsonlByteLength = source.JsonlByteLength;
            target.CasBlobCount = source.CasBlobCount;
            target.CasLogicalByteLength = source.CasLogicalByteLength;
            target.CasStoredByteLength = source.CasStoredByteLength;
            target.CasMissingBlobCount = source.CasMissingBlobCount;
            target.CasReferenceIssueCount = source.CasReferenceIssueCount;
            target.StorageWarningLevel = source.StorageWarningLevel;
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
            UpdateCurrentDocumentMetadata(session, CurrentDocumentPath());
        }

        private void UpdateCurrentDocumentMetadata(ChatSession session, string path)
        {
            if (!IsCurrentDocument(session))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(path) && !string.Equals(session.DocumentPath, path, StringComparison.OrdinalIgnoreCase))
            {
                var persisted = _chatStore.IsPersisted(session);
                if (persisted && IsRunOwned(session.Id))
                {
                    return;
                }
                var previousPath = session.DocumentPath;
                session.DocumentPath = path;
                if (persisted)
                {
                    try
                    {
                        _chatStore.Save(session);
                    }
                    catch (ChatConcurrencyException)
                    {
                        session.DocumentPath = previousPath;
                    }
                }
            }
        }

        private string CurrentDocumentPath()
        {
            var provider = _adapter as IOfficeContextProvider;
            var officeContext = provider == null ? null : provider.GetOfficeContext();
            return officeContext == null ? string.Empty : officeContext.DocumentPath;
        }

        private bool CurrentDocumentChanged(string host, string documentKey, string runtimeKey)
        {
            if (string.IsNullOrWhiteSpace(_observedHost))
            {
                return true;
            }
            if (!string.Equals(_observedHost, host, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_observedDocumentKey, documentKey, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(_observedRuntimeDocumentKey) &&
                !string.IsNullOrWhiteSpace(runtimeKey) &&
                !string.Equals(_observedRuntimeDocumentKey, runtimeKey, StringComparison.OrdinalIgnoreCase);
        }

        private void ObserveCurrentDocument(string host, string documentKey, string runtimeKey)
        {
            _observedHost = host;
            _observedDocumentKey = documentKey;
            _observedRuntimeDocumentKey = runtimeKey;
        }

        private bool AliasesReconciled(string host, string documentKey, string runtimeKey, string documentPath)
        {
            return string.Equals(_aliasReconciledHost, host, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_aliasReconciledDocumentKey, documentKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_aliasReconciledRuntimeDocumentKey, runtimeKey, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(_aliasReconciledDocumentPath) && string.IsNullOrWhiteSpace(documentPath) ||
                 DocumentOpenService.SamePath(_aliasReconciledDocumentPath, documentPath));
        }

        private bool ReconcileDocumentAliases(string host, string documentKey, string documentTitle, string documentPath)
        {
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(documentKey) ||
                string.IsNullOrWhiteSpace(documentPath))
            {
                return true;
            }

            var aliases = _chatStore.ListHeaders()
                .Where(header => header != null &&
                    string.Equals(header.Host, host, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(header.DocumentKey, documentKey, StringComparison.OrdinalIgnoreCase) &&
                    DocumentOpenService.SamePath(
                        ResolveDocumentPath(header.DocumentPath, header.DocumentKey),
                        documentPath))
                .GroupBy(header => header.DocumentKey, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Max(header => header.UpdatedUtc))
                .ToList();
            if (aliases.Count == 0)
            {
                return true;
            }
            if (IsDocumentRunOwned(host, documentKey))
            {
                return false;
            }

            IDisposable lease = null;
            var complete = true;
            try
            {
                if (MaintenanceLeaseProvider != null)
                {
                    try
                    {
                        lease = MaintenanceLeaseProvider();
                    }
                    catch (InvalidOperationException)
                    {
                        return false;
                    }
                    if (lease == null) return false;
                }
                if (IsDocumentRunOwned(host, documentKey))
                {
                    return false;
                }

                var preferredActiveId = aliases.SelectMany(alias => alias)
                    .Any(header => string.Equals(header.Id, _activeSessionId, StringComparison.OrdinalIgnoreCase))
                    ? _activeSessionId
                    : _chatStore.LoadActiveSessionId(host, documentKey);
                if (string.IsNullOrWhiteSpace(preferredActiveId))
                {
                    var newest = aliases[aliases.Count - 1];
                    preferredActiveId = _chatStore.LoadActiveSessionId(host, newest.Key);
                    if (string.IsNullOrWhiteSpace(preferredActiveId) ||
                        newest.All(header => !string.Equals(header.Id, preferredActiveId, StringComparison.OrdinalIgnoreCase)))
                    {
                        preferredActiveId = newest.OrderByDescending(header => header.UpdatedUtc).Select(header => header.Id).FirstOrDefault();
                    }
                }

                foreach (var alias in aliases)
                {
                    if (IsDocumentRunOwned(host, alias.Key))
                    {
                        complete = false;
                        continue;
                    }

                    var sourceActiveId = _chatStore.LoadActiveSessionId(host, alias.Key);
                    foreach (var header in alias.OrderBy(header => header.UpdatedUtc))
                    {
                        if (IsRunOwned(header.Id))
                        {
                            complete = false;
                            continue;
                        }

                        try
                        {
                            var session = _chatStore.Load(host, alias.Key, header.Id);
                            if (session == null || !DocumentOpenService.SamePath(
                                ResolveDocumentPath(session),
                                documentPath))
                            {
                                complete = false;
                                continue;
                            }
                            session.DocumentPath = documentPath.Trim();
                            _chatStore.Move(session, host, documentKey, documentTitle);
                        }
                        catch (ChatConcurrencyException)
                        {
                            // Another window changed this history; retry reconciliation on a later refresh.
                            complete = false;
                        }
                    }

                    var remainingHeaders = _chatStore.ListHeaders(host, alias.Key, string.Empty);
                    if (remainingHeaders.Any(header => DocumentOpenService.SamePath(
                        ResolveDocumentPath(header.DocumentPath, header.DocumentKey),
                        documentPath)))
                    {
                        complete = false;
                    }
                    if (!string.IsNullOrWhiteSpace(sourceActiveId) &&
                        remainingHeaders.All(header => !string.Equals(
                            header.Id,
                            sourceActiveId,
                            StringComparison.OrdinalIgnoreCase)))
                    {
                        var remaining = remainingHeaders.FirstOrDefault();
                        _chatStore.SaveActiveSessionId(host, alias.Key, remaining == null ? string.Empty : remaining.Id);
                    }
                }

                if (!string.IsNullOrWhiteSpace(preferredActiveId) &&
                    _chatStore.Load(host, documentKey, preferredActiveId) != null)
                {
                    _chatStore.SaveActiveSessionId(host, documentKey, preferredActiveId);
                }
                return complete;
            }
            finally
            {
                if (lease != null) lease.Dispose();
            }
        }

        private bool IsRunOwned(string sessionId)
        {
            return RunOwnershipProvider != null
                ? RunOwnershipProvider(sessionId)
                : RunStateProvider != null && RunStateProvider(sessionId) != null;
        }

        private bool IsDocumentRunOwned(string host, string documentKey)
        {
            var runningSessions = RunSessionsProvider == null
                ? new ChatSession[0]
                : RunSessionsProvider();
            if (runningSessions.Any(session => session != null &&
                string.Equals(session.Host, host, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(session.DocumentKey, documentKey, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return _chatStore.ListHeaders(host, documentKey, string.Empty)
                .Any(header => header != null && IsRunOwned(header.Id));
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
