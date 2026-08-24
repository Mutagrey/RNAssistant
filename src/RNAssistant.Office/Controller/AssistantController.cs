using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Diagnostics;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController : IDisposable
    {
        private readonly string _runtimeId = Guid.NewGuid().ToString("N");
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly AppDataPaths _paths;
        private readonly SettingsService _settingsService;
        private readonly ChatStore _chatStore;
        private readonly AttachmentStore _attachmentStore;
        private readonly ToolStore _toolStore;
        private readonly SkillStore _skillStore;
        private readonly VbaBackupStore _vbaBackupStore;
        private readonly OfficeToolExecutor _toolExecutor;
        private readonly ToolCatalogService _toolCatalog;
        private readonly SkillCatalogService _skillCatalog;
        private readonly ChatSessionService _chatSessions;
        private readonly ChatHistoryEditService _chatHistoryEditService;
        private readonly AgentRunService _agentRunService;
        private readonly PlainChatService _plainChatService;
        private readonly ContextCompactionService _contextCompactionService;
        private readonly ContextService _contextService;
        private readonly LlmClient _llmClient;
        private readonly LlmCompletionDelegate _llmCompletion;
        private readonly object _syncRoot;
        private readonly Dictionary<string, PendingAgentTool> _pendingAgentTools;
        private readonly ChatRunRegistry _chatRuns;
        private readonly HtmlNetworkService _htmlNetwork;
        private readonly CancellationTokenSource _lifetimeCancellation;
        private int _disposed;
        private string _queuedQuickAction;

        public AssistantController(IOfficeApplicationAdapter adapter)
            : this(adapter, null, null)
        {
        }

        internal AssistantController(
            IOfficeApplicationAdapter adapter,
            AppDataPaths paths,
            Func<AppSettings, IEnumerable<ChatMessage>, CancellationToken, Task<LlmCompletionResult>> completeAsync)
        {
            _adapter = adapter;
            _paths = paths ?? AppDataPaths.CreateDefault();
            RuntimeLog.Configure(_paths.Root);
            _settingsService = new SettingsService(_paths);
            _chatStore = new ChatStore(_paths);
            _attachmentStore = new AttachmentStore(_paths);
            _toolStore = new ToolStore(_paths);
            _skillStore = new SkillStore(_paths);
            _vbaBackupStore = new VbaBackupStore(_paths);
            _toolExecutor = new OfficeToolExecutor(
                _adapter,
                _vbaBackupStore,
                _skillStore,
                _toolStore,
                () => _settingsService.Load(),
                settings => _settingsService.Save(settings),
                _paths);
            _toolCatalog = new ToolCatalogService(_adapter, _toolExecutor, _toolStore);
            _skillCatalog = new SkillCatalogService(_adapter, _skillStore);
            _chatRuns = new ChatRunRegistry(_paths);
            _chatSessions = new ChatSessionService(_adapter, _chatStore);
            _lifetimeCancellation = new CancellationTokenSource();
            _chatSessions.RunStateProvider = _chatRuns.Get;
            _chatSessions.RunStatusProvider = _chatRuns.GetStatus;
            _chatSessions.RunSessionsProvider = _chatRuns.Sessions;
            _chatSessions.RunOwnershipProvider = _chatRuns.IsExternallyRunning;
            _chatSessions.RunRecoveryLeaseProvider = session => _chatRuns.Start(
                session.Id,
                "recover_" + Guid.NewGuid().ToString("N"),
                session);
            _chatSessions.MaintenanceLeaseProvider = _chatRuns.ReserveMaintenance;
            _chatSessions.ReconcileInterruptedRuns(_runtimeId);
            _chatHistoryEditService = new ChatHistoryEditService(
                RemovePendingAgentToolsForSession,
                CancelPendingActivities,
                _chatStore.LoadHtmlArtifactBody);
            _htmlNetwork = new HtmlNetworkService(() => _settingsService.Load(), value => _settingsService.Save(value));
            _llmClient = new LlmClient(
                () => _settingsService.LoadApiKey(),
                attachment => AttachmentImageService.ReadForModel(_attachmentStore, attachment),
                (attachment, maxChars) => _attachmentStore.ReadExtractedText(attachment, maxChars),
                (settings, attachment, maxImages, cancellationToken) =>
                    ModelAttachmentService.ReadForModel(_attachmentStore, settings, attachment, maxImages, cancellationToken),
                RuntimeLog.Debug,
                ReportModelRequestDiagnostics);
            LlmCompletionDelegate rawCompletion;
            if (completeAsync == null)
            {
                rawCompletion = (settings, messages, requestOptions, streamProgress, cancellationToken) =>
                    _llmClient.CompleteAsync(settings, messages, requestOptions, streamProgress, cancellationToken);
            }
            else
            {
                rawCompletion = (settings, messages, requestOptions, streamProgress, cancellationToken) =>
                    completeAsync(settings, messages, cancellationToken);
            }
            LlmCompletionDelegate completion = async (settings, messages, requestOptions, streamProgress, cancellationToken) =>
            {
                var result = await rawCompletion(
                    settings,
                    messages,
                    requestOptions,
                    streamProgress,
                    cancellationToken).ConfigureAwait(false);
                if (result != null && result.TokenEstimateCalibrationEligible &&
                    result.BaseEstimatedPromptTokens.GetValueOrDefault() > 0 &&
                    result.EstimatedPromptTokens.GetValueOrDefault() > 0 &&
                    result.PromptTokens.GetValueOrDefault() > 0)
                {
                    TokenEstimateCalibration.Observe(
                        settings,
                        settings == null ? null : settings.Model,
                        result.BaseEstimatedPromptTokens.Value,
                        result.EstimatedPromptTokens.Value,
                        result.PromptTokens.Value);
                }
                return result;
            };
            _llmCompletion = completion;
            _contextCompactionService = new ContextCompactionService(
                completion,
                (attachment, maxChars) => _attachmentStore.ReadExtractedText(attachment, maxChars));
            _agentRunService = new AgentRunService(_adapter, _toolExecutor, completion, _contextCompactionService);
            _plainChatService = new PlainChatService(completion, _contextCompactionService);
            _contextService = new ContextService(_adapter);
            _syncRoot = new object();
            _pendingAgentTools = new Dictionary<string, PendingAgentTool>(StringComparer.OrdinalIgnoreCase);
        }

        public string HostName { get { return _adapter.HostName; } }

        public InitResponse Initialize()
        {
            var session = LoadSession(null);
            var activeId = session.Id;
            var context = LoadContext(session);
            var settings = _settingsService.Load();
            var chatSettings = ResolveChatSettings(session, settings);
            return new InitResponse
            {
                AppVersion = ApplicationVersionService.Current,
                Host = _adapter.HostName,
                DocumentKey = _adapter.DocumentKey,
                Title = _adapter.DocumentTitle,
                OfficeContext = CaptureOfficeContext(),
                ActiveChatId = activeId,
                ActiveChatModel = session == null ? string.Empty : session.Model,
                ActiveChatMode = ChatModes.Normalize(session == null ? null : session.Mode),
                ActiveChatHtmlMode = session != null && session.HtmlModeEnabled,
                ActiveChatReasoning = session != null && session.ReasoningEnabled,
                Chats = _chatSessions.GetChatSummaries(activeId),
                Documents = ListOpenDocuments(),
                Settings = settings,
                HasApiKey = !string.IsNullOrWhiteSpace(_settingsService.LoadApiKey()),
                Tools = _toolCatalog.GetVisibleTools(),
                ToolsPath = _paths.ToolsDirectory,
                Skills = _skillCatalog.GetVisibleSkills(),
                SkillsPath = _paths.SkillsDirectory,
                Context = ChatCloneService.CloneContext(context),
                Messages = ChatCloneService.CloneMessages(session.Messages),
                Artifacts = ChatArtifactDto.From(session.Artifacts),
                ActiveContextCheckpointId = session.ActiveContextCheckpointId,
                ActiveHtmlArtifactId = session.ActiveHtmlArtifactId,
                ActivePlanArtifactId = session.ActivePlanArtifactId,
                ContextUsage = ContextUsageEstimator.FromSession(session, chatSettings),
                HtmlWorkspace = HtmlWorkspaceDto.From(session == null ? null : HtmlArtifactToolExecutor.NormalizeWorkspace(session.HtmlWorkspace)),
                QuickAction = DequeueQuickAction()
            };
        }

        private OfficeContext CaptureOfficeContext()
        {
            var provider = _adapter as IOfficeContextProvider;
            if (provider == null)
            {
                return null;
            }

            try
            {
                return provider.GetOfficeContext();
            }
            catch
            {
                return null;
            }
        }

        internal IReadOnlyList<OpenOfficeDocumentDto> ListOpenDocuments()
        {
            var catalog = _adapter as IOfficeDocumentCatalog;
            if (catalog == null)
            {
                return new OpenOfficeDocumentDto[0];
            }

            try
            {
                return catalog.ListOpenDocuments() ?? new OpenOfficeDocumentDto[0];
            }
            catch
            {
                return new OpenOfficeDocumentDto[0];
            }
        }

        public ChatStateResponse ActivateDocument(string documentKey)
        {
            var catalog = _adapter as IOfficeDocumentCatalog;
            if (catalog == null || !catalog.ActivateDocument(documentKey))
            {
                throw new InvalidOperationException("Не удалось активировать документ.");
            }

            return ChatState(LoadSession(null));
        }

        public async Task<SendChatResponse> SendChatAsync(
            string text,
            string chatId = null,
            IReadOnlyList<string> attachmentIds = null,
            Action<string, string, ChatActivity> progress = null,
            Action<ChatStateResponse> chatStateChanged = null,
            CancellationToken cancellationToken = default(CancellationToken),
            string runId = null)
        {
            if (string.IsNullOrWhiteSpace(text) && (attachmentIds == null || attachmentIds.Count == 0))
            {
                return EmptySendResponse(LoadAddressedSession(chatId), _settingsService.Load());
            }

            var settings = _settingsService.Load();
            var session = LoadAddressedSession(chatId);
            runId = string.IsNullOrWhiteSpace(runId) ? Guid.NewGuid().ToString("N") : runId;
            var attachments = _attachmentStore.LoadDrafts(attachmentIds);
            var invalidAttachment = attachments.FirstOrDefault(a => a != null && a.Status == "error");
            if (invalidAttachment != null)
            {
                throw new InvalidOperationException(invalidAttachment.FileName + ": " + invalidAttachment.Error);
            }

            return await ExecuteChatTurnAsync(
                session,
                settings,
                new ChatTurnInput
                {
                    Text = text ?? string.Empty,
                    Attachments = attachments,
                    AppendUserMessage = true,
                    CommitUserAttachments = true
                },
                null,
                progress,
                chatStateChanged,
                cancellationToken,
                runId).ConfigureAwait(false);
        }

        public bool CancelChatRun(string chatId, string runId)
        {
            return _chatRuns.Cancel(chatId, runId);
        }

        private static void AnnotateRunMessages(ChatSession session, int firstIndex, string runId)
        {
            if (session == null || session.Messages == null) return;
            var sequence = 1;
            for (var index = Math.Max(0, firstIndex); index < session.Messages.Count; index++)
            {
                var message = session.Messages[index];
                if (message == null) continue;
                message.RunId = runId;
                message.Sequence = sequence++;
                if (message.Activity != null)
                {
                    AnnotateActivity(message.Activity, runId, message.Sequence);
                }
            }
        }

        private static void AnnotateActivity(ChatActivity activity, string runId, int? sequence)
        {
            if (activity == null) return;
            activity.RunId = runId;
            activity.Sequence = sequence;
            foreach (var child in activity.Children ?? new List<ChatActivity>())
            {
                AnnotateActivity(child, runId, sequence);
            }
        }

        private void PersistRunCheckpoint(ChatSession session, string runId, string phase)
        {
            if (!string.Equals(phase, "tool_running", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(phase, "tool_result", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            SaveSessionChanges(session);
            _chatRuns.UpdateSessionSnapshot(session.Id, runId, session);
        }

        private static void CloseRunningActivities(ChatSession session, int firstMessageIndex, bool cancelled)
        {
            if (session == null || session.Messages == null) return;
            for (var index = Math.Max(0, firstMessageIndex); index < session.Messages.Count; index++)
            {
                CloseRunningActivity(session.Messages[index] == null ? null : session.Messages[index].Activity, cancelled);
            }
        }

        private static void CloseRunningActivity(ChatActivity activity, bool cancelled)
        {
            if (activity == null) return;
            if (string.Equals(activity.Status, "running", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(activity.ExecutionStatus, "executing", StringComparison.OrdinalIgnoreCase))
            {
                activity.Status = cancelled ? "cancelled" : "failed";
                activity.ExecutionStatus = cancelled ? "cancelled" : "runtime_error";
                activity.Retryable = cancelled;
                activity.PendingId = null;
                activity.ConfirmationCatalogSha256 = null;
                activity.ResultMessage = cancelled
                    ? "Execution was cancelled before a result was recorded."
                    : "Execution stopped before a result was recorded.";
            }
            foreach (var child in activity.Children ?? new List<ChatActivity>())
            {
                CloseRunningActivity(child, cancelled);
            }
        }

        public AttachmentResponse ImportAttachment(string fileName, string contentType, string base64)
        {
            var attachment = _attachmentStore.Import(fileName, contentType, base64);
            _attachmentStore.SaveDraftMetadata(attachment);
            return new AttachmentResponse { Attachment = attachment };
        }

        public DeleteResponse DeleteDraftAttachment(string id)
        {
            _attachmentStore.DeleteDraft(id);
            return new DeleteResponse { Deleted = true };
        }

        private void StartChatTitleGeneration(
            ChatSession session,
            string userText,
            string assistantText,
            AppSettings settings,
            string expectedCurrentTitle,
            Action<ChatStateResponse> chatStateChanged)
        {
            if (session == null || !ChatTitleBuilder.CanReplaceAutoTitle(session, expectedCurrentTitle))
            {
                return;
            }

            var host = session.Host;
            var documentKey = session.DocumentKey;
            var documentTitle = session.DocumentTitle;
            var sessionId = session.Id;
            CancellationToken lifetimeToken;
            try
            {
                lifetimeToken = _lifetimeCancellation.Token;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            Task.Run(async delegate
            {
                var title = string.Empty;
                try
                {
                    title = await ChatTitleBuilder.GenerateLlmTitleAsync(settings, userText, assistantText, _llmClient.CompleteAsync, lifetimeToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    if (lifetimeToken.IsCancellationRequested)
                    {
                        return;
                    }
                    title = ChatTitleBuilder.BuildFallbackTitle(userText, assistantText);
                }

                if (lifetimeToken.IsCancellationRequested || string.IsNullOrWhiteSpace(title))
                {
                    return;
                }

                try
                {
                    ChatStateResponse state;
                    using (_chatRuns.ReserveMaintenance())
                    {
                        lock (_syncRoot)
                        {
                            if (!_chatSessions.TryApplyGeneratedTitle(
                                host,
                                documentKey,
                                sessionId,
                                expectedCurrentTitle,
                                title))
                            {
                                return;
                            }
                            state = CreateStoredChatState(host, documentKey, documentTitle);
                        }
                    }

                    if (chatStateChanged != null)
                    {
                        chatStateChanged(state);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch
                {
                    // Title generation is best-effort and must not fault the chat run.
                }
            });
        }

        private static void RecordFailedTurn(ChatSession session, Exception error)
        {
            if (session == null)
            {
                return;
            }
            var cancelled = error is OperationCanceledException;
            session.Messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = cancelled ? "Запрос отменён." : "Запрос завершился технической ошибкой.",
                Activity = new ChatActivity
                {
                    Kind = "diagnostic",
                    Title = cancelled ? "Request cancelled" : "Request failed",
                    Status = cancelled ? "cancelled" : "failed",
                    ExecutionStatus = cancelled ? "cancelled" : "runtime_error",
                    ResultMessage = error == null ? string.Empty : error.Message
                }
            });
        }

        private sealed class ChatTurnInput
        {
            public string Text { get; set; }
            public IReadOnlyList<ChatAttachment> Attachments { get; set; }
            public bool AppendUserMessage { get; set; }
            public bool CommitUserAttachments { get; set; }
            public IReadOnlyList<ChatMessage> MessagesToDeleteAfterSave { get; set; }
        }

        private async Task<SendChatResponse> ExecuteChatTurnAsync(
            ChatSession session,
            AppSettings settings,
            ChatTurnInput input,
            Func<ChatSession, ChatTurnInput> prepareTurn,
            Action<string, string, ChatActivity> progress,
            Action<ChatStateResponse> chatStateChanged,
            CancellationToken cancellationToken,
            string runId)
        {
            session = session ?? LoadAddressedSession(null);
            var sessionId = session.Id;
            runId = string.IsNullOrWhiteSpace(runId) ? Guid.NewGuid().ToString("N") : runId;

            var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ChatRunLease runLease;
            try
            {
                runLease = _chatRuns.Start(sessionId, runId, session, runCancellation);
            }
            catch
            {
                runCancellation.Dispose();
                throw;
            }

            try
            {
                session = ReloadReservedSession(session);
                settings = ResolveChatSettings(session, settings);
                if (prepareTurn == null && HasPendingAgentConfirmation(session))
                {
                    throw new InvalidOperationException("Сначала подтвердите или отмените ожидающее действие агента.");
                }
                input = prepareTurn == null ? input : prepareTurn(session);
                input = input ?? new ChatTurnInput();
                var text = input.Text ?? string.Empty;
                var attachments = input.Attachments ?? new ChatAttachment[0];
                var executionMode = ChatModes.Normalize(session.Mode);
                var documentRuntimeKey = string.Empty;
                if (executionMode == ChatModes.Agent)
                {
                    documentRuntimeKey = CaptureExpectedRuntimeDocumentKey(session);
                }
                HtmlWorkspaceArtifactService.CaptureCurrent(session, "Before chat turn");
                var firstRunMessageIndex = session.Messages == null ? 0 : session.Messages.Count;
                ChatMessage appendedUserMessage = null;
                var commitUserAttachments = input.CommitUserAttachments;
                if (!input.AppendUserMessage && session.Messages != null && session.Messages.Count > 0)
                {
                    firstRunMessageIndex = Math.Max(0, session.Messages.Count - 1);
                }
                if (input.AppendUserMessage)
                {
                    var userMessage = new ChatMessage
                    {
                        Role = "user",
                        Content = text,
                        RunId = runId,
                        Sequence = 1,
                        HtmlWorkspaceCheckpointId = session.ActiveHtmlArtifactId,
                        Attachments = new List<ChatAttachment>(attachments)
                    };
                    session.Messages.Add(userMessage);
                    appendedUserMessage = userMessage;
                }
                var documentContext = LoadContext(session);
                var titleUserSeed = ChatTitleBuilder.ResolveUserSeed(session, text);
                var shouldGenerateLlmTitle = settings.SmartChatTitles && ChatTitleBuilder.ShouldAssign(session);
                var provisionalTitle = ChatTitleBuilder.ShouldAssign(session)
                    ? ChatTitleBuilder.BuildDraftTitle(titleUserSeed)
                    : string.Empty;
                if (!string.IsNullOrWhiteSpace(provisionalTitle))
                {
                    session.Title = provisionalTitle;
                }

                session.LastRun = new ChatRunRecord
                {
                    RunId = runId,
                    RuntimeId = _runtimeId,
                    Status = "running",
                    Phase = "starting",
                    CurrentAction = "Preparing request.",
                    DocumentRuntimeKey = executionMode == ChatModes.Agent ? documentRuntimeKey : null,
                    IterationsUsed = 0,
                    ToolStepsUsed = 0,
                    StartedUtc = DateTime.UtcNow
                };
                var preparedTurnPersisted = false;
                try
                {
                    if (commitUserAttachments && appendedUserMessage != null)
                    {
                        // Keep drafts until the chat points to the copied final files durably.
                        _attachmentStore.Commit(sessionId, appendedUserMessage, false);
                    }
                    _chatStore.Save(session);
                    preparedTurnPersisted = true;
                    _chatSessions.NotifySaved(session);
                    if (commitUserAttachments && appendedUserMessage != null)
                    {
                        _attachmentStore.DeleteDrafts(appendedUserMessage);
                    }
                    foreach (var removedMessage in input.MessagesToDeleteAfterSave ?? new ChatMessage[0])
                    {
                        _attachmentStore.DeleteMessage(removedMessage);
                    }
                    input.MessagesToDeleteAfterSave = null;
                    _chatRuns.UpdateSessionSnapshot(sessionId, runId, session);
                    if (!string.IsNullOrWhiteSpace(provisionalTitle) && chatStateChanged != null)
                    {
                        try
                        {
                            chatStateChanged(CreateStoredChatState(session.Host, session.DocumentKey, session.DocumentTitle));
                        }
                        catch
                        {
                            // A UI notification cannot invalidate an already persisted request.
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (preparedTurnPersisted)
                    {
                        RecordFailedTurn(session, ex);
                        if (session.LastRun != null)
                        {
                            session.LastRun.Status = "failed";
                            session.LastRun.Phase = "failed";
                            session.LastRun.CurrentAction = ex.Message;
                        }
                        AnnotateRunMessages(session, firstRunMessageIndex, runId);
                        try
                        {
                            SaveSessionChanges(session);
                        }
                        catch
                        {
                            // Keep the original preparation failure. Draft files remain available.
                        }
                        _chatRuns.UpdateSessionSnapshot(sessionId, runId, session);
                    }
                    throw;
                }

                Action<string, string, ChatActivity> runProgress = (phase, message, activity) =>
                {
                    _chatRuns.Update(sessionId, runId, phase, message);
                    if (session.LastRun != null && string.Equals(session.LastRun.RunId, runId, StringComparison.OrdinalIgnoreCase))
                    {
                        session.LastRun.Phase = string.IsNullOrWhiteSpace(phase) ? session.LastRun.Phase : phase;
                        session.LastRun.CurrentAction = string.IsNullOrWhiteSpace(message) ? session.LastRun.CurrentAction : message;
                    }
                    if (activity != null)
                    {
                        AnnotateActivity(activity, runId, null);
                    }
                    if (string.Equals(phase, "tool_running", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(phase, "tool_result", StringComparison.OrdinalIgnoreCase))
                    {
                        AnnotateRunMessages(session, firstRunMessageIndex, runId);
                    }
                    PersistRunCheckpoint(session, runId, phase);
                    ReportExternalProgress(progress, phase, message, activity);
                };

                ChatTurnResult completion;
                try
                {
                    try
                    {
                        await _contextCompactionService.EnsureWithinBudgetAsync(
                            session,
                            settings,
                            string.Empty,
                            false,
                            runProgress,
                            runCancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception compactionError)
                    {
                        var activity = new ChatActivity
                        {
                            Kind = "compaction",
                            Title = "Не удалось сжать контекст",
                            Subtitle = "Полная история сохранена",
                            Status = "failed",
                            ExecutionStatus = "compaction_failed",
                            ResultMessage = compactionError.Message
                        };
                        session.Messages.Add(AgentTranscript.CreateAssistantMessage(
                            "Не удалось обновить сжатый контекст; продолжаю с сохранённой историей.", null, activity));
                        runProgress("compaction_failed", activity.ResultMessage, activity);
                    }
                    if (executionMode == ChatModes.Chat)
                    {
                        completion = await _plainChatService.ExecuteAsync(
                            text,
                            session,
                            documentContext,
                            settings,
                            attachments,
                            runProgress,
                            runCancellation.Token,
                            false).ConfigureAwait(false);
                    }
                    else
                    {
                        var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
                        var skills = _skillCatalog.GetVisibleSkills().Where(skill => skill.Enabled).ToList();
                        try
                        {
                            completion = await _agentRunService.ExecuteAsync(
                                text,
                                session,
                                documentContext,
                                settings,
                                tools,
                                attachments,
                                runProgress,
                                RegisterPendingAgentTool,
                                skills,
                                runCancellation.Token,
                                false).ConfigureAwait(false);
                        }
                        finally
                        {
                            // Auto-confirmed VBA mutations bypass the explicit VBA bridge methods.
                            _toolCatalog.InvalidateDocumentVbaTools();
                        }
                    }
                }
                catch (Exception ex)
                {
                    PersistTokenEstimateCalibration(settings);
                    CloseRunningActivities(session, firstRunMessageIndex, ex is OperationCanceledException);
                    RecordFailedTurn(session, ex);
                    if (session.LastRun != null)
                    {
                        session.LastRun.Status = ex is OperationCanceledException ? "cancelled" : "failed";
                        session.LastRun.Phase = session.LastRun.Status;
                        session.LastRun.CurrentAction = ex.Message;
                    }
                    AnnotateRunMessages(session, firstRunMessageIndex, runId);
                    HtmlWorkspaceArtifactService.StampUncheckpointed(session, firstRunMessageIndex, session.ActiveHtmlArtifactId);
                    ChatArtifactService.LinkMessageArtifacts(session, firstRunMessageIndex);
                    SaveSessionChanges(session);
                    _chatRuns.UpdateSessionSnapshot(sessionId, runId, session);
                    throw;
                }

                PersistTokenEstimateCalibration(settings);
                if (settings.SmartChatTitles == false)
                {
                    ChatTitleBuilder.ApplyFallback(session, text, completion.AssistantText);
                }
                ReportProgress(runProgress, "saving", "Сохраняю историю чата...");
                HtmlWorkspaceArtifactService.StampUncheckpointed(session, firstRunMessageIndex, session.ActiveHtmlArtifactId);
                AnnotateRunMessages(session, firstRunMessageIndex, runId);
                ChatArtifactService.LinkMessageArtifacts(session, firstRunMessageIndex);
                if (completion == null || !completion.WaitingForConfirmation)
                {
                    session.LastRun = null;
                }
                SaveSessionChanges(session);
                _chatRuns.UpdateSessionSnapshot(sessionId, runId, session);
                runLease.Dispose();
                var response = CreateSendChatResponse(session, settings, completion);
                if (shouldGenerateLlmTitle)
                {
                    StartChatTitleGeneration(
                        session,
                        titleUserSeed,
                        ChatTitleBuilder.ResolveAssistantSeed(session, completion.AssistantText),
                        settings,
                        provisionalTitle,
                        chatStateChanged);
                }

                return response;
            }
            finally
            {
                runLease.Dispose();
            }
        }

        private SendChatResponse CreateSendChatResponse(ChatSession session, AppSettings settings, ChatTurnResult completion)
        {
            settings = ResolveChatSettings(session, settings);
            var activeId = session.Id;
            return new SendChatResponse
            {
                Message = completion == null ? string.Empty : completion.AssistantText,
                ToolResults = completion == null
                    ? (IReadOnlyList<object>)new object[0]
                    : completion.ToolResults ?? new object[0],
                ActiveChatId = activeId,
                ActiveChatModel = session == null ? string.Empty : session.Model,
                ActiveChatMode = ChatModes.Normalize(session == null ? null : session.Mode),
                ActiveChatHtmlMode = session != null && session.HtmlModeEnabled,
                ActiveChatReasoning = session != null && session.ReasoningEnabled,
                Chats = _chatSessions.GetChatSummaries(activeId),
                Documents = ListOpenDocuments(),
                Context = session == null ? CreateEmptyContext() : ChatCloneService.CloneContext(LoadContext(session)),
                Messages = session == null ? new List<ChatMessage>() : ChatCloneService.CloneMessages(session.Messages),
                Artifacts = ChatArtifactDto.From(session == null ? null : session.Artifacts),
                ActiveContextCheckpointId = session == null ? string.Empty : session.ActiveContextCheckpointId,
                ActiveHtmlArtifactId = session == null ? string.Empty : session.ActiveHtmlArtifactId,
                ActivePlanArtifactId = session == null ? string.Empty : session.ActivePlanArtifactId,
                ContextUsage = completion == null
                    ? ContextUsageEstimator.FromSession(session, settings)
                    : completion.ContextUsage ?? ContextUsageEstimator.FromSession(session, settings),
                HtmlWorkspace = HtmlWorkspaceDto.From(session == null ? null : HtmlArtifactToolExecutor.NormalizeWorkspace(session.HtmlWorkspace))
            };
        }

        private SendChatResponse EmptySendResponse(ChatSession session, AppSettings settings)
        {
            settings = ResolveChatSettings(session, settings);
            return CreateSendChatResponse(session, settings, new ChatTurnResult
            {
                AssistantText = string.Empty,
                ToolResults = new object[0],
                ContextUsage = ContextUsageEstimator.FromSession(session, settings)
            });
        }

        private ChatStateResponse CreateStoredChatState(string host, string documentKey, string documentTitle)
        {
            var activeId = _chatStore.LoadActiveSessionId(host, documentKey);
            var active = string.IsNullOrWhiteSpace(activeId) ? null : _chatStore.Load(host, documentKey, activeId);
            var chats = _chatSessions.GetChatSummaries(activeId)
                .ToList();

            return new ChatStateResponse
            {
                ActiveChatId = activeId,
                ActiveChatModel = active == null ? string.Empty : active.Model,
                ActiveChatMode = ChatModes.Normalize(active == null ? null : active.Mode),
                ActiveChatHtmlMode = active != null && active.HtmlModeEnabled,
                ActiveChatReasoning = active != null && active.ReasoningEnabled,
                Chats = chats,
                Documents = ListOpenDocuments(),
                Artifacts = ChatArtifactDto.From(active == null ? null : active.Artifacts),
                ActiveContextCheckpointId = active == null ? string.Empty : active.ActiveContextCheckpointId,
                ActiveHtmlArtifactId = active == null ? string.Empty : active.ActiveHtmlArtifactId,
                ActivePlanArtifactId = active == null ? string.Empty : active.ActivePlanArtifactId
            };
        }

        private static void ReportProgress(Action<string, string, ChatActivity> progress, string phase, string message)
        {
            if (progress != null)
            {
                progress(phase, message, null);
            }
        }

        private static void ReportProgress(Action<string, string, ChatActivity> progress, string phase, string message, ChatActivity activity)
        {
            if (progress != null)
            {
                progress(phase, message, activity);
            }
        }

        private static void ReportExternalProgress(
            Action<string, string, ChatActivity> progress,
            string phase,
            string message,
            ChatActivity activity)
        {
            if (progress == null) return;
            try
            {
                progress(phase, message, activity);
            }
            catch
            {
                // WebView notifications cannot abort already persisted work.
            }
        }

    }
}
