using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Services
{
    internal static class AgentRunPresentation
    {
        private const int MaxRejectedResponseChars = 12000;

        public static void RecordRecoveredPlannerResponses(
            ChatSession session,
            IEnumerable<AgentPlannerRejectedResponse> rejectedResponses)
        {
            if (session == null) return;
            foreach (var rejected in rejectedResponses ?? new AgentPlannerRejectedResponse[0])
            {
                if (rejected == null) continue;
                var activity = rejected.Activity ?? CreatePlannerRecoveryActivity(rejected);
                var message = AgentTranscript.CreateAssistantMessage(
                    "Невалидный ответ модели исключён из контекста; запрос повторён.",
                    rejected.Completion,
                    activity,
                    rejected.ParseResult == null ? null : rejected.ParseResult.RecoveredDecisionSummary,
                    rejected.ParseResult == null ? null : rejected.ParseResult.RecoveredGoal);
                message.ExcludeFromModelContext = true;
                session.Messages.Add(message);
            }
        }

        public static ChatActivity CreatePlannerRecoveryActivity(AgentPlannerRejectedResponse rejected)
        {
            var parseResult = rejected == null ? null : rejected.ParseResult;
            var rawText = rejected == null ? string.Empty : rejected.RawText ?? string.Empty;
            var storedText = BoundRejectedResponse(rawText);
            var fallback = rejected != null && string.Equals(
                rejected.RecoveryAction,
                "json_object_fallback",
                StringComparison.OrdinalIgnoreCase);
            var recoveredSummary = parseResult == null ? null : parseResult.RecoveredDecisionSummary;
            return new ChatActivity
            {
                Kind = "diagnostic",
                Title = string.IsNullOrWhiteSpace(recoveredSummary)
                    ? "Некорректный ответ модели"
                    : TruncateLine(recoveredSummary, 240),
                Subtitle = fallback
                    ? "Некорректный формат · json_schema → json_object"
                    : "Некорректный формат · " + (rejected == null ? string.Empty : rejected.ResponseMode + " · повтор " + rejected.RetryNumber + "/" + rejected.RetryLimit),
                Status = "completed",
                ExecutionStatus = fallback ? "format_rejected_fallback" : "format_rejected_retry",
                ErrorCode = parseResult == null ? "unknown" : parseResult.ErrorCode,
                Retryable = true,
                ResultMessage = "Ответ не выполнен из-за формата" +
                    (parseResult == null ? "." : ": " + parseResult.ErrorCode + ".") +
                    " Runtime повторил запрос; текст решения показан только для диагностики.",
                DataJson = JsonConvert.SerializeObject(new
                {
                    recoveryAction = rejected == null ? string.Empty : rejected.RecoveryAction,
                    responseMode = rejected == null ? string.Empty : rejected.ResponseMode,
                    retryNumber = rejected == null ? 0 : rejected.RetryNumber,
                    retryLimit = rejected == null ? 0 : rejected.RetryLimit,
                    errorCode = parseResult == null ? "unknown" : parseResult.ErrorCode,
                    errorMessage = parseResult == null ? string.Empty : parseResult.ErrorMessage,
                    responseChars = rawText.Length,
                    responseTruncated = storedText.Length < rawText.Length,
                    response = storedText,
                    toolCalls = rejected == null || rejected.Completion == null ? null : rejected.Completion.ToolCalls
                })
            };
        }

        public static string RecordPlannerFailure(
            ChatSession session,
            LlmCompletionResult completion,
            string rawText,
            AgentPlannerParseResult parseResult,
            string title)
        {
            var assistantText = "Planner response is invalid: " +
                (parseResult == null ? "unknown" : parseResult.ErrorCode + ". " + parseResult.ErrorMessage);
            if (session != null)
            {
                var storedText = BoundRejectedResponse(rawText);
                session.Messages.Add(AgentTranscript.CreateAssistantMessage(assistantText, completion, new ChatActivity
                {
                    Kind = "diagnostic",
                    Title = title,
                    Subtitle = string.IsNullOrWhiteSpace(parseResult == null ? null : parseResult.RecoveredDecisionSummary)
                        ? "strict_json"
                        : TruncateLine(parseResult.RecoveredDecisionSummary, 240),
                    Status = "failed",
                    ExecutionStatus = parseResult == null ? "unknown" : parseResult.ErrorCode,
                    ResultMessage = "Модель вернула некорректный формат плана: " + (parseResult == null ? "unknown" : parseResult.ErrorCode) + ".",
                    DataJson = JsonConvert.SerializeObject(new
                    {
                        errorCode = parseResult == null ? "unknown" : parseResult.ErrorCode,
                        errorMessage = parseResult == null ? string.Empty : parseResult.ErrorMessage,
                        responseChars = (rawText ?? string.Empty).Length,
                        responseTruncated = storedText.Length < (rawText ?? string.Empty).Length,
                        response = storedText
                    })
                },
                    parseResult == null ? null : parseResult.RecoveredDecisionSummary,
                    parseResult == null ? null : parseResult.RecoveredGoal));
            }
            return assistantText;
        }

        private static string BoundRejectedResponse(string value)
        {
            value = value ?? string.Empty;
            return value.Length <= MaxRejectedResponseChars
                ? value
                : value.Substring(0, MaxRejectedResponseChars);
        }

        private static string TruncateLine(string value, int maxChars)
        {
            value = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            return value.Length <= maxChars ? value : value.Substring(0, maxChars) + "…";
        }

        public static string RecordMissingTools(
            ChatSession session,
            RoutedTask route,
            ToolCatalogSlice slice)
        {
            var host = route == null ? string.Empty : route.App;
            var assistantText = "Нет доступного локального инструмента для этого этапа задачи.";
            if (session != null)
            {
                session.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = assistantText,
                    Activity = new ChatActivity
                    {
                        Kind = "diagnostic",
                        Title = "Инструменты недоступны",
                        Subtitle = host,
                        Status = "failed",
                        ExecutionStatus = "no_available_tools",
                        ResultMessage = "host=" + host + "; reason=" + (route == null ? string.Empty : route.DecisionReason),
                        DataJson = BuildRoutingDiagnosticsJson(route, slice)
                    }
                });
            }
            return assistantText;
        }

        public static ChatActivity BuildRoutingActivity(RoutedTask route, ToolCatalogSlice slice)
        {
            return new ChatActivity
            {
                Kind = "diagnostic",
                Title = BuildTaskProgressMessage(route, false).TrimEnd('.'),
                Subtitle = route == null ? string.Empty : route.App,
                Status = "completed",
                ExecutionStatus = "routed",
                ResultMessage = route == null
                    ? string.Empty
                    : "phase=" + route.Phase + "; reason=" + route.DecisionReason + "; tools=" + (slice == null ? 0 : slice.Tools.Count),
                DataJson = BuildRoutingDiagnosticsJson(route, slice)
            };
        }

        public static string BuildTaskProgressMessage(RoutedTask route, bool active)
        {
            if (route == null)
            {
                return active ? "Анализирую задачу..." : "Проверяю доступные действия.";
            }

            if (!active) return "Проверяю доступные инструменты.";

            if (string.Equals(route.Phase, AgentPhases.Verification, StringComparison.OrdinalIgnoreCase))
            {
                return "Проверяю результат внесенных изменений...";
            }
            if (string.Equals(route.Phase, AgentPhases.ReadOnly, StringComparison.OrdinalIgnoreCase))
            {
                return "Изучаю содержимое текущего документа...";
            }
            return "Выбираю следующее действие...";
        }

        public static string FriendlyToolAction(ToolCommand command)
        {
            if (command == null)
            {
                return string.Empty;
            }
            if (!string.IsNullOrWhiteSpace(command.Description) &&
                !command.Description.StartsWith("Deterministic", StringComparison.OrdinalIgnoreCase))
            {
                return command.Description.Trim().TrimEnd('.');
            }

            switch ((command.ToolId ?? string.Empty).ToLowerInvariant())
            {
                case "excel.get_context": return "читаю контекст книги";
                case "excel.get_selection": return "читаю выделенные ячейки";
                case "excel.workbook_summary": return "читаю структуру книги";
                case "excel.list_sheets": return "получаю список листов";
                case "excel.read_range": return "читаю значения диапазона";
                case "excel.read_formula_range": return "читаю формулы диапазона";
                case "excel.profile_range": return "анализирую структуру диапазона";
                case "excel.find_cells": return "ищу ячейки";
                case "excel.write_range": return "записываю значение в ячейки";
                case "excel.write_table": return "записываю таблицу";
                case "excel.set_formula": return "записываю формулу";
                case "excel.add_table": return "создаю таблицу Excel";
                case "excel.format_range": return "форматирую диапазон";
                case "excel.autofit": return "подбираю ширину строк и столбцов";
                case "excel.add_sheet": return "создаю новый лист";
                case "excel.rename_sheet": return "переименовываю лист";
                case "excel.clear_range": return "очищаю диапазон";
                case "excel.sort_range": return "сортирую диапазон";
                case "excel.filter_range": return "фильтрую диапазон";
                case "excel.replace_cells": return "заменяю значения в ячейках";
                case "excel.list_charts": return "проверяю список графиков";
                case "excel.get_chart": return "читаю параметры графика";
                case "excel.add_chart": return "создаю график";
                case "excel.update_chart": return "изменяю график";
                case "excel.delete_chart": return "удаляю график";
                case "excel.vba_list_modules": return "получаю список VBA-модулей";
                case "excel.vba_read_module": return "читаю VBA-модуль";
                case "excel.insert_vba_module": return "создаю VBA-модуль";
                case "excel.vba_replace_module": return "обновляю VBA-модуль";
                case "excel.run_macro": return "запускаю макрос";
                case "common.skills_load": return "загружаю инструкции навыка";
                default: return FriendlyToolAction(command.ToolId);
            }
        }

        public static string FriendlyToolAction(string toolId)
        {
            var id = (toolId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(id)) return "выполняю действие";
            var action = ToolActionKind(id);
            if (action == "SEARCH") return "выполняю поиск";
            if (action == "READ") return "читаю данные";
            if (action == "CREATE") return "создаю объект";
            if (action == "WRITE") return "записываю данные";
            if (action == "UPDATE") return "обновляю объект";
            if (action == "DELETE") return "удаляю объект";
            if (action == "RUN") return "запускаю действие";
            if (action == "CHECK") return "проверяю результат";
            if (action == "LOAD") return "загружаю данные";
            return "выполняю действие";
        }

        public static string ToolActionKind(string toolId)
        {
            var id = (toolId ?? string.Empty).ToLowerInvariant();
            if (id.Contains("find") || id.Contains("search")) return "SEARCH";
            if (id.Contains("delete") || id.Contains("remove") || id.Contains("clear")) return "DELETE";
            if (id.Contains("add_") || id.Contains("create") || id.Contains("insert")) return "CREATE";
            if (id.Contains("write") || id.Contains("set_formula")) return "WRITE";
            if (id.Contains("update") || id.Contains("replace") || id.Contains("rename") ||
                id.Contains("format") || id.Contains("autofit") || id.Contains("sort") || id.Contains("filter") || id.Contains("upsert")) return "UPDATE";
            if (id.Contains("run") || id.Contains("execute")) return "RUN";
            if (id.Contains("verify") || id.Contains("validate") || id.Contains("check")) return "CHECK";
            if (id.Contains("load")) return "LOAD";
            if (id.Contains("read") || id.Contains("get_") || id.Contains("list_") ||
                id.Contains("summary") || id.Contains("profile") || id.Contains("inspect")) return "READ";
            return "ACTION";
        }

        public static string BuildRoutingDiagnosticsJson(RoutedTask route, ToolCatalogSlice slice)
        {
            var exclusions = slice == null || slice.Excluded == null
                ? new List<ToolExclusion>()
                : slice.Excluded;
            return JsonConvert.SerializeObject(new
            {
                route = route == null ? null : new
                {
                    app = route.App,
                    mode = route.Mode,
                    taskType = route.TaskType,
                    phase = route.Phase,
                    requiresTool = route.RequiresTool,
                    requiresInspection = route.RequiresInspection,
                    reason = route.DecisionReason
                },
                selectedTools = slice == null
                    ? new string[0]
                    : slice.Tools.Select(tool => tool.Id).ToArray(),
                selectedToolDetails = slice == null
                    ? new object[0]
                    : slice.Tools.Select(tool => new
                    {
                        toolId = tool.Id,
                        mutatesDocument = tool.MutatesDocument,
                        mutatesLocalState = tool.MutatesLocalState,
                        agentCanRun = tool.AgentCanRun,
                        requiresConfirmation = tool.RequiresConfirmation,
                        riskLevel = tool.RiskLevel
                    }).ToArray(),
                excludedCounts = exclusions
                    .GroupBy(item => item.Reason ?? "unknown", StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
                excludedTools = exclusions.Take(40).Select(item => new
                {
                    toolId = item.ToolId,
                    reason = item.Reason,
                    detail = item.Detail
                }).ToArray()
            });
        }

        public static ChatActivity CreateRunningActivity(ToolCommand command, string status, string kind)
        {
            return new ChatActivity
            {
                Kind = string.IsNullOrWhiteSpace(kind) ? "tool" : kind,
                Title = command == null ? "Действие" : FriendlyToolAction(command),
                Subtitle = command == null ? string.Empty : command.ToolId,
                Status = status,
                ExecutionStatus = status,
                ToolId = command == null ? string.Empty : command.ToolId,
                ArgumentsJson = command == null ? null : JsonConvert.SerializeObject(command.Arguments, Formatting.Indented)
            };
        }

        public static ChatActivity CreateToolBatchActivity(IEnumerable<ToolCommand> commands, string status)
        {
            var items = (commands ?? new ToolCommand[0]).Where(command => command != null).ToList();
            var activity = new ChatActivity
            {
                BatchId = "batch_" + Guid.NewGuid().ToString("N"),
                Kind = "tool_batch",
                Title = "Инструменты · " + items.Count,
                Subtitle = "Последовательное выполнение",
                Status = string.IsNullOrWhiteSpace(status) ? "planned" : status,
                ExecutionStatus = string.IsNullOrWhiteSpace(status) ? "planned" : status
            };
            foreach (var command in items)
            {
                activity.Children.Add(CreateRunningActivity(command, activity.Status, "tool"));
            }
            return activity;
        }

        public static void UpdateToolBatchActivity(ChatActivity batch, int index, ToolCommand command, ToolResult result)
        {
            if (batch == null || index < 0 || index >= batch.Children.Count) return;
            batch.Children[index] = AgentTranscript.CreateToolActivity(command, result, "tool");
            var children = batch.Children ?? new List<ChatActivity>();
            batch.Status = children.Any(child => child != null && string.Equals(child.Status, "failed", StringComparison.OrdinalIgnoreCase))
                ? "failed"
                : children.Any(child => child != null && string.Equals(child.Status, "waiting", StringComparison.OrdinalIgnoreCase))
                    ? "waiting"
                    : children.All(child => child != null && string.Equals(child.Status, "completed", StringComparison.OrdinalIgnoreCase))
                        ? "completed"
                        : "running";
            batch.ExecutionStatus = batch.Status;
            batch.ResultMessage = string.Empty;
        }

    }
}
