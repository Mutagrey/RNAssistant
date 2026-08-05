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
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        private static readonly string RuntimeId = Guid.NewGuid().ToString("N");
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
        private readonly ChatCompletionService _chatCompletionService;
        private readonly PlainChatService _plainChatService;
        private readonly ChatExecutionModeSelector _chatModeSelector;
        private readonly OfflineChatService _offlineChatService;
        private readonly ContextService _contextService;
        private readonly LlmClient _llmClient;
        private readonly object _syncRoot;
        private readonly Dictionary<string, PendingAgentTool> _pendingAgentTools;
        private readonly ChatRunRegistry _chatRuns;
        private readonly HtmlNetworkService _htmlNetwork;
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
                settings => _settingsService.Save(settings));
            _toolCatalog = new ToolCatalogService(_adapter, _toolExecutor, _toolStore);
            _skillCatalog = new SkillCatalogService(_adapter, _skillStore);
            _chatSessions = new ChatSessionService(_adapter, _chatStore);
            _chatRuns = new ChatRunRegistry();
            _chatSessions.RunStateProvider = _chatRuns.Get;
            _chatSessions.RunSessionsProvider = _chatRuns.Sessions;
            _chatSessions.ReconcileInterruptedRuns(RuntimeId);
            _chatHistoryEditService = new ChatHistoryEditService(_attachmentStore, RemovePendingAgentToolsForSession, CancelPendingActivities);
            _htmlNetwork = new HtmlNetworkService(() => _settingsService.Load(), value => _settingsService.Save(value));
            _llmClient = new LlmClient(
                () => _settingsService.LoadApiKey(),
                attachment => AttachmentImageService.ReadForModel(_attachmentStore, attachment),
                attachment => _attachmentStore.ReadExtractedText(attachment),
                (settings, attachment) => ModelAttachmentService.ReadForModel(_attachmentStore, settings, attachment));
            if (completeAsync == null)
            {
                LlmCompletionDelegate completion =
                    (settings, messages, requestOptions, streamProgress, cancellationToken) =>
                        _llmClient.CompleteAsync(settings, messages, requestOptions, streamProgress, cancellationToken);
                _chatCompletionService = new ChatCompletionService(_adapter, _toolExecutor, completion);
                _offlineChatService = new OfflineChatService(_toolExecutor, completion);
                _plainChatService = new PlainChatService(completion);
            }
            else
            {
                LlmCompletionDelegate completion =
                    (settings, messages, requestOptions, streamProgress, cancellationToken) =>
                        completeAsync(settings, messages, cancellationToken);
                _chatCompletionService = new ChatCompletionService(_adapter, _toolExecutor, completion);
                _offlineChatService = new OfflineChatService(_toolExecutor, completion);
                _plainChatService = new PlainChatService(completion);
            }
            _contextService = new ContextService(_adapter);
            _chatModeSelector = new ChatExecutionModeSelector();
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
            return new InitResponse
            {
                Host = _adapter.HostName,
                DocumentKey = _adapter.DocumentKey,
                Title = _adapter.DocumentTitle,
                OfficeContext = CaptureOfficeContext(),
                ActiveChatId = activeId,
                ActiveChatModel = session == null ? string.Empty : session.Model,
                ActiveChatMode = ChatModes.Normalize(session == null ? null : session.Mode),
                ActiveChatHtmlMode = session != null && session.HtmlModeEnabled,
                Chats = _chatSessions.GetChatSummaries(activeId),
                Documents = ListOpenDocuments(),
                Settings = settings,
                HasApiKey = !string.IsNullOrWhiteSpace(_settingsService.LoadApiKey()),
                Tools = _toolCatalog.GetVisibleTools(),
                ToolsPath = _paths.ToolsDirectory,
                Skills = _skillCatalog.GetVisibleSkills(),
                SkillsPath = _paths.SkillsDirectory,
                Context = context,
                Messages = session.Messages,
                ContextUsage = ContextUsageEstimator.FromSession(session, settings),
                HtmlWorkspace = session == null ? new HtmlWorkspace() : HtmlArtifactToolExecutor.NormalizeWorkspace(session.HtmlWorkspace),
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

            return ListChats();
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
                    CommitUserAttachments = true,
                    ConsumeContext = true
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

        public AttachmentResponse ImportAttachment(string fileName, string contentType, string base64)
        {
            var attachment = _attachmentStore.Import(fileName, contentType, base64);
            _attachmentStore.SaveDraftMetadata(attachment);
            return new AttachmentResponse { Attachment = attachment };
        }

        public object DeleteDraftAttachment(string id)
        {
            _attachmentStore.DeleteDraft(id);
            return new { deleted = true };
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
            Task.Run(async delegate
            {
                var title = string.Empty;
                try
                {
                    title = await ChatTitleBuilder.GenerateLlmTitleAsync(settings, userText, assistantText, _llmClient.CompleteAsync, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    title = ChatTitleBuilder.BuildFallbackTitle(userText, assistantText);
                }

                if (string.IsNullOrWhiteSpace(title))
                {
                    return;
                }

                ChatStateResponse state;
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

                if (chatStateChanged != null)
                {
                    chatStateChanged(state);
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
            public bool ConsumeContext { get; set; }
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
            settings = settings ?? _settingsService.Load();
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
                input = prepareTurn == null ? input : prepareTurn(session);
                input = input ?? new ChatTurnInput();
                var text = input.Text ?? string.Empty;
                var attachments = input.Attachments ?? new ChatAttachment[0];
                var documentContext = LoadContext(session);
                var skills = _skillCatalog.SelectRelevantSkills(text, documentContext, 5);
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
                    RuntimeId = RuntimeId,
                    Status = "running",
                    Phase = "starting",
                    CurrentAction = "Preparing request.",
                    StartedUtc = DateTime.UtcNow
                };
                _chatStore.Save(session);
                _chatSessions.NotifySaved(session);
                if (!string.IsNullOrWhiteSpace(provisionalTitle) && chatStateChanged != null)
                {
                    chatStateChanged(CreateStoredChatState(session.Host, session.DocumentKey, session.DocumentTitle));
                }

                var firstRunMessageIndex = session.Messages == null ? 0 : session.Messages.Count;
                if (!input.AppendUserMessage && session.Messages != null && session.Messages.Count > 0)
                {
                    firstRunMessageIndex = Math.Max(0, session.Messages.Count - 1);
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
                    if (progress != null)
                    {
                        progress(phase, message, activity);
                    }
                };

                ChatCompletionResult completion;
                try
                {
                    var executionMode = _chatModeSelector.Select(text, session);
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
                            input.AppendUserMessage).ConfigureAwait(false);
                    }
                    else if (_chatSessions.IsCurrentDocument(session))
                    {
                        var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
                        completion = await _chatCompletionService.ExecuteAsync(
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
                            input.AppendUserMessage).ConfigureAwait(false);
                    }
                    else
                    {
                        var offlineTools = _toolCatalog.GetVisibleTools()
                            .Where(tool => tool.Enabled &&
                                string.Equals(tool.Host, "Common", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(tool.Executor, "pipeline", StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        completion = await _offlineChatService.ExecuteAsync(
                            text,
                            session,
                            documentContext,
                            settings,
                            offlineTools,
                            attachments,
                            runProgress,
                            RegisterPendingAgentTool,
                            skills,
                            runCancellation.Token,
                            input.AppendUserMessage).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    AgentPlanStateService.MarkCurrentForRun(
                        session,
                        runId,
                        ex is OperationCanceledException ? "cancelled" : "failed");
                    RecordFailedTurn(session, ex);
                    if (session.LastRun != null)
                    {
                        session.LastRun.Status = ex is OperationCanceledException ? "cancelled" : "failed";
                        session.LastRun.Phase = session.LastRun.Status;
                        session.LastRun.CurrentAction = ex.Message;
                    }
                    AnnotateRunMessages(session, firstRunMessageIndex, runId);
                    if (input.CommitUserAttachments)
                    {
                        _attachmentStore.Commit(sessionId, LatestUserMessage(session));
                    }
                    SaveSessionChanges(session);
                    throw;
                }

                if (settings.SmartChatTitles == false)
                {
                    ChatTitleBuilder.ApplyFallback(session, text, completion.AssistantText);
                }

                if (input.ConsumeContext)
                {
                    session.Context = CreateEmptyContext();
                    NormalizeContext(session.Context, session);
                    if (completion != null)
                    {
                        completion.ContextUsage = ContextUsageEstimator.FromSession(session, settings);
                    }
                }

                ReportProgress(runProgress, "saving", "Saving chat history...");
                if (input.CommitUserAttachments)
                {
                    _attachmentStore.Commit(sessionId, LatestUserMessage(session));
                }
                AnnotateRunMessages(session, firstRunMessageIndex, runId);
                session.LastRun = null;
                SaveSessionChanges(session);
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

        private SendChatResponse CreateSendChatResponse(ChatSession session, AppSettings settings, ChatCompletionResult completion)
        {
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
                Chats = _chatSessions.GetChatSummaries(activeId),
                Documents = ListOpenDocuments(),
                Context = session == null ? CreateEmptyContext() : LoadContext(session),
                Messages = session == null ? new List<ChatMessage>() : session.Messages,
                ContextUsage = completion == null
                    ? ContextUsageEstimator.FromSession(session, settings)
                    : completion.ContextUsage ?? ContextUsageEstimator.FromSession(session, settings),
                HtmlWorkspace = session == null ? new HtmlWorkspace() : HtmlArtifactToolExecutor.NormalizeWorkspace(session.HtmlWorkspace)
            };
        }

        private SendChatResponse EmptySendResponse(ChatSession session, AppSettings settings)
        {
            return CreateSendChatResponse(session, settings, new ChatCompletionResult
            {
                AssistantText = string.Empty,
                ToolResults = new object[0],
                ContextUsage = ContextUsageEstimator.FromSession(session, settings)
            });
        }

        private static ChatMessage LatestUserMessage(ChatSession session)
        {
            return session == null || session.Messages == null
                ? null
                : session.Messages.LastOrDefault(message =>
                    message != null &&
                    string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase));
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
                Chats = chats,
                Documents = ListOpenDocuments()
            };
        }

        public SettingsResponse GetSettings()
        {
            return new SettingsResponse
            {
                Settings = _settingsService.Load(),
                HasApiKey = !string.IsNullOrWhiteSpace(_settingsService.LoadApiKey())
            };
        }

        public async Task<ModelCatalogResponse> GetModelCatalogAsync(AppSettings settings, string apiKey)
        {
            settings = settings ?? _settingsService.Load();
            var json = await _llmClient.GetModelsConfigJsonAsync(
                settings,
                string.IsNullOrWhiteSpace(apiKey) ? null : apiKey).ConfigureAwait(false);
            var catalog = JToken.Parse(json);
            var storedSettings = _settingsService.Load();
            if (ModelCapabilityService.Merge(storedSettings, catalog))
            {
                _settingsService.Save(storedSettings);
            }

            return new ModelCatalogResponse
            {
                ConfigUrl = LlmClient.BuildModelsConfigUrl(settings),
                Catalog = catalog
            };
        }

        public SettingsResponse SaveSettings(AppSettings settings, string apiKey)
        {
            _settingsService.Save(settings);
            if (apiKey != null)
            {
                _settingsService.SaveApiKey(apiKey);
            }

            return GetSettings();
        }

        public InitResponse ClearRuntimeData()
        {
            if (_chatRuns.HasRuns())
            {
                throw new InvalidOperationException("Сначала остановите выполняющиеся запросы.");
            }
            _paths.ClearRuntimeData();
            _chatSessions.Reset();
            _chatRuns.Clear();
            lock (_syncRoot)
            {
                _pendingAgentTools.Clear();
            }
            return Initialize();
        }

        public IReadOnlyList<ToolDefinition> GetTools()
        {
            return _toolCatalog.GetVisibleTools();
        }

        public IReadOnlyList<ToolDefinition> SaveTools(IEnumerable<ToolDefinition> tools)
        {
            var customTools = (tools ?? new ToolDefinition[0]).Where(s =>
                s != null && !s.BuiltIn && !string.Equals(s.Scope, "document", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var tool in customTools)
            {
                var validation = _toolExecutor.ValidateToolDefinition(tool);
                if (!validation.Success)
                {
                    throw new InvalidOperationException(validation.Message);
                }
            }
            _toolStore.Save(customTools, _adapter.HostName);
            return GetTools();
        }

        public VbaToolPackageResponse InstallVbaTool(string id, bool dryRun)
        {
            var tool = _toolStore.Load().FirstOrDefault(item => item != null &&
                string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Executor, "vba", StringComparison.OrdinalIgnoreCase));
            if (tool == null) throw new InvalidOperationException("Global VBA tool not found: " + id);
            var result = _toolExecutor.InstallVbaTool(tool, dryRun);
            return new VbaToolPackageResponse { Result = result, Tools = GetTools() };
        }

        public VbaToolPackageResponse UninstallVbaTool(string id)
        {
            var tool = _toolStore.Load().FirstOrDefault(item => item != null &&
                string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Executor, "vba", StringComparison.OrdinalIgnoreCase));
            if (tool == null) throw new InvalidOperationException("Global VBA tool not found: " + id);
            var result = _toolExecutor.RemoveVbaTool(tool);
            return new VbaToolPackageResponse { Result = result, Tools = GetTools() };
        }

        public IReadOnlyList<SkillDefinition> GetSkills()
        {
            return _skillCatalog.GetVisibleSkills();
        }

        public IReadOnlyList<SkillDefinition> SaveSkills(IEnumerable<SkillDefinition> skills)
        {
            var custom = (skills ?? new SkillDefinition[0]).Where(s => s != null && !s.BuiltIn).ToList();
            var builtInIds = new HashSet<string>(
                _skillCatalog.GetVisibleSkills().Where(s => s.BuiltIn).Select(s => s.Id),
                StringComparer.OrdinalIgnoreCase);
            var collision = custom.FirstOrDefault(s => builtInIds.Contains(s.Id ?? string.Empty));
            if (collision != null) throw new InvalidOperationException("Built-in skill id is reserved: " + collision.Id);
            _skillStore.Save(custom, _adapter.HostName);
            return GetSkills();
        }

        public ToolResult RunTool(string toolId, IDictionary<string, object> arguments, bool dryRun, Action<string, string> progress = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            var settings = _settingsService.Load();
            var session = LoadSession(null);
            var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
            var command = new ToolCommand { ToolId = toolId };
            foreach (var pair in arguments ?? new Dictionary<string, object>())
            {
                command.Arguments[pair.Key] = pair.Value;
            }

            ReportProgress(progress, dryRun ? "checking" : "executing", (dryRun ? "Проверяю tool: " : "Исполняю tool: ") + toolId);
            var result = _toolExecutor.Execute(command, tools, settings, dryRun, true, session, cancellationToken);
            if (!dryRun && IsHtmlWorkspaceTool(toolId))
            {
                SaveSessionChanges(session);
            }

            return result;
        }

        public VbaProjectResponse GetVbaProject(int maxChars)
        {
            var settings = _settingsService.Load();
            var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
            var command = new ToolCommand { ToolId = _toolExecutor.VbaToolId("vba_read_project") };
            command.Arguments["maxChars"] = maxChars <= 0 ? settings.VbaContextCharLimit : maxChars;
            var result = _toolExecutor.Execute(command, tools, settings, false, true);
            return new VbaProjectResponse
            {
                Result = result,
                Backups = _vbaBackupStore.List(_adapter.HostName, _adapter.DocumentKey)
            };
        }

        public ToolResult SaveVbaModule(string moduleName, string code)
        {
            var settings = _settingsService.Load();
            var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
            var command = new ToolCommand { ToolId = _toolExecutor.VbaToolId("vba_replace_module") };
            command.Arguments["moduleName"] = moduleName;
            command.Arguments["code"] = code;
            command.Arguments["createIfMissing"] = "true";
            return _toolExecutor.Execute(command, tools, settings, false, true);
        }

        public ToolResult RestoreVbaBackup(string backupId, string moduleName)
        {
            var settings = _settingsService.Load();
            var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
            return _toolExecutor.Execute(new ToolCommand
            {
                ToolId = _toolExecutor.VbaToolId("vba_restore_backup"),
                Arguments =
                {
                    ["backupId"] = backupId ?? string.Empty,
                    ["moduleName"] = moduleName ?? string.Empty
                }
            }, tools, settings, false, true);
        }

        public void QueueQuickAction(string action)
        {
            lock (_syncRoot)
            {
                _queuedQuickAction = action;
            }
        }

        public Task<QuickActionResponse> RunQuickActionAsync(string action)
        {
            string prompt;
            switch ((action ?? string.Empty).ToLowerInvariant())
            {
                case "summarize":
                    prompt = "Сделай краткое summary текущего документа. Если нужны данные документа, используй доступные tools.";
                    break;
                case "explain-selection":
                    prompt = "Объясни выделенный фрагмент. Если надо, прочитай выделение через tool.";
                    break;
                case "draft-rewrite":
                    prompt = "Помоги написать или улучшить текст для текущего документа/письма. Сначала уточни цель, если данных недостаточно.";
                    break;
                case "run-skill":
                    prompt = "Покажи доступные tools для этого Office-приложения и предложи, что можно выполнить.";
                    break;
                case "settings":
                    prompt = "/open-settings";
                    break;
                case "context":
                    prompt = "/open-context";
                    break;
                case "ask-context":
                    prompt = "Используй добавленный контекст выше как основной объект задачи. Сначала кратко скажи, что именно видишь в контексте, затем ответь на мой вопрос или предложи следующий шаг.";
                    break;
                default:
                    prompt = action ?? string.Empty;
                    break;
            }

            return Task.FromResult(new QuickActionResponse { Prompt = prompt });
        }

        private string DequeueQuickAction()
        {
            lock (_syncRoot)
            {
                var action = _queuedQuickAction;
                _queuedQuickAction = null;
                return action;
            }
        }

        private static void ReportProgress(Action<string, string> progress, string phase, string message)
        {
            if (progress != null)
            {
                progress(phase, message);
            }
        }

        private static void ReportProgress(Action<string, string, ChatActivity> progress, string phase, string message)
        {
            if (progress != null)
            {
                progress(phase, message, null);
            }
        }

        private static bool IsHtmlWorkspaceTool(string toolId)
        {
            return string.Equals(toolId, HtmlArtifactToolExecutor.UpsertFileToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, HtmlArtifactToolExecutor.UpsertDataToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, HtmlArtifactToolExecutor.DeleteFileToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, HtmlArtifactToolExecutor.DeleteDataToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, HtmlArtifactToolExecutor.SetActiveToolId, StringComparison.OrdinalIgnoreCase);
        }
    }
}
