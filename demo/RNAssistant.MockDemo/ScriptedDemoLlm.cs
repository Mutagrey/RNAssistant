using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

namespace RNAssistant.MockDemo
{
    internal sealed class ScriptedDemoLlm
    {
        public static readonly string[] ModelIds = { "mock-strict", "mock-glm5", "mock-qwen80b", "mock-deepseek" };
        private readonly string _host;

        public ScriptedDemoLlm(string host)
        {
            _host = string.IsNullOrWhiteSpace(host) ? "Excel" : host;
        }

        public Task<LlmCompletionResult> CompleteAsync(AppSettings settings, IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var model = NormalizeModel(settings == null ? null : settings.Model);
            var messageList = (messages ?? new ChatMessage[0]).Where(message => message != null).ToList();
            var requestIndex = CurrentUserRequestIndex(messageList);
            var lastUser = LastUserMessage(messages);
            var userRequest = requestIndex < 0
                ? string.Empty
                : ExtractUserRequest(messageList[requestIndex].Content);
            var toolResults = ReadToolResults(messageList, requestIndex + 1);
            var isHtmlTask = LooksLikeHtmlTask(userRequest);
            var isHtmlEdit = LooksLikeHtmlEdit(userRequest);
            var repair = IsRepairRequest(lastUser);
            string content;

            if (!isHtmlTask && !LooksLikeOfficeAction(userRequest))
            {
                content = FinalBlock(ProseAnswer(userRequest));
            }
            else
            {
                var commands = (isHtmlTask
                    ? HtmlCommands(isHtmlEdit, false)
                    : InitialCommands(_host, false)).ToList();
                var next = NextPendingCommand(commands, toolResults);
                var firstTurn = toolResults.Count == 0;

                if (!repair && firstTurn && string.Equals(model, "mock-glm5", StringComparison.OrdinalIgnoreCase))
                {
                    content = AgentBlock(isHtmlTask
                        ? HtmlCommands(isHtmlEdit, true).First()
                        : InitialCommands(_host, true).First());
                }
                else if (!repair && firstTurn && string.Equals(model, "mock-qwen80b", StringComparison.OrdinalIgnoreCase))
                {
                    content = "Понял задачу. Выполню следующее локальное действие.\n\n" +
                        AgentBlock(next) +
                        "\nПосле выполнения проверю результат.";
                }
                else if (!repair && firstTurn && string.Equals(model, "mock-deepseek", StringComparison.OrdinalIgnoreCase))
                {
                    content = "{\"protocolVersion\":1,\"kind\":\"tool\",\"decisionSummary\":\"Начинаю действие\",\"goal\":null,\"plan\":null,\"tool\":{";
                }
                else if (next != null)
                {
                    content = AgentBlock(next);
                }
                else
                {
                    content = FinalBlock(isHtmlTask ? HtmlFinalAnswer(isHtmlEdit) : FinalAnswer(_host));
                }
            }

            return Task.FromResult(new LlmCompletionResult
            {
                Content = content,
                PromptTokens = 1200,
                CompletionTokens = Math.Max(1, content.Length / 4),
                TotalTokens = 1200 + Math.Max(1, content.Length / 4),
                UsageJson = "{\"mock\":true,\"model\":\"" + JsonEscape(model) + "\"}"
            });
        }

        public static string CatalogJson()
        {
            return JsonConvert.SerializeObject(new
            {
                default_model = "mock-strict",
                models = new[]
                {
                    Model("mock-strict", "Mock strict", "Clean RNAssistant JSON for the happy path."),
                    Model("mock-glm5", "Mock GLM 5.0", "Starts with a wrong tool id, then follows retry guidance."),
                    Model("mock-qwen80b", "Mock Qwen 80B", "Adds prose around a valid planner JSON object."),
                    Model("mock-deepseek", "Mock DeepSeek", "Starts with malformed tool JSON, then repairs it.")
                }
            });
        }

        private static object Model(string value, string title, string description)
        {
            return new
            {
                value = value,
                title = title,
                description = description,
                max_context_tokens = 32000,
                max_tokens = 2048,
                temperature = 0.2,
                top_p = 1.0
            };
        }

