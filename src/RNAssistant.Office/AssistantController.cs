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
        private readonly LlmClient _llmClient;
        private readonly PromptComposer _promptComposer;
        private readonly SkillCommandParser _commandParser;
        private readonly object _syncRoot;
        private string _queuedQuickAction;

        public AssistantController(IOfficeApplicationAdapter adapter)
        {
            _adapter = adapter;
            _paths = AppDataPaths.CreateDefault();
            _settingsService = new SettingsService(_paths);
            _chatStore = new ChatStore(_paths);
            _contextStore = new ContextStore(_paths);
            _toolStore = new ToolStore(_paths);
            _llmClient = new LlmClient(() => _settingsService.LoadApiKey());
            _promptComposer = new PromptComposer();
            _commandParser = new SkillCommandParser();
            _syncRoot = new object();
        }

        public string HostName { get { return _adapter.HostName; } }

        public string InitializeJson()
        {
            var session = LoadSession();
            var state = new
            {
                host = _adapter.HostName,
                documentKey = _adapter.DocumentKey,
                title = _adapter.DocumentTitle,
                settings = _settingsService.Load(),
                hasApiKey = !string.IsNullOrWhiteSpace(_settingsService.LoadApiKey()),
                tools = GetVisibleTools(),
                toolsPath = _paths.ToolsDirectory,
                context = LoadContext(),
                messages = session.Messages,
                quickAction = DequeueQuickAction()
            };
            return JsonConvert.SerializeObject(state);
        }

        public async Task<string> SendChatAsync(string text, Action<string, string> progress = null)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return JsonConvert.SerializeObject(new { message = string.Empty, skillResults = new SkillResult[0], messages = LoadSession().Messages });
            }

            ReportProgress(progress, "context", "Читаю документ...");
            var settings = _settingsService.Load();
            var session = LoadSession();
            session.Messages.Add(new ChatMessage { Role = "user", Content = text });

            var tools = GetVisibleTools().Where(s => s.Enabled).ToList();
            var systemPrompt = _promptComposer.ComposeSystemPrompt(
                settings,
                _adapter.HostName,
                _adapter.GetDocumentSnapshot(settings.ContextCharLimit),
                tools);

            var messages = BuildPromptMessages(systemPrompt, session.Messages, settings.ContextCharLimit);
            ReportProgress(progress, "thinking", "Модель думает...");
            var assistantText = await _llmClient.CompleteAsync(settings, messages);
            var assistantMessage = new ChatMessage { Role = "assistant", Content = assistantText };
            session.Messages.Add(assistantMessage);

            ReportProgress(progress, "processing", "Разбираю ответ...");
            var commands = _commandParser.Parse(assistantText).ToList();
            var results = new List<SkillResult>();
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
                results.Add(result);
                session.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = "Local skill result for `" + command.SkillId + "`: " + result.Message + (string.IsNullOrWhiteSpace(result.DataJson) ? string.Empty : "\n```json\n" + result.DataJson + "\n```")
                });
            }

            ReportProgress(progress, "saving", "Сохраняю историю...");
            _chatStore.Save(session);
            return JsonConvert.SerializeObject(new { message = assistantText, skillResults = results, messages = session.Messages });
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

        public string GetContextJson()
        {
            return JsonConvert.SerializeObject(LoadContext());
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

        private ChatSession LoadSession()
        {
            return _chatStore.LoadOrCreate(_adapter.HostName, _adapter.DocumentKey, _adapter.DocumentTitle);
        }

        private DocumentContext LoadContext()
        {
            return _contextStore.LoadOrCreate(_adapter.HostName, _adapter.DocumentKey, _adapter.DocumentTitle);
        }

        private List<SkillDefinition> GetVisibleTools()
        {
            var result = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var skill in _adapter.GetBuiltInSkills() ?? new SkillDefinition[0])
            {
                result[skill.Id] = skill;
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

            if (dryRun)
            {
                return SkillResult.Ok("Dry run: would execute " + command.SkillId, JsonConvert.SerializeObject(command.Arguments));
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

            var moduleName = GetArgument(command.Arguments, "moduleName", ToolModuleName(tool.Id));
            var macroName = GetArgument(command.Arguments, "macroName", string.Empty);
            if (dryRun)
            {
                return SkillResult.Ok("Dry run: would insert VBA module " + moduleName + (string.IsNullOrWhiteSpace(macroName) ? string.Empty : " and run " + macroName), JsonConvert.SerializeObject(new { moduleName = moduleName, macroName = macroName, code = tool.Code }));
            }

            var insert = new SkillCommand { SkillId = "excel.insert_vba_module" };
            insert.Arguments["moduleName"] = moduleName;
            insert.Arguments["code"] = tool.Code;
            var insertResult = _adapter.ExecuteSkill(insert);
            if (!insertResult.Success ||
                string.IsNullOrWhiteSpace(macroName) ||
                (insertResult.Message ?? string.Empty).StartsWith("VBA insert was blocked", StringComparison.OrdinalIgnoreCase))
            {
                return insertResult;
            }

            var run = new SkillCommand { SkillId = "excel.run_macro" };
            run.Arguments["macroName"] = macroName;
            var runResult = _adapter.ExecuteSkill(run);
            return SkillResult.Ok("VBA tool executed: " + tool.Id, JsonConvert.SerializeObject(new { insert = insertResult, run = runResult }));
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

        private static void ReportProgress(Action<string, string> progress, string phase, string message)
        {
            if (progress != null)
            {
                progress(phase, message);
            }
        }
    }
}
