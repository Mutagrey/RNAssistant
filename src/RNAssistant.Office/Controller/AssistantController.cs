using System;
using System.Collections.Generic;
using System.Linq;
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
        private readonly ToolStore _toolStore;
        private readonly VbaBackupStore _vbaBackupStore;
        private readonly OfficeToolExecutor _toolExecutor;
        private readonly ToolCatalogService _toolCatalog;
        private readonly ChatCompletionService _chatCompletionService;
        private readonly ContextService _contextService;
        private readonly LlmClient _llmClient;
        private readonly object _syncRoot;
        private string _queuedQuickAction;
        private string _activeSessionId;
        private string _activeHost;
        private string _activeDocumentKey;
        private string _activeRuntimeDocumentKey;

        public AssistantController(IOfficeApplicationAdapter adapter)
        {
            _adapter = adapter;
            _paths = AppDataPaths.CreateDefault();
            _settingsService = new SettingsService(_paths);
            _chatStore = new ChatStore(_paths);
            _toolStore = new ToolStore(_paths);
            _vbaBackupStore = new VbaBackupStore(_paths);
            _toolExecutor = new OfficeToolExecutor(_adapter, _vbaBackupStore);
            _toolCatalog = new ToolCatalogService(_adapter, _toolExecutor, _toolStore);
            _llmClient = new LlmClient(() => _settingsService.LoadApiKey());
            _chatCompletionService = new ChatCompletionService(_adapter, _toolExecutor, _llmClient.CompleteAsync);
            _contextService = new ContextService(_adapter);
            _syncRoot = new object();
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
                ActiveChatId = activeId,
                ActiveChatModel = session == null ? string.Empty : session.Model,
                Chats = GetChatSummaries(activeId),
                Settings = settings,
                HasApiKey = !string.IsNullOrWhiteSpace(_settingsService.LoadApiKey()),
                Tools = _toolCatalog.GetVisibleTools(),
                ToolsPath = _paths.ToolsDirectory,
                Context = context,
                Messages = session.Messages,
                ContextUsage = ContextUsageEstimator.FromSession(session, settings),
                QuickAction = DequeueQuickAction()
            };
        }

        public async Task<SendChatResponse> SendChatAsync(string text, string chatId = null, Action<string, string> progress = null)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                var emptySession = LoadSession(chatId);
                var emptyId = ChatStore.GetSessionId(emptySession);
                return new SendChatResponse { Message = string.Empty, SkillResults = new object[0], ActiveChatId = emptyId, ActiveChatModel = emptySession.Model, Chats = GetChatSummaries(emptyId), Context = LoadContext(emptySession), Messages = emptySession.Messages, ContextUsage = ContextUsageEstimator.FromSession(emptySession, _settingsService.Load()) };
            }

            var settings = _settingsService.Load();
            var session = LoadSession(chatId);
            var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
            var documentContext = LoadContext(session);
            var completion = await _chatCompletionService.ExecuteAsync(text, session, documentContext, settings, tools, progress);

            ReportProgress(progress, "saving", "Сохраняю историю...");
            _chatStore.Save(session);
            var activeId = ChatStore.GetSessionId(session);
            return new SendChatResponse { Message = completion.AssistantText, SkillResults = completion.SkillResults, ActiveChatId = activeId, ActiveChatModel = session.Model, Chats = GetChatSummaries(activeId), Context = LoadContext(session), Messages = session.Messages, ContextUsage = completion.ContextUsage ?? ContextUsageEstimator.FromSession(session, settings) };
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

            return new ModelCatalogResponse
            {
                ConfigUrl = LlmClient.BuildModelsConfigUrl(settings.BaseUrl),
                Catalog = JToken.Parse(json)
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
            _activeSessionId = null;
            _activeHost = null;
            _activeDocumentKey = null;
            _activeRuntimeDocumentKey = null;
            return Initialize();
        }

        public IReadOnlyList<SkillDefinition> GetTools()
        {
            return _toolCatalog.GetVisibleTools();
        }

        public IReadOnlyList<SkillDefinition> SaveTools(IEnumerable<SkillDefinition> tools)
        {
            _toolStore.Save((tools ?? new SkillDefinition[0]).Where(s => !s.BuiltIn), _adapter.HostName);
            return GetTools();
        }

        public SkillResult RunTool(string toolId, IDictionary<string, object> arguments, bool dryRun, Action<string, string> progress = null)
        {
            var settings = _settingsService.Load();
            var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
            var command = new SkillCommand { SkillId = toolId };
            foreach (var pair in arguments ?? new Dictionary<string, object>())
            {
                command.Arguments[pair.Key] = pair.Value;
            }

            ReportProgress(progress, dryRun ? "checking" : "executing", (dryRun ? "Проверяю tool: " : "Исполняю tool: ") + toolId);
            return _toolExecutor.Execute(command, tools, settings, dryRun, true);
        }

        public VbaProjectResponse GetVbaProject(int maxChars)
        {
            var settings = _settingsService.Load();
            var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
            var command = new SkillCommand { SkillId = _toolExecutor.VbaToolId("vba_read_project") };
            command.Arguments["maxChars"] = maxChars <= 0 ? settings.VbaContextCharLimit : maxChars;
            var result = _toolExecutor.Execute(command, tools, settings, false, true);
            return new VbaProjectResponse
            {
                Result = result,
                Backups = _vbaBackupStore.List(_adapter.HostName, _adapter.DocumentKey)
            };
        }

        public SkillResult SaveVbaModule(string moduleName, string code)
        {
            var settings = _settingsService.Load();
            var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
            var command = new SkillCommand { SkillId = _toolExecutor.VbaToolId("vba_replace_module") };
            command.Arguments["moduleName"] = moduleName;
            command.Arguments["code"] = code;
            command.Arguments["createIfMissing"] = "true";
            return _toolExecutor.Execute(command, tools, settings, false, true);
        }

        public SkillResult RestoreVbaBackup(string backupId, string moduleName)
        {
            var settings = _settingsService.Load();
            var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
            return _toolExecutor.Execute(new SkillCommand
            {
                SkillId = _toolExecutor.VbaToolId("vba_restore_backup"),
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
    }
}