        private static IEnumerable<DemoCommand> InitialCommands(string host, bool weakWrongTool)
        {
            if (weakWrongTool)
            {
                if (IsHost(host, "Word"))
                {
                    return new[] { Cmd("word.write_text", "text", "Mock summary") };
                }

                if (IsHost(host, "PowerPoint"))
                {
                    return new[] { Cmd("powerpoint.create_slide", "title", "Mock summary") };
                }

                if (IsHost(host, "Outlook"))
                {
                    return new[] { Cmd("outlook.reply", "body", "Спасибо, вернусь с деталями сегодня.") };
                }

                return new[] { Cmd("excel.create_sheet", "name", "Demo Report") };
            }

            if (IsHost(host, "Word"))
            {
                return new[]
                {
                    Cmd("word.write_text", "mode", "insert", "text", "\n\nRNAssistant mock summary: revenue is up, but retention requires follow-up.")
                };
            }

            if (IsHost(host, "PowerPoint"))
            {
                return new[]
                {
                    Cmd("powerpoint.add_slide", "title", "Mock Action Plan", "body", "1. Confirm renewal risks\n2. Prepare retention actions\n3. Review forecast")
                };
            }

            if (IsHost(host, "Outlook"))
            {
                return new[]
                {
                    Cmd("outlook.create_draft", "kind", "reply", "body", "Спасибо за письмо. Предлагаю сегодня согласовать следующие шаги и срок обновленного коммерческого предложения.")
                };
            }

            return new[]
            {
                Cmd("excel.add_sheet", "name", "Demo Report"),
                Cmd("excel.write_range", "kind", "table", "sheet", "Demo Report", "address", "A1", "values", new[]
                {
                    new[] { "Month", "Sales" },
                    new[] { "Jan", "120" },
                    new[] { "Feb", "150" },
                    new[] { "Mar", "180" }
                }),
                Cmd("excel.upsert_chart", "sheet", "Demo Report", "sourceRange", "A1:B4", "chartType", "line", "title", "Demo Sales")
            };
        }

        private static IEnumerable<DemoCommand> HtmlCommands(bool editExisting, bool weakWrongTool)
        {
            if (weakWrongTool)
            {
                return new[]
                {
                    Cmd("common.html_workspace_save_file", "path", "index.html", "kind", "html", "content", "<h1>Broken weak-model alias</h1>", "setActive", true)
                };
            }

            if (editExisting)
            {
                return new[]
                {
                    Cmd("common.html_workspace_read"),
                    Cmd("common.html_workspace_upsert", "resourceType", "data", "name", "sales", "content", "{\"rows\":[{\"month\":\"Jan\",\"sales\":120},{\"month\":\"Feb\",\"sales\":150},{\"month\":\"Mar\",\"sales\":180}],\"title\":\"Updated Sales HTML Dashboard\"}"),
                    Cmd("common.html_workspace_upsert", "resourceType", "file", "name", "app.js", "content", "(function(){var data=window.RNAssistantData.sales||{};var rows=data.rows||[];var total=rows.reduce(function(sum,row){return sum+Number(row.sales||0);},0);document.getElementById('salesTitle').textContent=data.title||'Sales HTML Dashboard';document.getElementById('salesTotal').textContent='Total: '+total;var list=document.getElementById('salesRows');if(list){list.innerHTML=rows.map(function(row){return '<article class=\"row-card\"><strong>'+row.month+'</strong><span>'+row.sales+'</span></article>';}).join('');}document.body.setAttribute('data-script-ready','updated');}());", "setActive", false)
                };
            }

            return new[]
            {
                Cmd("common.html_workspace_upsert", "resourceType", "data", "name", "sales", "content", "{\"rows\":[{\"month\":\"Jan\",\"sales\":120},{\"month\":\"Feb\",\"sales\":150}],\"title\":\"Sales HTML Dashboard\"}"),
                Cmd("common.html_workspace_upsert", "resourceType", "file", "name", "styles.css", "content", "body{font-family:Segoe UI,Arial,sans-serif;margin:0;min-height:100vh;background:#f8fafc;color:#111827}.dashboard{min-height:100vh;padding:32px clamp(20px,4vw,56px);display:grid;align-content:start;gap:22px}.hero{display:flex;justify-content:space-between;gap:18px;align-items:flex-end;border-bottom:1px solid #d0d5dd;padding-bottom:18px}.hero h1{margin:0;font-size:clamp(28px,4vw,48px);font-weight:500}.hero p{margin:8px 0 0;color:#475467}.metric{margin:0;font-size:clamp(32px,5vw,56px);font-weight:500;color:#0f766e}.rows{display:grid;grid-template-columns:repeat(auto-fit,minmax(160px,1fr));gap:12px}.row-card{display:flex;justify-content:space-between;gap:12px;border:1px solid #d0d5dd;border-radius:8px;padding:14px;background:#fff}"),
                Cmd("common.html_workspace_upsert", "resourceType", "file", "name", "app.js", "content", "(function(){var data=window.RNAssistantData.sales||{};var rows=data.rows||[];var total=rows.reduce(function(sum,row){return sum+Number(row.sales||0);},0);document.getElementById('salesTitle').textContent=data.title||'Sales HTML Dashboard';document.getElementById('salesTotal').textContent='Total: '+total;var list=document.getElementById('salesRows');if(list){list.innerHTML=rows.map(function(row){return '<article class=\"row-card\"><strong>'+row.month+'</strong><span>'+row.sales+'</span></article>';}).join('');}document.body.setAttribute('data-script-ready','created');}());", "setActive", false),
                Cmd("common.html_workspace_upsert", "resourceType", "file", "name", "index.html", "content", "<!doctype html><html><head><meta charset=\"utf-8\"><title>Sales HTML Dashboard</title></head><body><main class=\"dashboard\"><section class=\"hero\"><div><h1 id=\"salesTitle\">Sales HTML Dashboard</h1><p>Data comes from RNAssistantData.sales</p></div><p id=\"salesTotal\" class=\"metric\">Total: 0</p></section><section id=\"salesRows\" class=\"rows\"></section></main></body></html>", "setActive", true)
            };
        }

