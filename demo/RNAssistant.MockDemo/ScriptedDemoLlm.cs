using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
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
            var prompt = Flatten(messages);
            var lastUser = LastUserMessage(messages);
            var isMetaPrompt = IsAgentMetaPrompt(lastUser);
            var htmlModeEnabled = Contains(prompt, "HTML MODE IS ENABLED");
            var isHtmlTask = htmlModeEnabled || (isMetaPrompt ? LooksLikeHtmlConversation(prompt) : LooksLikeHtmlTask(lastUser));
            var isHtmlEdit = isMetaPrompt ? LooksLikeHtmlEditConversation(prompt) : LooksLikeHtmlEdit(lastUser);
            string content;

            if (Contains(lastUser, "A local tool call failed") ||
                Contains(lastUser, "Unknown tool id") ||
                Contains(lastUser, "Use only these exact available tool ids"))
            {
                content = AgentBlock(isHtmlTask ? HtmlCommands(isHtmlEdit, false) : InitialCommands(_host, false));
            }
            else if (Contains(lastUser, "could not recover executable JSON") ||
                Contains(lastUser, "ForceToolUsePrompt") ||
                Contains(lastUser, "prose-only answer is not acceptable") ||
                Contains(lastUser, "Return only one ```rnassistant-agent"))
            {
                content = AgentBlock(isHtmlTask ? HtmlCommands(isHtmlEdit, false) : InitialCommands(_host, false));
            }
            else if (Contains(lastUser, "verify the result") ||
                Contains(lastUser, "Before the final answer, verify"))
            {
                content = AgentBlock(VerificationCommands(_host));
            }
            else if (Contains(lastUser, "If the task is complete, answer the user normally"))
            {
                content = isHtmlTask ? HtmlFinalAnswer(isHtmlEdit) : FinalAnswer(_host);
            }
            else if (isHtmlTask)
            {
                if (string.Equals(model, "mock-glm5", StringComparison.OrdinalIgnoreCase))
                {
                    content = AgentBlock(HtmlCommands(isHtmlEdit, true));
                }
                else if (string.Equals(model, "mock-qwen80b", StringComparison.OrdinalIgnoreCase))
                {
                    content = "Понял HTML задачу. Обновлю workspace через локальные tools.\n\n" +
                        AgentBlock(HtmlCommands(isHtmlEdit, false)) +
                        "\nПосле выполнения можно открыть вкладку HTML.";
                }
                else if (string.Equals(model, "mock-deepseek", StringComparison.OrdinalIgnoreCase))
                {
                    content = "```rnassistant-agent\n{steps:[{toolId:'common.html_workspace_save_file', arguments:{path:'../bad.html'}}\n```\nЧерновик плана.";
                }
                else
                {
                    content = AgentBlock(HtmlCommands(isHtmlEdit, false));
                }
            }
            else if (!LooksLikeOfficeAction(lastUser))
            {
                content = ProseAnswer(lastUser);
            }
            else if (string.Equals(model, "mock-glm5", StringComparison.OrdinalIgnoreCase))
            {
                content = AgentBlock(InitialCommands(_host, true));
            }
            else if (string.Equals(model, "mock-qwen80b", StringComparison.OrdinalIgnoreCase))
            {
                content = "Понял задачу. Сначала выполню локальные действия, затем проверю результат.\n\n" +
                    AgentBlock(InitialCommands(_host, false)) +
                    "\nПосле выполнения нужна проверка.";
            }
            else if (string.Equals(model, "mock-deepseek", StringComparison.OrdinalIgnoreCase))
            {
                content = "```rnassistant-agent\n{steps:[{toolId:'" + FirstToolId(_host) + "', arguments:{}}\n```\nЯ попробовал составить план.";
            }
            else
            {
                content = AgentBlock(InitialCommands(_host, false));
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
                    Model("mock-qwen80b", "Mock Qwen 80B", "Adds prose around a valid fenced JSON block."),
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
                    Cmd("word.insert_text", "text", "\n\nRNAssistant mock summary: revenue is up, but retention requires follow-up.")
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
                    Cmd("outlook.draft_reply", "body", "Спасибо за письмо. Предлагаю сегодня согласовать следующие шаги и срок обновленного коммерческого предложения.")
                };
            }

            return new[]
            {
                Cmd("excel.add_sheet", "name", "Demo Report"),
                Cmd("excel.write_table", "sheet", "Demo Report", "startAddress", "A1", "values", new[]
                {
                    new[] { "Month", "Sales" },
                    new[] { "Jan", "120" },
                    new[] { "Feb", "150" },
                    new[] { "Mar", "180" }
                }),
                Cmd("excel.add_chart", "sheet", "Demo Report", "sourceRange", "A1:B4", "chartType", "line", "title", "Demo Sales")
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
                    Cmd("common.html_workspace_upsert_data", "name", "sales", "json", "{\"rows\":[{\"month\":\"Jan\",\"sales\":120},{\"month\":\"Feb\",\"sales\":150},{\"month\":\"Mar\",\"sales\":180}],\"title\":\"Updated Sales HTML Dashboard\"}"),
                    Cmd("common.html_workspace_upsert_file", "path", "app.js", "kind", "script", "content", "(function(){var data=window.RNAssistantData.sales||{};var rows=data.rows||[];var total=rows.reduce(function(sum,row){return sum+Number(row.sales||0);},0);document.getElementById('salesTitle').textContent=data.title||'Sales HTML Dashboard';document.getElementById('salesTotal').textContent='Total: '+total;document.body.setAttribute('data-script-ready','updated');}());", "setActive", false)
                };
            }

            return new[]
            {
                Cmd("common.html_workspace_upsert_data", "name", "sales", "json", "{\"rows\":[{\"month\":\"Jan\",\"sales\":120},{\"month\":\"Feb\",\"sales\":150}],\"title\":\"Sales HTML Dashboard\"}"),
                Cmd("common.html_workspace_upsert_file", "path", "styles.css", "kind", "css", "content", "body{font-family:Segoe UI,Arial,sans-serif;margin:0;padding:24px;background:#f8fafc;color:#111827}.card{max-width:560px;border:1px solid #d0d5dd;border-radius:8px;padding:18px;background:#fff}.metric{font-size:28px;font-weight:700;color:#0f766e}"),
                Cmd("common.html_workspace_upsert_file", "path", "app.js", "kind", "script", "content", "(function(){var data=window.RNAssistantData.sales||{};var rows=data.rows||[];var total=rows.reduce(function(sum,row){return sum+Number(row.sales||0);},0);document.getElementById('salesTitle').textContent=data.title||'Sales HTML Dashboard';document.getElementById('salesTotal').textContent='Total: '+total;document.body.setAttribute('data-script-ready','created');}());", "setActive", false),
                Cmd("common.html_workspace_upsert_file", "path", "index.html", "kind", "html", "content", "<!doctype html><html><head><meta charset=\"utf-8\"><title>Sales HTML Dashboard</title></head><body><main class=\"card\"><h1 id=\"salesTitle\">Sales HTML Dashboard</h1><p id=\"salesTotal\" class=\"metric\">Total: 0</p><div id=\"salesRows\">Data comes from RNAssistantData.sales</div></main></body></html>", "setActive", true)
            };
        }

        private static IEnumerable<DemoCommand> VerificationCommands(string host)
        {
            if (IsHost(host, "Word"))
            {
                return new[] { Cmd("word.read_document") };
            }

            if (IsHost(host, "PowerPoint"))
            {
                return new[] { Cmd("powerpoint.read_slides") };
            }

            if (IsHost(host, "Outlook"))
            {
                return new[] { Cmd("outlook.read_selection") };
            }

            return new[]
            {
                Cmd("excel.read_range", "sheet", "Demo Report", "range", "A1:B4"),
                Cmd("excel.list_charts")
            };
        }

        private static string FirstToolId(string host)
        {
            return InitialCommands(host, false).First().ToolId;
        }

        private static string FinalAnswer(string host)
        {
            if (IsHost(host, "Word"))
            {
                return "Готово: добавил mock summary в документ и проверил текст через `word.read_document`.";
            }

            if (IsHost(host, "PowerPoint"))
            {
                return "Готово: добавил mock slide и проверил deck через `powerpoint.read_slides`.";
            }

            if (IsHost(host, "Outlook"))
            {
                return "Готово: подготовил mock reply и проверил выбранное письмо через `outlook.read_selection`.";
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

        private static string AgentBlock(IEnumerable<DemoCommand> commands)
        {
            return "```rnassistant-agent\n" +
                JsonConvert.SerializeObject(new
                {
                    description = "mock demo plan",
                    steps = (commands ?? new DemoCommand[0]).Select(command => new
                    {
                        toolId = command.ToolId,
                        arguments = command.Arguments
                    }).ToArray()
                }) +
                "\n```";
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

        private static bool HasVerificationResult(string prompt, string host)
        {
            if (IsHost(host, "Word"))
            {
                return Contains(prompt, "word.read_document");
            }

            if (IsHost(host, "PowerPoint"))
            {
                return Contains(prompt, "powerpoint.read_slides");
            }

            if (IsHost(host, "Outlook"))
            {
                return Contains(prompt, "outlook.read_selection") && Contains(prompt, "drafted reply");
            }

            return Contains(prompt, "excel.read_range") && Contains(prompt, "excel.list_charts");
        }

        private static string Flatten(IEnumerable<ChatMessage> messages)
        {
            return string.Join("\n", (messages ?? new ChatMessage[0]).Select(m => (m == null ? string.Empty : m.Role + "\n" + m.Content)).ToArray());
        }

        private static string LastUserMessage(IEnumerable<ChatMessage> messages)
        {
            var last = (messages ?? new ChatMessage[0])
                .Where(m => m != null && string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
                .LastOrDefault();
            return last == null ? string.Empty : last.Content ?? string.Empty;
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

        private static bool IsAgentMetaPrompt(string text)
        {
            return Contains(text, "A local tool call failed") ||
                Contains(text, "Unknown tool id") ||
                Contains(text, "Use only these exact available tool ids") ||
                Contains(text, "could not recover executable JSON") ||
                Contains(text, "ForceToolUsePrompt") ||
                Contains(text, "prose-only answer is not acceptable") ||
                Contains(text, "Return only one ```rnassistant-agent") ||
                Contains(text, "verify the result") ||
                Contains(text, "Before the final answer, verify") ||
                Contains(text, "If the task is complete, answer the user normally");
        }

        private static bool LooksLikeHtmlConversation(string text)
        {
            var value = (text ?? string.Empty).ToLowerInvariant();
            return Contains(value, "sales html dashboard") ||
                Contains(value, "data-script-ready") ||
                ConversationHasHtmlUserRequest(text, false);
        }

        private static bool LooksLikeHtmlEditConversation(string text)
        {
            var value = (text ?? string.Empty).ToLowerInvariant();
            return Contains(value, "updated sales html dashboard") ||
                ConversationHasHtmlUserRequest(text, true);
        }

        private static bool ConversationHasHtmlUserRequest(string text, bool requireEdit)
        {
            var lines = (text ?? string.Empty).Split('\n');
            for (var i = 0; i + 1 < lines.Length; i++)
            {
                if (!string.Equals((lines[i] ?? string.Empty).Trim(), "user", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var content = lines[i + 1] ?? string.Empty;
                if (IsAgentMetaPrompt(content))
                {
                    continue;
                }
                if (requireEdit ? LooksLikeHtmlEdit(content) : LooksLikeHtmlTask(content))
                {
                    return true;
                }
            }

            return false;
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
