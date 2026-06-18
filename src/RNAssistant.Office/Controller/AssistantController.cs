using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;
using RNAssistant.Office.Skills;
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

        public string InitializeJson()
        {
            var session = LoadSession(null);
            var activeId = ChatStore.GetSessionId(session);
            var context = LoadContext(session);
            var settings = _settingsService.Load();
            var state = new
            {
                host = _adapter.HostName,
                documentKey = _adapter.DocumentKey,
                title = _adapter.DocumentTitle,
                activeChatId = activeId,
                activeChatModel = session == null ? string.Empty : session.Model,
                chats = GetChatSummaries(activeId),
                settings = settings,
                hasApiKey = !string.IsNullOrWhiteSpace(_settingsService.LoadApiKey()),
                tools = _toolCatalog.GetVisibleTools(),
                toolsPath = _paths.ToolsDirectory,
                context = context,
                messages = session.Messages,
                contextUsage = ContextUsageEstimator.FromSession(session, settings),
                quickAction = DequeueQuickAction()
            };
            return JsonConvert.SerializeObject(state);
        }

        public async Task<string> SendChatAsync(string text, string chatId = null, Action<string, string> progress = null)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                var emptySession = LoadSession(chatId);
                var emptyId = ChatStore.GetSessionId(emptySession);
                return JsonConvert.SerializeObject(new { message = string.Empty, skillResults = new SkillResult[0], activeChatId = emptyId, activeChatModel = emptySession.Model, chats = GetChatSummaries(emptyId), context = LoadContext(emptySession), messages = emptySession.Messages, contextUsage = ContextUsageEstimator.FromSession(emptySession, _settingsService.Load()) });
            }

            var settings = _settingsService.Load();
            var session = LoadSession(chatId);
            var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
            var documentContext = LoadContext(session);
            var completion = await _chatCompletionService.ExecuteAsync(text, session, documentContext, settings, tools, progress);

            ReportProgress(progress, "saving", "Сохраняю историю...");
            _chatStore.Save(session);
            var activeId = ChatStore.GetSessionId(session);
            return JsonConvert.SerializeObject(new { message = completion.AssistantText, skillResults = completion.SkillResults, activeChatId = activeId, activeChatModel = session.Model, chats = GetChatSummaries(activeId), context = LoadContext(session), messages = session.Messages, contextUsage = completion.ContextUsage ?? ContextUsageEstimator.FromSession(session, settings) });
        }

        public string GetSettingsJson()
        {
            return JsonConvert.SerializeObject(new
            {
                settings = _settingsService.Load(),
                hasApiKey = !string.IsNullOrWhiteSpace(_settingsService.LoadApiKey())
            });
        }

        public async Task<string> GetModelCatalogJsonAsync(string settingsJson, string apiKey)
        {
            var settings = string.IsNullOrWhiteSpace(settingsJson)
                ? _settingsService.Load()
                : (JsonConvert.DeserializeObject<AppSettings>(settingsJson) ?? _settingsService.Load());
            var json = await _llmClient.GetModelsConfigJsonAsync(
                settings,
                string.IsNullOrWhiteSpace(apiKey) ? null : apiKey).ConfigureAwait(false);

            return JsonConvert.SerializeObject(new
            {
                configUrl = LlmClient.BuildModelsConfigUrl(settings.BaseUrl),
                catalog = JToken.Parse(json)
            });
        }

        public string SaveSettingsJson(string settingsJson, string apiKey)
        {
            var settings = JsonConvert.DeserializeObject<AppSettings>(settingsJson) ?? new AppSettings();
            _settingsService.Save(settings);
            if (apiKey != null)
            {
                _settingsService.SaveApiKey(apiKey);
            }

            return GetSettingsJson();
        }

        public string ClearRuntimeDataJson()
        {
            _paths.ClearRuntimeData();
            _activeSessionId = null;
            _activeHost = null;
            _activeDocumentKey = null;
            _activeRuntimeDocumentKey = null;
            return InitializeJson();
        }

        public string GetToolsJson()
        {
            return JsonConvert.SerializeObject(_toolCatalog.GetVisibleTools());
        }

        public string SaveToolsJson(string toolsJson)
        {
            var tools = JsonConvert.DeserializeObject<List<SkillDefinition>>(toolsJson) ?? new List<SkillDefinition>();
            _toolStore.Save(tools.Where(s => !s.BuiltIn), _adapter.HostName);
            return GetToolsJson();
        }

        public string RunToolJson(string toolId, string argumentsJson, bool dryRun, Action<string, string> progress = null)
        {
            var settings = _settingsService.Load();
            var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
            var command = new SkillCommand { SkillId = toolId };
            var args = SkillArgumentReader.ParseObject(argumentsJson);
            foreach (var pair in args)
            {
                command.Arguments[pair.Key] = pair.Value;
            }

            ReportProgress(progress, dryRun ? "checking" : "executing", (dryRun ? "Проверяю tool: " : "Исполняю tool: ") + toolId);
            var result = _toolExecutor.Execute(command, tools, settings, dryRun, true);
            return JsonConvert.SerializeObject(result);
        }

        public string GetVbaProjectJson(int maxChars)
        {
            var settings = _settingsService.Load();
            var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
            var command = new SkillCommand { SkillId = _toolExecutor.VbaToolId("vba_read_project") };
            command.Arguments["maxChars"] = maxChars <= 0 ? settings.VbaContextCharLimit : maxChars;
            var result = _toolExecutor.Execute(command, tools, settings, false, true);
            return JsonConvert.SerializeObject(new
            {
                result = result,
                backups = _vbaBackupStore.List(_adapter.HostName, _adapter.DocumentKey)
            });
        }

        public string SaveVbaModuleJson(string moduleName, string code)
        {
            var settings = _settingsService.Load();
            var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
            var command = new SkillCommand { SkillId = _toolExecutor.VbaToolId("vba_replace_module") };
            command.Arguments["moduleName"] = moduleName;
            command.Arguments["code"] = code;
            command.Arguments["createIfMissing"] = "true";
            var result = _toolExecutor.Execute(command, tools, settings, false, true);
            return JsonConvert.SerializeObject(result);
        }

        public string RestoreVbaBackupJson(string backupId, string moduleName)
        {
            var settings = _settingsService.Load();
            var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
            var result = _toolExecutor.Execute(new SkillCommand
            {
                SkillId = _toolExecutor.VbaToolId("vba_restore_backup"),
                Arguments =
                {
                    ["backupId"] = backupId ?? string.Empty,
                    ["moduleName"] = moduleName ?? string.Empty
                }
            }, tools, settings, false, true);
            return JsonConvert.SerializeObject(result);
        }

        public void QueueQuickAction(string action)
        {
            lock (_syncRoot)
            {
                _queuedQuickAction = action;
            }
        }

        public Task<string> RunQuickActionAsync(string action)
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

            return Task.FromResult(JsonConvert.SerializeObject(new { prompt = prompt }));
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