        private static string FinalAnswer(string host)
        {
            if (IsHost(host, "Word"))
            {
                return "Готово: добавил mock summary в документ и проверил текст через `word.read_text`.";
            }

            if (IsHost(host, "PowerPoint"))
            {
                return "Готово: добавил mock slide и проверил deck через `powerpoint.read_slides`.";
            }

            if (IsHost(host, "Outlook"))
            {
                return "Готово: подготовил mock reply и проверил выбранное письмо через `outlook.read_mail`.";
            }

            return "Готово: создал лист `Demo Report`, записал таблицу продаж, добавил график `Demo Sales` и проверил диапазон/список графиков.";
        }

        private static string ProseAnswer(string userText)
        {
            if (Contains(userText, "EBITDA"))
            {
                return "EBITDA - это прибыль бизнеса до процентов по кредитам, налогов, амортизации и износа. Простыми словами: показатель помогает понять, сколько компания зарабатывает на основной деятельности до влияния кредитов, налоговой структуры и бухгалтерской амортизации.";
            }

            return "Это обычный mock-ответ без вызова Office tools. В демо agent tools запускаются только когда запрос похож на действие с документом.";
        }

        private static string HtmlFinalAnswer(bool editExisting)
        {
            return editExisting
                ? "Готово: прочитал HTML workspace, обновил `sales` data source и `app.js`; результат доступен во вкладке HTML."
                : "Готово: создал HTML workspace с `index.html`, `styles.css`, `app.js` и data source `sales`; результат доступен во вкладке HTML.";
        }

        private static string AgentBlock(DemoCommand command)
        {
            command = command ?? new DemoCommand { ToolId = "missing.tool" };
            return JsonConvert.SerializeObject(new
            {
                message = "Выполняю следующий шаг: " + command.ToolId + ".",
                tool_calls = new[]
                {
                    new
                    {
                        id = CallId(command),
                        name = command.ToolId,
                        arguments = command.Arguments
                    }
                }
            });
        }

        private static string FinalBlock(string message)
        {
            return JsonConvert.SerializeObject(new
            {
                message = message ?? string.Empty,
                tool_calls = new object[0]
            });
        }

        private static DemoCommand NextPendingCommand(IEnumerable<DemoCommand> commands, IEnumerable<JObject> toolResults)
        {
            return (commands ?? new DemoCommand[0]).FirstOrDefault(command => !CommandWasObserved(command, toolResults));
        }

