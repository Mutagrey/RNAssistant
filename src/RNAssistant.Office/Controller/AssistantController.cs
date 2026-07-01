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
        private readonly ChatCompletionService _chatCompletionService;
        private readonly OfflineChatService _offlineChatService;
        private readonly ContextService _contextService;
        private readonly LlmClient _llmClient;
        private readonly object _syncRoot;
        private readonly Dictionary<string, PendingAgentTool> _pendingAgentTools;
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
            foreach (var incomplete in _chatStore.List().Where(session => !HasCompletedExchange(session)).ToList())
            {
                _attachmentStore.DeleteSession(ChatStore.GetSessionId(incomplete));
                _chatStore.Delete(incomplete.Host, incomplete.DocumentKey, ChatStore.GetSessionId(incomplete));
            }
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
            _llmClient = new LlmClient(
                () => _settingsService.LoadApiKey(),
                attachment => AttachmentImageService.ReadForModel(_attachmentStore, attachment),
                attachment => _attachmentStore.ReadExtractedText(attachment),
                (settings, attachment) => ModelAttachmentService.ReadForModel(_attachmentStore, settings, attachment));
            if (completeAsync == null)
            {
                ChatCompletionService.CompletionDelegate streamingCompletion =
                    (settings, messages, streamProgress, cancellationToken) =>
                        _llmClient.CompleteAsync(settings, messages, streamProgress, cancellationToken);
                _chatCompletionService = new ChatCompletionService(_adapter, _toolExecutor, streamingCompletion);
                _offlineChatService = new OfflineChatService(_toolExecutor, streamingCompletion);
            }
            else
            {
                _chatCompletionService = new ChatCompletionService(_adapter, _toolExecutor, completeAsync);
                _offlineChatService = new OfflineChatService(_toolExecutor, completeAsync);
            }
            _contextService = new ContextService(_adapter);
            _syncRoot = new object();
            _pendingAgentTools = new Dictionary<string, PendingAgentTool>(StringComparer.OrdinalIgnoreCase);
        }

        public string HostName { get { return _adapter.HostName; } }

        public InitResponse Initialize()
        {
            var session = LoadSession(null);
            var activeId = ChatStore.GetSessionId(session);
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
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(text) && (attachmentIds == null || attachmentIds.Count == 0))
            {
                var emptySession = LoadSession(chatId, true);
                var emptyId = ChatStore.GetSessionId(emptySession);
                return new SendChatResponse { Message = string.Empty, ToolResults = new object[0], ActiveChatId = emptyId, ActiveChatModel = emptySession.Model, ActiveChatHtmlMode = emptySession.HtmlModeEnabled, Chats = _chatSessions.GetChatSummaries(emptyId), Context = LoadContext(emptySession), Messages = emptySession.Messages, ContextUsage = ContextUsageEstimator.FromSession(emptySession, _settingsService.Load()), HtmlWorkspace = HtmlArtifactToolExecutor.NormalizeWorkspace(emptySession.HtmlWorkspace) };
            }

            var settings = _settingsService.Load();
            var session = LoadSession(chatId, true);
            var attachments = _attachmentStore.LoadDrafts(attachmentIds);
            var invalidAttachment = attachments.FirstOrDefault(a => a != null && a.Status == "error");
            if (invalidAttachment != null)
            {
                throw new InvalidOperationException(invalidAttachment.FileName + ": " + invalidAttachment.Error);
            }
            var documentContext = LoadContext(session);
            var skills = _skillCatalog.SelectRelevantSkills(text, documentContext, 5);
            var shouldGenerateLlmTitle = settings.SmartChatTitles != false && ChatTitleBuilder.ShouldAssign(session);
            ChatCompletionResult completion;
            if (_chatSessions.IsCurrentDocument(session))
            {
                var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
                completion = await _chatCompletionService.ExecuteAsync(text ?? string.Empty, session, documentContext, settings, tools, attachments, progress, RegisterPendingAgentTool, skills, cancellationToken);
            }
            else
            {
                var offlineTools = _toolCatalog.GetVisibleTools()
                    .Where(s => s.Enabled &&
                        string.Equals(s.Host, "Common", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(s.Executor, "pipeline", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                completion = await _offlineChatService.ExecuteAsync(text ?? string.Empty, session, documentContext, settings, offlineTools, attachments, progress, RegisterPendingAgentTool, skills, cancellationToken);
            }
            if (settings.SmartChatTitles == false)
            {
                ChatTitleBuilder.ApplyFallback(session, text, completion.AssistantText);
            }

            ReportProgress(progress, "saving", "Сохраняю историю...");
            cancellationToken.ThrowIfCancellationRequested();
            var userMessage = session.Messages.LastOrDefault(m => m != null && string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));
            _attachmentStore.Commit(ChatStore.GetSessionId(session), userMessage);
            SaveSessionChanges(session);
            var activeId = ChatStore.GetSessionId(session);
            if (shouldGenerateLlmTitle)
            {
                StartChatTitleGeneration(session, text, completion.AssistantText, settings, chatStateChanged);
            }

            return new SendChatResponse { Message = completion.AssistantText, ToolResults = completion.ToolResults, ActiveChatId = activeId, ActiveChatModel = session.Model, ActiveChatHtmlMode = session.HtmlModeEnabled, Chats = _chatSessions.GetChatSummaries(activeId), Context = LoadContext(session), Messages = session.Messages, ContextUsage = completion.ContextUsage ?? ContextUsageEstimator.FromSession(session, settings), HtmlWorkspace = HtmlArtifactToolExecutor.NormalizeWorkspace(session.HtmlWorkspace) };
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

        private void StartChatTitleGeneration(ChatSession session, string userText, string assistantText, AppSettings settings, Action<ChatStateResponse> chatStateChanged)
        {
            if (session == null || !ChatTitleBuilder.ShouldAssign(session))
            {
                return;
            }

            var host = session.Host;
            var documentKey = session.DocumentKey;
            var documentTitle = session.DocumentTitle;
            var sessionId = ChatStore.GetSessionId(session);
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
                    var current = _chatStore.Load(host, documentKey, sessionId);
                    if (!ChatTitleBuilder.ShouldAssign(current))
                    {
                        return;
                    }

                    current.Title = title;
                    _chatStore.Save(current);
                    state = CreateStoredChatState(host, documentKey, documentTitle);
                }

                if (chatStateChanged != null)
                {
                    chatStateChanged(state);
                }
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
                ConfigUrl = LlmClient.BuildModelsConfigUrl(settings.BaseUrl),
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
            _paths.ClearRuntimeData();
            _chatSessions.Reset();
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
            var customTools = (tools ?? new ToolDefinition[0]).Where(s => s != null && !s.BuiltIn).ToList();
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

        public IReadOnlyList<SkillDefinition> GetSkills()
        {
            return _skillCatalog.GetVisibleSkills();
        }

        public IReadOnlyList<SkillDefinition> SaveSkills(IEnumerable<SkillDefinition> skills)
        {
            _skillStore.Save((skills ?? new SkillDefinition[0]).Where(s => !s.BuiltIn), _adapter.HostName);
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
                string.Equals(toolId, HtmlArtifactToolExecutor.SetActiveToolId, StringComparison.OrdinalIgnoreCase);
        }
    }
}
