using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Skills;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office
{
    public sealed class AssistantController
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly AppDataPaths _paths;
        private readonly SettingsService _settingsService;
        private readonly ChatStore _chatStore;
        private readonly ContextStore _contextStore;
        private readonly ToolStore _toolStore;
        private readonly VbaBackupStore _vbaBackupStore;
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
            _contextStore = new ContextStore(_paths);
            _toolStore = new ToolStore(_paths);
            _vbaBackupStore = new VbaBackupStore(_paths);
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
            var state = new
            {
                host = _adapter.HostName,
                documentKey = _adapter.DocumentKey,
                title = _adapter.DocumentTitle,
                activeChatId = activeId,
                chats = GetChatSummaries(activeId),
                settings = _settingsService.Load(),
                hasApiKey = !string.IsNullOrWhiteSpace(_settingsService.LoadApiKey()),
                tools = GetVisibleTools(),
                toolsPath = _paths.ToolsDirectory,
                context = LoadContext(),
                messages = session.Messages,
                contextUsage = EstimateContextUsage(session, _settingsService.Load()),
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
                return JsonConvert.SerializeObject(new { message = string.Empty, skillResults = new SkillResult[0], activeChatId = emptyId, chats = GetChatSummaries(emptyId), messages = emptySession.Messages, contextUsage = EstimateContextUsage(emptySession, _settingsService.Load()) });
            }

            ReportProgress(progress, "context", "Читаю документ...");
            var settings = _settingsService.Load();
            var session = LoadSession(chatId);
            session.Messages.Add(new ChatMessage { Role = "user", Content = text });
            EnsureSessionTitleFromUserText(session, text);

            var tools = GetVisibleTools().Where(s => s.Enabled).ToList();
            var documentContext = LoadContext();
            var vbaSnapshot = settings.IncludeVbaContext
                ? _adapter.GetVbaSnapshot(settings.VbaContextCharLimit)
                : string.Empty;
            var systemPrompt = _promptComposer.ComposeSystemPrompt(
                settings,
                _adapter.HostName,
                _adapter.GetDocumentSnapshot(settings.ContextCharLimit),
                vbaSnapshot,
                tools,
                documentContext);

            var messages = BuildPromptMessages(systemPrompt, session.Messages, settings.ContextCharLimit);
            var contextUsage = CreateContextUsage(messages, settings);
            ReportProgress(progress, "thinking", "Модель думает...");
            var completion = await _llmClient.CompleteAsync(settings, messages);
            var assistantText = completion.Content ?? string.Empty;
            var assistantMessage = CreateAssistantMessage(assistantText, completion);
            session.Messages.Add(assistantMessage);

            ReportProgress(progress, "processing", "Разбираю ответ...");
            var commands = _commandParser.Parse(assistantText).ToList();
            var resultLog = new List<object>();
            for (var i = 0; i < commands.Count; i++)
            {
                var command = commands[i];
                ReportProgress(
                    progress,
                    settings.AutoRunToolCalls != false ? "executing" : "waiting",
                    (settings.AutoRunToolCalls != false ? "Исполняю tool " : "Auto-run отключен для tool ") + (i + 1) + "/" + commands.Count + ": " + command.SkillId);
                var result = settings.AutoRunToolCalls != false
                    ? ExecuteCommand(command, tools, settings, 0, false, false)
                    : SkillResult.Fail("Auto tool execution is disabled: " + command.SkillId);
                resultLog.Add(DescribeResult(command, result));
                AddLocalResultMessage(session, command, result);
                if (!result.Success && settings.AutoRunToolCalls != false && settings.AutoRetryToolErrors != false && CanRetryToolError(result))
                {
                    ReportProgress(progress, "repairing", "Tool упал, прошу модель исправить вызов: " + command.SkillId);
                    await RetryFailedToolAsync(systemPrompt, session, settings, tools, command, result, resultLog, progress);
                }
            }

            ReportProgress(progress, "saving", "Сохраняю историю...");
            _chatStore.Save(session);
            var activeId = ChatStore.GetSessionId(session);
            return JsonConvert.SerializeObject(new { message = assistantText, skillResults = resultLog, activeChatId = activeId, chats = GetChatSummaries(activeId), messages = session.Messages, contextUsage = contextUsage });
        }

        public string DeleteMessageJson(string id, int index, string chatId = null)
        {
            var session = LoadSession(chatId);
            var removed = false;
            if (!string.IsNullOrWhiteSpace(id))
            {
                removed = session.Messages.RemoveAll(m => m != null && string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
            }

            if (!removed && index >= 0 && index < session.Messages.Count)
            {
                session.Messages.RemoveAt(index);
                removed = true;
            }

            if (removed)
            {
                _chatStore.Save(session);
            }

            var activeId = ChatStore.GetSessionId(session);
            return JsonConvert.SerializeObject(new { activeChatId = activeId, chats = GetChatSummaries(activeId), messages = session.Messages, contextUsage = EstimateContextUsage(session, _settingsService.Load()) });
        }

        public string ListChatsJson()
        {
            var session = LoadSession(null);
            return ChatStateJson(session);
        }

        public string CreateChatJson(string title)
        {
            LoadSession(null);
            var session = _chatStore.Create(_adapter.HostName, _adapter.DocumentKey, _adapter.DocumentTitle, string.IsNullOrWhiteSpace(title) ? "New chat" : title.Trim());
            SetActiveSession(session);
            return ChatStateJson(session);
        }

        public string SelectChatJson(string chatId)
        {
            var session = LoadSession(chatId);
            return ChatStateJson(session);
        }

        public string RenameChatJson(string chatId, string title)
        {
            var session = LoadSession(chatId);
            if (!string.IsNullOrWhiteSpace(title))
            {
                session.Title = title.Trim();
                _chatStore.Save(session);
            }

            return ChatStateJson(session);
        }

        public string ClearChatJson(string chatId)
        {
            var session = LoadSession(chatId);
            session.Messages.Clear();
            _chatStore.Save(session);
            return ChatStateJson(session);
        }

        public string DeleteChatJson(string chatId)
        {
            var current = LoadSession(chatId);
            _chatStore.Delete(_adapter.HostName, _adapter.DocumentKey, ChatStore.GetSessionId(current));
            var next = _chatStore.List(_adapter.HostName, _adapter.DocumentKey, _adapter.DocumentTitle).FirstOrDefault();
            if (next == null)
            {
                next = _chatStore.Create(_adapter.HostName, _adapter.DocumentKey, _adapter.DocumentTitle, "New chat");
            }

            SetActiveSession(next);
            return ChatStateJson(next);
        }

        public string GetSettingsJson()
        {
            return JsonConvert.SerializeObject(new
            {
                settings = _settingsService.Load(),
                hasApiKey = !string.IsNullOrWhiteSpace(_settingsService.LoadApiKey())
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

        public string GetToolsJson()
        {
            return JsonConvert.SerializeObject(GetVisibleTools());
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
            var tools = GetVisibleTools().Where(s => s.Enabled).ToList();
            var command = new SkillCommand { SkillId = toolId };
            var args = ParseArguments(argumentsJson);
            foreach (var pair in args)
            {
                command.Arguments[pair.Key] = pair.Value;
            }

            ReportProgress(progress, dryRun ? "checking" : "executing", (dryRun ? "Проверяю tool: " : "Исполняю tool: ") + toolId);
            var result = ExecuteCommand(command, tools, settings, 0, dryRun, true);
            return JsonConvert.SerializeObject(result);
        }

        public string GetVbaProjectJson(int maxChars)
        {
            var settings = _settingsService.Load();
            var tools = GetVisibleTools().Where(s => s.Enabled).ToList();
            var command = new SkillCommand { SkillId = VbaToolId("vba_read_project") };
            command.Arguments["maxChars"] = maxChars <= 0 ? settings.VbaContextCharLimit : maxChars;
            var result = ExecuteCommand(command, tools, settings, 0, false, true);
            return JsonConvert.SerializeObject(new
            {
                result = result,
                backups = _vbaBackupStore.List(_adapter.HostName, _adapter.DocumentKey)
            });
        }

        public string SaveVbaModuleJson(string moduleName, string code)
        {
            var settings = _settingsService.Load();
            var tools = GetVisibleTools().Where(s => s.Enabled).ToList();
            var command = new SkillCommand { SkillId = VbaToolId("vba_replace_module") };
            command.Arguments["moduleName"] = moduleName;
            command.Arguments["code"] = code;
            command.Arguments["createIfMissing"] = "true";
            var result = ExecuteCommand(command, tools, settings, 0, false, true);
            return JsonConvert.SerializeObject(result);
        }

        public string RestoreVbaBackupJson(string backupId, string moduleName)
        {
            var settings = _settingsService.Load();
            var tools = GetVisibleTools().Where(s => s.Enabled).ToList();
            var result = RestoreVbaBackup(new SkillCommand
            {
                SkillId = VbaToolId("vba_restore_backup"),
                Arguments =
                {
                    ["backupId"] = backupId ?? string.Empty,
                    ["moduleName"] = moduleName ?? string.Empty
                }
            }, tools, settings, false, true);
            return JsonConvert.SerializeObject(result);
        }

        public string GetContextJson()
        {
            return JsonConvert.SerializeObject(LoadContext());
        }

        public string AddSelectionContextJson(string mode)
        {
            return JsonConvert.SerializeObject(AddSelectionContext(mode));
        }

        public DocumentContext AddSelectionContext(string mode)
        {
            var settings = _settingsService.Load();
            var context = LoadContext();
            if (context.Notes == null)
            {
                context.Notes = new List<ContextNote>();
            }
            var note = _adapter.CaptureSelectionContext(mode, Math.Min(Math.Max(1000, settings.ContextCharLimit), 12000));
            if (note == null)
            {
                throw new InvalidOperationException("No selectable Office context was found.");
            }

            NormalizeContextNote(note, mode);
            context.Notes.Add(note);
            _contextStore.Save(context);
            return context;
        }

        public string RemoveContextItemJson(string id)
        {
            var context = LoadContext();
            if (context.Notes != null && !string.IsNullOrWhiteSpace(id))
            {
                context.Notes.RemoveAll(n => n != null && string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase));
                _contextStore.Save(context);
            }

            return JsonConvert.SerializeObject(context);
        }

        public string ClearContextJson()
        {
            _contextStore.Clear(_adapter.HostName, _adapter.DocumentKey);
            return GetContextJson();
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
            var repairMessages = BuildPromptMessages(systemPrompt, session.Messages, settings.ContextCharLimit);
            repairMessages.Add(new ChatMessage { Role = "user", Content = repairPrompt });

            var repairCompletion = await _llmClient.CompleteAsync(settings, repairMessages);
            var repairText = repairCompletion.Content ?? string.Empty;
            session.Messages.Add(CreateAssistantMessage(repairText, repairCompletion));
            var retryCommands = _commandParser.Parse(repairText).ToList();
            for (var i = 0; i < retryCommands.Count; i++)
            {
                var retry = retryCommands[i];
                ReportProgress(progress, "retrying", "Повтор tool " + (i + 1) + "/" + retryCommands.Count + ": " + retry.SkillId);
                var retryResult = ExecuteCommand(retry, tools, settings, 0, false, false);
                if (resultLog != null)
                {
                    resultLog.Add(DescribeResult(retry, retryResult));
                }
                AddLocalResultMessage(session, retry, retryResult);
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

        private static void AddLocalResultMessage(ChatSession session, SkillCommand command, SkillResult result)
        {
            session.Messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = "Local skill result for `" + command.SkillId + "`: " + result.Message + (string.IsNullOrWhiteSpace(result.DataJson) ? string.Empty : "\n```json\n" + result.DataJson + "\n```")
            });
        }

        private static ChatMessage CreateAssistantMessage(string content, LlmCompletionResult completion)
        {
            return new ChatMessage
            {
                Role = "assistant",
                Content = content ?? string.Empty,
                PromptTokens = completion == null ? null : completion.PromptTokens,
                CompletionTokens = completion == null ? null : completion.CompletionTokens,
                TotalTokens = completion == null ? null : completion.TotalTokens,
                UsageJson = completion == null ? null : completion.UsageJson
            };
        }

        private static object DescribeResult(SkillCommand command, SkillResult result)
        {
            return new
            {
                skillId = command == null ? string.Empty : command.SkillId,
                success = result != null && result.Success,
                message = result == null ? string.Empty : result.Message,
                dataJson = result == null ? null : result.DataJson
            };
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

        private ChatSession LoadSession(string requestedSessionId)
        {
            var host = _adapter.HostName;
            var documentKey = _adapter.DocumentKey;
            var legacyDocumentKey = _adapter.LegacyDocumentKey;
            var runtimeKey = _adapter.RuntimeDocumentKey;
            var title = _adapter.DocumentTitle;

            if (!string.IsNullOrWhiteSpace(_activeSessionId) &&
                string.Equals(_activeHost, host, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_activeRuntimeDocumentKey, runtimeKey, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(_activeDocumentKey, documentKey, StringComparison.OrdinalIgnoreCase))
            {
                var oldDocumentKey = _activeDocumentKey;
                _contextStore.Move(_activeHost, oldDocumentKey, host, documentKey, title);
                _chatStore.MoveDocument(_activeHost, oldDocumentKey, host, documentKey, title);
                _activeHost = host;
                _activeDocumentKey = documentKey;
                _activeRuntimeDocumentKey = runtimeKey;
            }

            RestoreLegacyDocumentKeyIfNeeded(host, legacyDocumentKey, documentKey, title);

            ChatSession session = null;
            if (!string.IsNullOrWhiteSpace(requestedSessionId))
            {
                session = _chatStore.Load(host, documentKey, requestedSessionId);
                if (session == null)
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
            return session;
        }

        private void RestoreLegacyDocumentKeyIfNeeded(string host, string legacyDocumentKey, string documentKey, string title)
        {
            if (string.IsNullOrWhiteSpace(legacyDocumentKey) ||
                string.Equals(legacyDocumentKey, documentKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_chatStore.List(host, documentKey, title).Count > 0)
            {
                return;
            }

            if (_chatStore.List(host, legacyDocumentKey, title).Count > 0)
            {
                _chatStore.MoveDocument(host, legacyDocumentKey, host, documentKey, title);
                _contextStore.Move(host, legacyDocumentKey, host, documentKey, title);
                return;
            }

            if (!_contextStore.Exists(host, documentKey) && _contextStore.Exists(host, legacyDocumentKey))
            {
                _contextStore.Move(host, legacyDocumentKey, host, documentKey, title);
            }
        }

        private DocumentContext LoadContext()
        {
            var context = _contextStore.LoadOrCreate(_adapter.HostName, _adapter.DocumentKey, _adapter.DocumentTitle);
            if (string.IsNullOrWhiteSpace(context.Host))
            {
                context.Host = _adapter.HostName;
            }
            if (string.IsNullOrWhiteSpace(context.DocumentKey))
            {
                context.DocumentKey = _adapter.DocumentKey;
            }
            if (string.IsNullOrWhiteSpace(context.Title))
            {
                context.Title = _adapter.DocumentTitle;
            }
            if (context.Notes == null)
            {
                context.Notes = new List<ContextNote>();
            }
            return context;
        }

        private void SetActiveSession(ChatSession session)
        {
            if (session == null)
            {
                return;
            }

            _activeSessionId = ChatStore.GetSessionId(session);
            _activeHost = session.Host;
            _activeDocumentKey = session.DocumentKey;
            _activeRuntimeDocumentKey = _adapter.RuntimeDocumentKey;
            _chatStore.SaveActiveSessionId(session.Host, session.DocumentKey, _activeSessionId);
        }

        private string ChatStateJson(ChatSession session)
        {
            var activeId = ChatStore.GetSessionId(session);
            return JsonConvert.SerializeObject(new
            {
                activeChatId = activeId,
                chats = GetChatSummaries(activeId),
                messages = session == null ? new List<ChatMessage>() : session.Messages,
                contextUsage = EstimateContextUsage(session, _settingsService.Load())
            });
        }

        private IReadOnlyList<ChatSessionSummary> GetChatSummaries(string activeId)
        {
            return _chatStore.List(_adapter.HostName, _adapter.DocumentKey, _adapter.DocumentTitle)
                .Select(s => new ChatSessionSummary
                {
                    Id = ChatStore.GetSessionId(s),
                    Host = s.Host,
                    DocumentKey = s.DocumentKey,
                    DocumentTitle = s.DocumentTitle,
                    Title = s.Title,
                    CreatedUtc = s.CreatedUtc,
                    UpdatedUtc = s.UpdatedUtc,
                    MessageCount = s.Messages == null ? 0 : s.Messages.Count
                })
                .ToList();
        }

        private static void EnsureSessionTitleFromUserText(ChatSession session, string text)
        {
            if (session == null || string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            if (!string.Equals(session.Title, "New chat", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var title = Regex.Replace(text.Trim(), "\\s+", " ");
            session.Title = title.Length <= 64 ? title : title.Substring(0, 61) + "...";
        }

        private void NormalizeContextNote(ContextNote note, string mode)
        {
            if (string.IsNullOrWhiteSpace(note.Id))
            {
                note.Id = Guid.NewGuid().ToString("N");
            }
            if (note.CreatedUtc == default(DateTime))
            {
                note.CreatedUtc = DateTime.UtcNow;
            }
            if (string.IsNullOrWhiteSpace(note.Host))
            {
                note.Host = _adapter.HostName;
            }
            if (string.IsNullOrWhiteSpace(note.Kind))
            {
                note.Kind = string.Equals(mode, "reference", StringComparison.OrdinalIgnoreCase) ? "reference" : "selection";
            }
            if (string.IsNullOrWhiteSpace(note.Title))
            {
                note.Title = _adapter.DocumentTitle;
            }
            if (string.IsNullOrWhiteSpace(note.Reference))
            {
                note.Reference = note.Source ?? _adapter.DocumentTitle;
            }
            if (string.IsNullOrWhiteSpace(note.Source))
            {
                note.Source = note.Reference;
            }
            if (string.IsNullOrWhiteSpace(note.Preview))
            {
                note.Preview = TrimForContext(note.Text, 360);
            }
            if (string.IsNullOrWhiteSpace(note.Text))
            {
                note.Text = note.Preview;
            }
        }

        private static string TrimForContext(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            {
                return text ?? string.Empty;
            }

            return text.Substring(0, maxChars) + "\n...[truncated]";
        }

        private List<SkillDefinition> GetVisibleTools()
        {
            var result = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var skill in _adapter.GetBuiltInSkills() ?? new SkillDefinition[0])
            {
                result[skill.Id] = skill;
            }

            foreach (var tool in GetControllerTools())
            {
                result[tool.Id] = tool;
            }

            foreach (var tool in _toolStore.Load().Where(s =>
                string.Equals(s.Host, _adapter.HostName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Host, "Common", StringComparison.OrdinalIgnoreCase)))
            {
                if (!string.IsNullOrWhiteSpace(tool.Id))
                {
                    result[tool.Id] = tool;
                }
            }

            return result.Values.OrderBy(s => s.Host).ThenBy(s => s.Id).ToList();
        }

        private IEnumerable<SkillDefinition> GetControllerTools()
        {
            if (HostSupportsVba())
            {
                var listId = VbaToolId("vba_list_backups");
                yield return ControllerTool(listId, "List RNAssistant VBA rollback backups for the current document.", "{}");
                var restoreId = VbaToolId("vba_restore_backup");
                yield return ControllerTool(restoreId, "Restore a VBA module from a prior RNAssistant backup by backupId, or latest backup for moduleName.", "{\"backupId\":\"optional\",\"moduleName\":\"Module1\"}");
                var replaceTextId = VbaToolId("vba_replace_text");
                yield return ControllerTool(replaceTextId, "Replace an exact text fragment inside one VBA module; safer than replacing the whole module and creates a rollback backup.", "{\"moduleName\":\"Module1\",\"find\":\"old code\",\"replace\":\"new code\"}");
                var patchId = VbaToolId("vba_apply_patch");
                yield return ControllerTool(patchId, "Apply structured VBA code patches: replace exact text, insert before/after exact text, or replace line ranges; creates rollback backup.", "{\"moduleName\":\"Module1\",\"patch\":[{\"op\":\"replace\",\"find\":\"old\",\"text\":\"new\"},{\"op\":\"replaceLines\",\"startLine\":10,\"deleteCount\":2,\"text\":\"new code\"}]}");
            }
        }

        private SkillDefinition ControllerTool(string id, string description, string schema)
        {
            return new SkillDefinition
            {
                Id = id,
                Host = _adapter.HostName,
                Name = id,
                Description = description,
                ArgumentSchemaJson = schema,
                BuiltIn = true,
                Enabled = true
            };
        }

        private SkillResult ExecuteCommand(SkillCommand command, IReadOnlyList<SkillDefinition> skills, AppSettings settings, int depth, bool dryRun, bool manualRun)
        {
            if (command == null || string.IsNullOrWhiteSpace(command.SkillId))
            {
                return SkillResult.Fail("Tool command is empty.");
            }

            if (depth > 8)
            {
                return SkillResult.Fail("Pipeline nesting limit exceeded.");
            }

            var tool = skills.FirstOrDefault(s =>
                !s.BuiltIn &&
                s.Enabled &&
                string.Equals(s.Id, command.SkillId, StringComparison.OrdinalIgnoreCase));

            if (IsVbaMutationTool(command.SkillId) && !settings.AutoConfirmToolActions && !manualRun)
            {
                return SkillResult.Fail("VBA tool requires confirmation before execution: " + command.SkillId);
            }

            if (tool != null && tool.RequiresConfirmation && !settings.AutoConfirmToolActions && !manualRun)
            {
                return SkillResult.Fail("Tool requires confirmation before execution: " + tool.Id);
            }

            if (tool != null && string.Equals(tool.Executor, "pipeline", StringComparison.OrdinalIgnoreCase))
            {
                return ExecutePipeline(tool, command, skills, settings, depth + 1, dryRun, manualRun);
            }

            if (tool != null && string.Equals(tool.Executor, "vba", StringComparison.OrdinalIgnoreCase))
            {
                return ExecuteVbaTool(tool, command, skills, settings, dryRun, manualRun);
            }

            if (tool != null)
            {
                return SkillResult.Fail("Tool executor is not runnable yet: " + tool.Executor);
            }

            if (string.Equals(command.SkillId, VbaToolId("vba_list_backups"), StringComparison.OrdinalIgnoreCase))
            {
                return SkillResult.Ok("VBA backups listed.", JsonConvert.SerializeObject(_vbaBackupStore.List(_adapter.HostName, _adapter.DocumentKey)));
            }

            if (string.Equals(command.SkillId, VbaToolId("vba_restore_backup"), StringComparison.OrdinalIgnoreCase))
            {
                return RestoreVbaBackup(command, skills, settings, dryRun, manualRun);
            }

            if (string.Equals(command.SkillId, VbaToolId("vba_replace_text"), StringComparison.OrdinalIgnoreCase))
            {
                return ReplaceVbaText(command, skills, settings, dryRun, manualRun);
            }

            if (string.Equals(command.SkillId, VbaToolId("vba_apply_patch"), StringComparison.OrdinalIgnoreCase))
            {
                return ApplyVbaPatch(command, skills, settings, dryRun, manualRun);
            }

            if (dryRun)
            {
                return SkillResult.Ok("Dry run: would execute " + command.SkillId, JsonConvert.SerializeObject(command.Arguments));
            }

            if (string.Equals(command.SkillId, VbaToolId("vba_replace_module"), StringComparison.OrdinalIgnoreCase))
            {
                BackupVbaModuleBeforeReplace(command, skills, settings);
            }

            return _adapter.ExecuteSkill(command);
        }

        private SkillResult ExecutePipeline(SkillDefinition tool, SkillCommand command, IReadOnlyList<SkillDefinition> skills, AppSettings settings, int depth, bool dryRun, bool manualRun)
        {
            if (string.IsNullOrWhiteSpace(tool.PipelineJson))
            {
                return SkillResult.Fail("Tool has no pipeline: " + tool.Id);
            }

            JObject pipeline;
            try
            {
                pipeline = JObject.Parse(tool.PipelineJson);
            }
            catch (JsonException ex)
            {
                return SkillResult.Fail("Invalid pipeline JSON for " + tool.Id + ": " + ex.Message);
            }

            var steps = pipeline["steps"] as JArray;
            if (steps == null || steps.Count == 0)
            {
                return SkillResult.Fail("Pipeline has no steps: " + tool.Id);
            }

            var stepResults = new Dictionary<string, SkillResult>(StringComparer.OrdinalIgnoreCase);
            var output = new List<object>();
            foreach (var stepToken in steps)
            {
                var step = stepToken as JObject;
                if (step == null)
                {
                    continue;
                }

                var toolId = (string)(step["toolId"] ?? step["skillId"] ?? step["id"]);
                if (string.IsNullOrWhiteSpace(toolId))
                {
                    return SkillResult.Fail("Pipeline step has no toolId.");
                }

                var stepId = (string)step["id"];
                if (string.IsNullOrWhiteSpace(stepId))
                {
                    stepId = toolId;
                }

                var nested = new SkillCommand { SkillId = toolId };
                var args = step["arguments"] as JObject;
                if (args != null)
                {
                    foreach (var property in args.Properties())
                    {
                        nested.Arguments[property.Name] = ResolvePipelineValue(property.Value, command.Arguments, stepResults);
                    }
                }

                var result = ExecuteCommand(nested, skills, settings, depth + 1, dryRun, manualRun);
                stepResults[stepId] = result;
                output.Add(new { id = stepId, toolId = toolId, success = result.Success, message = result.Message, dataJson = result.DataJson });

                if (!result.Success)
                {
                    return SkillResult.Fail("Pipeline step failed: " + stepId + ". " + result.Message);
                }
            }

            return SkillResult.Ok((dryRun ? "Dry run completed: " : "Pipeline executed: ") + tool.Id, JsonConvert.SerializeObject(new { toolId = tool.Id, dryRun = dryRun, steps = output }));
        }

        private SkillResult ExecuteVbaTool(SkillDefinition tool, SkillCommand command, IReadOnlyList<SkillDefinition> skills, AppSettings settings, bool dryRun, bool manualRun)
        {
            if (string.IsNullOrWhiteSpace(tool.Code))
            {
                return SkillResult.Fail("VBA tool has no code: " + tool.Id);
            }
            if (!dryRun && !manualRun && !settings.AutoConfirmToolActions)
            {
                return SkillResult.Fail("VBA tool requires confirmation before execution: " + tool.Id);
            }

            var moduleName = GetArgument(command.Arguments, "moduleName", ToolModuleName(tool.Id));
            var macroName = GetArgument(command.Arguments, "macroName", string.Empty);
            if (dryRun)
            {
                return SkillResult.Ok("Dry run: would insert VBA module " + moduleName + (string.IsNullOrWhiteSpace(macroName) ? string.Empty : " and run " + macroName), JsonConvert.SerializeObject(new { moduleName = moduleName, macroName = macroName, code = tool.Code }));
            }

            var insert = new SkillCommand { SkillId = VbaToolId("insert_vba_module") };
            insert.Arguments["moduleName"] = moduleName;
            insert.Arguments["code"] = tool.Code;
            var insertResult = _adapter.ExecuteSkill(insert);
            if (!insertResult.Success ||
                string.IsNullOrWhiteSpace(macroName) ||
                (insertResult.Message ?? string.Empty).StartsWith("VBA insert was blocked", StringComparison.OrdinalIgnoreCase))
            {
                return insertResult;
            }

            var run = new SkillCommand { SkillId = VbaToolId("run_macro") };
            run.Arguments["macroName"] = macroName;
            var runResult = _adapter.ExecuteSkill(run);
            return SkillResult.Ok("VBA tool executed: " + tool.Id, JsonConvert.SerializeObject(new { insert = insertResult, run = runResult }));
        }

        private void BackupVbaModuleBeforeReplace(SkillCommand command, IReadOnlyList<SkillDefinition> tools, AppSettings settings)
        {
            var moduleName = GetArgument(command.Arguments, "moduleName", string.Empty);
            if (string.IsNullOrWhiteSpace(moduleName) || GetArgument(command.Arguments, "skipBackup", "false").Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var read = new SkillCommand { SkillId = VbaToolId("vba_read_module") };
            read.Arguments["moduleName"] = moduleName;
            read.Arguments["maxChars"] = Math.Max(settings.VbaContextCharLimit, 30000);
            var existing = _adapter.ExecuteSkill(read);
            if (!existing.Success || string.IsNullOrWhiteSpace(existing.DataJson))
            {
                return;
            }

            try
            {
                var data = JObject.Parse(existing.DataJson);
                var code = (string)data["code"];
                var componentType = (string)data["type"];
                if (code != null)
                {
                    _vbaBackupStore.Save(_adapter.HostName, _adapter.DocumentKey, _adapter.DocumentTitle, moduleName, componentType, code);
                }
            }
            catch (JsonException)
            {
            }
        }

        private SkillResult RestoreVbaBackup(SkillCommand command, IReadOnlyList<SkillDefinition> tools, AppSettings settings, bool dryRun, bool manualRun)
        {
            var backupId = GetArgument(command.Arguments, "backupId", string.Empty);
            var moduleName = GetArgument(command.Arguments, "moduleName", string.Empty);
            var backup = _vbaBackupStore.Find(_adapter.HostName, _adapter.DocumentKey, backupId, moduleName);
            if (backup == null)
            {
                return SkillResult.Fail("VBA backup not found.");
            }

            if (dryRun)
            {
                return SkillResult.Ok("Dry run: would restore VBA backup " + backup.BackupId, JsonConvert.SerializeObject(backup));
            }

            var replace = new SkillCommand { SkillId = VbaToolId("vba_replace_module") };
            replace.Arguments["moduleName"] = backup.ModuleName;
            replace.Arguments["code"] = backup.Code;
            replace.Arguments["createIfMissing"] = "true";
            var result = ExecuteCommand(replace, tools, settings, 0, false, manualRun);
            return result.Success
                ? SkillResult.Ok("VBA backup restored: " + backup.BackupId, JsonConvert.SerializeObject(new { backup = backup, restore = result }))
                : result;
        }

        private SkillResult ReplaceVbaText(SkillCommand command, IReadOnlyList<SkillDefinition> tools, AppSettings settings, bool dryRun, bool manualRun)
        {
            var moduleName = GetArgument(command.Arguments, "moduleName", string.Empty);
            var find = GetArgument(command.Arguments, "find", string.Empty);
            var replace = GetArgument(command.Arguments, "replace", string.Empty);
            if (string.IsNullOrWhiteSpace(moduleName) || string.IsNullOrEmpty(find))
            {
                return SkillResult.Fail("moduleName and find are required.");
            }

            string code;
            SkillResult error;
            if (!TryReadVbaModuleCode(moduleName, out code, out error))
            {
                return error;
            }

            var replacements = CountOccurrences(code, find);
            if (replacements == 0)
            {
                return SkillResult.Fail("Text fragment was not found in VBA module: " + moduleName);
            }

            var updated = code.Replace(find, replace ?? string.Empty);
            var preview = JsonConvert.SerializeObject(new
            {
                moduleName = moduleName,
                replacements = replacements,
                oldLength = code.Length,
                newLength = updated.Length
            });
            if (dryRun)
            {
                return SkillResult.Ok("Dry run: would patch VBA module " + moduleName + " (" + replacements + " replacement(s)).", preview);
            }

            var write = new SkillCommand { SkillId = VbaToolId("vba_replace_module") };
            write.Arguments["moduleName"] = moduleName;
            write.Arguments["code"] = updated;
            write.Arguments["createIfMissing"] = "true";
            var result = ExecuteCommand(write, tools, settings, 0, false, manualRun);
            return result.Success
                ? SkillResult.Ok("VBA text replaced in " + moduleName + ": " + replacements + " replacement(s).", preview)
                : result;
        }

        private SkillResult ApplyVbaPatch(SkillCommand command, IReadOnlyList<SkillDefinition> tools, AppSettings settings, bool dryRun, bool manualRun)
        {
            var moduleName = GetArgument(command.Arguments, "moduleName", string.Empty);
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                return SkillResult.Fail("moduleName is required.");
            }

            JArray operations;
            try
            {
                operations = ParsePatchOperations(GetArgument(command.Arguments, "patch", string.Empty));
            }
            catch (JsonException ex)
            {
                return SkillResult.Fail("Invalid patch JSON: " + ex.Message);
            }

            if (operations.Count == 0)
            {
                return SkillResult.Fail("Patch has no operations.");
            }

            string code;
            SkillResult error;
            if (!TryReadVbaModuleCode(moduleName, out code, out error))
            {
                return error;
            }

            var updated = code;
            var summary = new List<object>();
            foreach (JObject operation in operations.OfType<JObject>())
            {
                var result = ApplyPatchOperation(updated, operation, out updated);
                if (!result.Success)
                {
                    return result;
                }

                summary.Add(new { op = (string)(operation["op"] ?? operation["type"]), message = result.Message });
            }
            if (summary.Count != operations.Count)
            {
                return SkillResult.Fail("Each patch operation must be a JSON object.");
            }

            var preview = JsonConvert.SerializeObject(new
            {
                moduleName = moduleName,
                operations = summary,
                oldLength = code.Length,
                newLength = updated.Length
            });
            if (dryRun)
            {
                return SkillResult.Ok("Dry run: would apply VBA patch to " + moduleName + ".", preview);
            }

            var write = new SkillCommand { SkillId = VbaToolId("vba_replace_module") };
            write.Arguments["moduleName"] = moduleName;
            write.Arguments["code"] = updated;
            write.Arguments["createIfMissing"] = "true";
            var writeResult = ExecuteCommand(write, tools, settings, 0, false, manualRun);
            return writeResult.Success
                ? SkillResult.Ok("VBA patch applied to " + moduleName + ".", preview)
                : writeResult;
        }

        private bool TryReadVbaModuleCode(string moduleName, out string code, out SkillResult error)
        {
            code = string.Empty;
            error = null;
            var read = new SkillCommand { SkillId = VbaToolId("vba_read_module") };
            read.Arguments["moduleName"] = moduleName;
            read.Arguments["maxChars"] = 1000000;
            var current = _adapter.ExecuteSkill(read);
            if (!current.Success || string.IsNullOrWhiteSpace(current.DataJson))
            {
                error = current.Success ? SkillResult.Fail("VBA module returned no code.") : current;
                return false;
            }

            try
            {
                code = (string)JObject.Parse(current.DataJson)["code"] ?? string.Empty;
            }
            catch (JsonException ex)
            {
                error = SkillResult.Fail("Could not parse VBA module data: " + ex.Message);
                return false;
            }

            if (code.EndsWith("\n...[truncated]", StringComparison.Ordinal))
            {
                error = SkillResult.Fail("VBA module is too large for a safe patch.");
                return false;
            }

            return true;
        }

        private static object ResolvePipelineValue(JToken token, IDictionary<string, object> inputArgs, IDictionary<string, SkillResult> stepResults)
        {
            var value = token.Type == JTokenType.String
                ? token.Value<string>()
                : token.ToString(Formatting.None);

            return ReplacePlaceholders(value, inputArgs, stepResults);
        }

        private static Dictionary<string, object> ParseArguments(string argumentsJson)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(argumentsJson))
            {
                return result;
            }

            try
            {
                var args = JObject.Parse(argumentsJson);
                foreach (var property in args.Properties())
                {
                    result[property.Name] = property.Value.Type == JTokenType.String
                        ? (object)property.Value.Value<string>()
                        : property.Value.ToString(Formatting.None);
                }
            }
            catch (JsonException)
            {
            }

            return result;
        }

        private static JArray ParsePatchOperations(string patchJson)
        {
            if (string.IsNullOrWhiteSpace(patchJson))
            {
                return new JArray();
            }

            var token = JToken.Parse(patchJson);
            if (token.Type == JTokenType.Array)
            {
                return (JArray)token;
            }

            return new JArray(token);
        }

        private static SkillResult ApplyPatchOperation(string current, JObject operation, out string updated)
        {
            updated = current;
            var op = ((string)(operation["op"] ?? operation["type"]) ?? string.Empty).Trim();
            var find = (string)(operation["find"] ?? operation["anchor"]);
            var text = (string)(operation["text"] ?? operation["replace"] ?? operation["code"]) ?? string.Empty;
            switch (op.ToLowerInvariant())
            {
                case "replace":
                case "replaceall":
                    if (string.IsNullOrEmpty(find))
                    {
                        return SkillResult.Fail("Patch replace requires find.");
                    }

                    var count = CountOccurrences(current, find);
                    if (count == 0)
                    {
                        return SkillResult.Fail("Patch find text was not found.");
                    }

                    updated = current.Replace(find, text);
                    return SkillResult.Ok("Replaced " + count + " occurrence(s).");
                case "replacefirst":
                    return ReplaceAtMatch(current, find, text, out updated);
                case "insertbefore":
                    return ReplaceAtMatch(current, find, text + find, out updated);
                case "insertafter":
                    return ReplaceAtMatch(current, find, find + text, out updated);
                case "replacelines":
                    return ReplaceLines(current, operation, text, out updated);
                default:
                    return SkillResult.Fail("Unsupported patch op: " + op);
            }
        }

        private static SkillResult ReplaceAtMatch(string current, string find, string replacement, out string updated)
        {
            updated = current;
            if (string.IsNullOrEmpty(find))
            {
                return SkillResult.Fail("Patch operation requires find.");
            }

            var index = current.IndexOf(find, StringComparison.Ordinal);
            if (index < 0)
            {
                return SkillResult.Fail("Patch find text was not found.");
            }

            updated = current.Substring(0, index) + replacement + current.Substring(index + find.Length);
            return SkillResult.Ok("Patched first occurrence.");
        }

        private static SkillResult ReplaceLines(string current, JObject operation, string text, out string updated)
        {
            updated = current;
            var startLine = (int?)operation["startLine"] ?? 0;
            var deleteCount = (int?)operation["deleteCount"] ?? 0;
            if (startLine <= 0 || deleteCount < 0)
            {
                return SkillResult.Fail("replaceLines requires startLine >= 1 and deleteCount >= 0.");
            }

            var newline = current.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            var lines = current.Replace("\r\n", "\n").Split('\n').ToList();
            var index = startLine - 1;
            if (index > lines.Count)
            {
                return SkillResult.Fail("replaceLines startLine is outside the module.");
            }

            var remove = Math.Min(deleteCount, lines.Count - index);
            if (remove > 0)
            {
                lines.RemoveRange(index, remove);
            }

            if (!string.IsNullOrEmpty(text))
            {
                lines.InsertRange(index, text.Replace("\r\n", "\n").Split('\n'));
            }

            updated = string.Join(newline, lines.ToArray());
            return SkillResult.Ok("Replaced lines at " + startLine + " deleting " + deleteCount + ".");
        }

        private static string GetArgument(IDictionary<string, object> args, string name, string fallback)
        {
            object value;
            return args != null && args.TryGetValue(name, out value) && value != null
                ? Convert.ToString(value)
                : fallback;
        }

        private static string ToolModuleName(string toolId)
        {
            return "RNAssistant_" + Regex.Replace(toolId ?? "Tool", "[^A-Za-z0-9_]", "_");
        }

        private string VbaToolId(string suffix)
        {
            return HostToolPrefix() + "." + suffix;
        }

        private string HostToolPrefix()
        {
            return (_adapter.HostName ?? string.Empty).ToLowerInvariant();
        }

        private bool HostSupportsVba()
        {
            return string.Equals(_adapter.HostName, "Excel", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_adapter.HostName, "Word", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_adapter.HostName, "PowerPoint", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsVbaMutationTool(string toolId)
        {
            return EndsWithTool(toolId, ".vba_replace_module") ||
                EndsWithTool(toolId, ".vba_replace_text") ||
                EndsWithTool(toolId, ".vba_apply_patch") ||
                EndsWithTool(toolId, ".vba_restore_backup") ||
                EndsWithTool(toolId, ".insert_vba_module") ||
                EndsWithTool(toolId, ".run_macro");
        }

        private static bool EndsWithTool(string toolId, string suffix)
        {
            return (toolId ?? string.Empty).EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool CanRetryToolError(SkillResult result)
        {
            var message = result == null ? string.Empty : result.Message ?? string.Empty;
            return message.IndexOf("requires confirmation", StringComparison.OrdinalIgnoreCase) < 0 &&
                message.IndexOf("Auto tool execution is disabled", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static int CountOccurrences(string value, string find)
        {
            var count = 0;
            var index = 0;
            while ((index = value.IndexOf(find, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += find.Length;
            }

            return count;
        }

        private static string ReplacePlaceholders(string value, IDictionary<string, object> inputArgs, IDictionary<string, SkillResult> stepResults)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            return Regex.Replace(value, "\\{\\{\\s*([^}]+)\\s*\\}\\}", match =>
            {
                var key = match.Groups[1].Value.Trim();
                if (key.StartsWith("args.", StringComparison.OrdinalIgnoreCase))
                {
                    object arg;
                    return inputArgs != null && inputArgs.TryGetValue(key.Substring(5), out arg) && arg != null
                        ? Convert.ToString(arg)
                        : string.Empty;
                }

                if (key.StartsWith("steps.", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = key.Split('.');
                    SkillResult step;
                    if (parts.Length >= 3 && stepResults != null && stepResults.TryGetValue(parts[1], out step))
                    {
                        if (string.Equals(parts[2], "message", StringComparison.OrdinalIgnoreCase))
                        {
                            return step.Message ?? string.Empty;
                        }

                        if (string.Equals(parts[2], "dataJson", StringComparison.OrdinalIgnoreCase))
                        {
                            return step.DataJson ?? string.Empty;
                        }

                        if (string.Equals(parts[2], "success", StringComparison.OrdinalIgnoreCase))
                        {
                            return step.Success ? "true" : "false";
                        }
                    }
                }

                return match.Value;
            });
        }

        private static List<ChatMessage> BuildPromptMessages(string systemPrompt, IEnumerable<ChatMessage> sessionMessages, int charLimit)
        {
            var result = new List<ChatMessage> { new ChatMessage { Role = "system", Content = systemPrompt } };
            var remaining = Math.Max(4000, charLimit);
            foreach (var message in sessionMessages.Reverse())
            {
                if (string.IsNullOrEmpty(message.Content))
                {
                    continue;
                }

                remaining -= message.Content.Length;
                if (remaining < 0)
                {
                    break;
                }

                result.Insert(1, message);
            }

            return result;
        }

        private static object CreateContextUsage(IEnumerable<ChatMessage> promptMessages, AppSettings settings)
        {
            var limit = Math.Max(4000, settings == null ? 24000 : settings.ContextCharLimit);
            var used = 0;
            var count = 0;
            if (promptMessages != null)
            {
                foreach (var message in promptMessages)
                {
                    if (message == null)
                    {
                        continue;
                    }

                    used += (message.Content ?? string.Empty).Length;
                    count += 1;
                }
            }

            return new
            {
                usedChars = used,
                limitChars = limit,
                percent = limit <= 0 ? 0 : Math.Min(100, (int)Math.Round(used * 100.0 / limit)),
                messageCount = count,
                actual = true
            };
        }

        private static object EstimateContextUsage(ChatSession session, AppSettings settings)
        {
            var limit = Math.Max(4000, settings == null ? 24000 : settings.ContextCharLimit);
            var used = 0;
            var count = 0;
            if (session != null && session.Messages != null)
            {
                foreach (var message in session.Messages)
                {
                    if (message == null)
                    {
                        continue;
                    }

                    used += (message.Content ?? string.Empty).Length;
                    count += 1;
                }
            }

            return new
            {
                usedChars = used,
                limitChars = limit,
                percent = limit <= 0 ? 0 : Math.Min(100, (int)Math.Round(used * 100.0 / limit)),
                messageCount = count,
                actual = false
            };
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