        private static bool CommandWasObserved(DemoCommand command, IEnumerable<JObject> toolResults)
        {
            if (command == null || string.IsNullOrWhiteSpace(command.ToolId))
            {
                return false;
            }

            var callId = CallId(command);
            foreach (var result in toolResults ?? new JObject[0])
            {
                if (result != null &&
                    (bool?)result["ok"] == true &&
                    string.Equals((string)result["name"], command.ToolId, StringComparison.Ordinal) &&
                    string.Equals((string)result["tool_call_id"], callId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static DemoCommand Cmd(string toolId, params object[] keyValues)
        {
            var command = new DemoCommand { ToolId = toolId };
            for (var i = 0; i + 1 < (keyValues == null ? 0 : keyValues.Length); i += 2)
            {
                command.Arguments[Convert.ToString(keyValues[i])] = keyValues[i + 1];
            }

            return command;
        }

        private static int CurrentUserRequestIndex(IReadOnlyList<ChatMessage> messages)
        {
            for (var index = (messages == null ? 0 : messages.Count) - 1; index >= 0; index--)
            {
                var message = messages[index];
                if (message != null && !message.ProtocolMessage &&
                    string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }
            return -1;
        }

        private static string LastUserMessage(IEnumerable<ChatMessage> messages)
        {
            var last = (messages ?? new ChatMessage[0])
                .Where(m => m != null && string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
                .LastOrDefault();
            return last == null ? string.Empty : last.Content ?? string.Empty;
        }

        private static string ExtractUserRequest(string text)
        {
            var value = text ?? string.Empty;
            var marker = "USER_REQUEST:";
            var start = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return LastNonEmptyLine(value);
            }

            start += marker.Length;
            return value.Substring(start).Trim();
        }

        private static List<JObject> ReadToolResults(IReadOnlyList<ChatMessage> messages, int startIndex)
        {
            var results = new List<JObject>();
            for (var index = Math.Max(0, startIndex); messages != null && index < messages.Count; index++)
            {
                var message = messages[index];
                var content = message == null ? string.Empty : message.Content ?? string.Empty;
                const string marker = "TOOL_RESULT:";
                if (content.StartsWith(marker, StringComparison.Ordinal))
                {
                    content = content.Substring(marker.Length).Trim();
                }
                else if (message == null || !string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    results.Add(JObject.Parse(content));
                }
                catch (JsonException)
                {
                }
            }
            return results;
        }

        private static bool IsRepairRequest(string text)
        {
            return Contains(text, "FORMAT_REPAIR:");
        }

        private static string CallId(DemoCommand command)
        {
            var seed = (command == null ? string.Empty : command.ToolId ?? string.Empty) + "|" +
                JsonConvert.SerializeObject(command == null ? null : command.Arguments, Formatting.None);
            unchecked
            {
                uint hash = 2166136261;
                foreach (var character in seed)
                {
                    hash ^= character;
                    hash *= 16777619;
                }
                return "call_" + Regex.Replace(command == null ? "tool" : command.ToolId ?? "tool", "[^A-Za-z0-9_-]", "_") +
                    "_" + hash.ToString("x8");
            }
        }

        private static string LastNonEmptyLine(string value)
        {
            var lines = (value ?? string.Empty).Split('\n');
            for (var index = lines.Length - 1; index >= 0; index--)
            {
                if (!string.IsNullOrWhiteSpace(lines[index]))
                {
                    return lines[index].Trim();
                }
            }
            return string.Empty;
        }

        private static bool LooksLikeOfficeAction(string text)
        {
            var value = (text ?? string.Empty).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var action = Regex.IsMatch(value, "(создай|создать|сделай|построй|сгенерируй|заполни|вставь|замени|измени|добавь|нарисуй|create|make|add|insert|replace|update|write|generate|build|chart)");
            if (!action)
            {
                return false;
            }

            return Regex.IsMatch(value, "(лист|таблиц|диапазон|ячейк|график|диаграмм|sheet|table|range|cell|chart|slide|слайд|document|документ|selection|выдел|mail|email|письм|отчет|report)");
        }

        private static bool LooksLikeHtmlTask(string text)
        {
            var value = (text ?? string.Empty).ToLowerInvariant();
            return Regex.IsMatch(value, "(html|веб|web|page|страниц|dashboard|лендинг|script|скрипт|js)");
        }

        private static bool LooksLikeHtmlEdit(string text)
        {
            var value = (text ?? string.Empty).ToLowerInvariant();
            return Regex.IsMatch(value, "(измени|изменить|обнови|обновить|поменяй|replace|update|edit|change)") &&
                LooksLikeHtmlTask(value);
        }

        private static bool Contains(string value, string expected)
        {
            return (value ?? string.Empty).IndexOf(expected ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsHost(string host, string expected)
        {
            return string.Equals(host, expected, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeModel(string model)
        {
            return string.IsNullOrWhiteSpace(model) ? "mock-strict" : model.Trim();
        }

        private static string JsonEscape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private sealed class DemoCommand
        {
            public string ToolId { get; set; }
            public Dictionary<string, object> Arguments { get; private set; }

            public DemoCommand()
            {
                Arguments = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
