using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Skills;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;
using RNAssistant.Office.Skills;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        private const int MaxAgentIterations = 3;
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly AppDataPaths _paths;
        private readonly SettingsService _settingsService;
        private readonly ChatStore _chatStore;
        private readonly ToolStore _toolStore;
        private readonly VbaBackupStore _vbaBackupStore;
        private readonly OfficeToolExecutor _toolExecutor;
        private readonly ToolCatalogService _toolCatalog;
        private readonly LlmClient _llmClient;
        private readonly PromptComposer _promptComposer;
        private readonly SkillCommandParser _commandParser;
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
            _promptComposer = new PromptComposer();
            _commandParser = new SkillCommandParser();
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

            ReportProgress(progress, "context", "Читаю документ...");
            var settings = _settingsService.Load();
            var session = LoadSession(chatId);
            ApplyChatModel(settings, session);
            session.Messages.Add(new ChatMessage { Role = "user", Content = text });
            EnsureSessionTitleFromUserText(session, text);

            var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
            var documentContext = LoadContext(session);
            var vbaSnapshot = string.Empty;
            var systemPrompt = _promptComposer.ComposeSystemPrompt(
                settings,
                _adapter.HostName,
                _adapter.GetDocumentSnapshot(settings.ContextCharLimit),
                vbaSnapshot,
                tools,
                null);
            var contextPrompt = _promptComposer.ComposeContextPrompt(documentContext);
            if (!string.IsNullOrWhiteSpace(contextPrompt))
            {
                ReportProgress(progress, "context", "Добавленный контекст включен в запрос: " + documentContext.Notes.Count + " item(s).");
            }

            object contextUsage = null;
            var assistantText = string.Empty;
            var resultLog = new List<object>();
            string followUpPrompt = null;
            for (var iteration = 0; iteration < MaxAgentIterations; iteration++)
            {
                var messages = PromptMessageBuilder.Build(systemPrompt, contextPrompt, session.Messages, settings.ContextCharLimit);
                if (!string.IsNullOrWhiteSpace(followUpPrompt))
                {
                    messages.Add(new ChatMessage { Role = "user", Content = followUpPrompt });
                }

                contextUsage = ContextUsageEstimator.FromPrompt(messages, settings);
                ReportProgress(progress, "thinking", iteration == 0 ? "Модель думает..." : "Модель продолжает агентскую задачу...");
                var completion = await _llmClient.CompleteAsync(settings, messages);
                assistantText = completion.Content ?? string.Empty;

                ReportProgress(progress, "processing", "Разбираю ответ...");
                var commands = _commandParser.Parse(assistantText).ToList();
                if (commands.Count == 0)
                {
                    if (iteration == 0 && settings.AgentModeEnabled != false && AgentTranscript.ShouldForceAgentToolUse(text, _adapter.HostName))
                    {
                        followUpPrompt = "You are in RNAssistant Agent mode. The user asked for an Office action, so a prose-only answer is not acceptable. Return only one ```rnassistant-agent fenced JSON block with executable steps using available tools. If a tool is missing, say that plainly instead of inventing one.";
                        continue;
                    }
                    session.Messages.Add(AgentTranscript.CreateAssistantMessage(assistantText, completion));
                    break;
                }

                session.Messages.Add(AgentTranscript.CreateAssistantMessage(AgentTranscript.CreateAgentPlanMessage(commands), completion));
                var shouldContinue = settings.AutoRunToolCalls != false;
                for (var i = 0; i < commands.Count; i++)
                {
                    var command = commands[i];
                    ReportProgress(
                        progress,
                        settings.AutoRunToolCalls != false ? "executing" : "waiting",
                        (settings.AutoRunToolCalls != false ? "Исполняю tool " : "Auto-run отключен для tool ") + (i + 1) + "/" + commands.Count + ": " + command.SkillId);
                    var result = settings.AutoRunToolCalls != false
                        ? _toolExecutor.Execute(command, tools, settings, false, false)
                        : SkillResult.Fail("Auto tool execution is disabled: " + command.SkillId);
                    resultLog.Add(AgentTranscript.DescribeResult(command, result));
                    AgentTranscript.AddLocalResultMessage(session, command, result);
                    if (!result.Success)
                    {
                        shouldContinue = false;
                    }
                    if (!result.Success && settings.AutoRunToolCalls != false && settings.AutoRetryToolErrors != false && AgentTranscript.CanRetryToolError(result))
                    {
                        ReportProgress(progress, "repairing", "Tool упал, прошу модель исправить вызов: " + command.SkillId);
                        await RetryFailedToolAsync(systemPrompt, contextPrompt, session, settings, tools, command, result, resultLog, progress);
                    }
                }

                if (!shouldContinue)
                {
                    break;
                }

                followUpPrompt = "Local tool results above are available. If the task is complete, answer the user normally. If more Office/VBA actions are needed, return one rnassistant-agent block with only the next commands.";
            }

            ReportProgress(progress, "saving", "Сохраняю историю...");
            _chatStore.Save(session);
            var activeId = ChatStore.GetSessionId(session);
            return JsonConvert.SerializeObject(new { message = assistantText, skillResults = resultLog, activeChatId = activeId, activeChatModel = session.Model, chats = GetChatSummaries(activeId), context = LoadContext(session), messages = session.Messages, contextUsage = contextUsage ?? ContextUsageEstimator.FromSession(session, settings) });
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

        private async Task RetryFailedToolAsync(
            string systemPrompt,
            string contextPrompt,
            ChatSession session,
            AppSettings settings,
            IReadOnlyList<SkillDefinition> tools,
            SkillCommand failedCommand,
            SkillResult failedResult,
            ICollection<object> resultLog,
            Action<string, string> progress)
        {
            var repairPrompt = "A local tool call failed. Return only corrected rnassistant-skill JSON block(s), no prose. " +
                "Original command: `" + failedCommand.SkillId + "` with arguments:\n```json\n" +
                JsonConvert.SerializeObject(failedCommand.Arguments, Formatting.Indented) +
                "\n```\nError: " + failedResult.Message +
                (string.IsNullOrWhiteSpace(failedResult.DataJson) ? string.Empty : "\nData:\n```json\n" + failedResult.DataJson + "\n```");
            var repairMessages = PromptMessageBuilder.Build(systemPrompt, contextPrompt, session.Messages, settings.ContextCharLimit);
            repairMessages.Add(new ChatMessage { Role = "user", Content = repairPrompt });

            var repairCompletion = await _llmClient.CompleteAsync(settings, repairMessages);
            var repairText = repairCompletion.Content ?? string.Empty;
            session.Messages.Add(AgentTranscript.CreateAssistantMessage(repairText, repairCompletion));
            var retryCommands = _commandParser.Parse(repairText).ToList();
            for (var i = 0; i < retryCommands.Count; i++)
            {
                var retry = retryCommands[i];
                ReportProgress(progress, "retrying", "Повтор tool " + (i + 1) + "/" + retryCommands.Count + ": " + retry.SkillId);
                var retryResult = _toolExecutor.Execute(retry, tools, settings, false, false);
                if (resultLog != null)
                {
                    resultLog.Add(AgentTranscript.DescribeResult(retry, retryResult));
                }
                AgentTranscript.AddLocalResultMessage(session, retry, retryResult);
            }

            if (retryCommands.Count == 0)
            {
                var noCommand = SkillResult.Fail("Auto-retry did not return a corrected tool call.");
                if (resultLog != null)
                {
                    resultLog.Add(new { skillId = "auto-retry", success = false, message = noCommand.Message, dataJson = noCommand.DataJson });
                }
                session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Local skill retry result: " + noCommand.Message });
            }
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
