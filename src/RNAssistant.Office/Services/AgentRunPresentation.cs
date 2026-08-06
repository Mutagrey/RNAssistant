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
                        Title = "Tool routing",
                        Subtitle = route == null ? string.Empty : route.TaskType + " / " + route.Phase,
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
                Subtitle = route == null ? string.Empty : route.Mode + " · " + route.TaskType,
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

            if (!active)
            {
                if (string.Equals(route.TaskType, "vba", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(route.TaskType, "macro_execution", StringComparison.OrdinalIgnoreCase))
                {
                    return "Проверяю доступные операции VBA.";
                }
                if (string.Equals(route.TaskType, "chart", StringComparison.OrdinalIgnoreCase))
                {
                    return "Проверяю доступные операции с графиками.";
                }
                return "Проверяю доступные действия для текущего документа.";
            }

            if (string.Equals(route.Phase, AgentPhases.Verification, StringComparison.OrdinalIgnoreCase))
            {
                return "Проверяю результат внесенных изменений...";
            }
            if (string.Equals(route.Phase, AgentPhases.ReadOnly, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(route.TaskType, "chart", StringComparison.OrdinalIgnoreCase))
                {
                    return "Изучаю существующие графики и их параметры...";
                }
                if (string.Equals(route.TaskType, "vba", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(route.TaskType, "macro_execution", StringComparison.OrdinalIgnoreCase))
                {
                    return "Изучаю VBA-проект и доступные модули...";
                }
                return "Изучаю содержимое текущего документа...";
            }
            if (string.Equals(route.TaskType, "chart", StringComparison.OrdinalIgnoreCase))
            {
                return "Подготавливаю изменения графика...";
            }
            if (string.Equals(route.TaskType, "vba", StringComparison.OrdinalIgnoreCase))
            {
                return "Подготавливаю VBA-код и параметры модуля...";
            }
            if (string.Equals(route.TaskType, "formatting", StringComparison.OrdinalIgnoreCase))
            {
                return "Подготавливаю форматирование документа...";
            }
            if (string.Equals(route.TaskType, "tool_authoring", StringComparison.OrdinalIgnoreCase))
            {
                return "Подготавливаю описание нового инструмента...";
            }
            return "Подготавливаю изменение текущего документа...";
        }

        public static string FriendlyToolAction(ToolCommand command)
        {
            if (command == null)
            {
                return string.Empty;
            }
            if (!string.IsNullOrWhiteSpace(command.Description))
            {
                return command.Description.Trim().TrimEnd('.');
            }

            switch ((command.ToolId ?? string.Empty).ToLowerInvariant())
            {
                case "excel.list_charts": return "проверяю список графиков";
                case "excel.get_chart": return "читаю параметры графика";
                case "excel.add_chart": return "создаю график";
                case "excel.update_chart": return "изменяю график";
                case "excel.delete_chart": return "удаляю график";
                case "excel.vba_read_project": return "читаю VBA-проект";
                case "excel.vba_read_module": return "читаю VBA-модуль";
                case "excel.insert_vba_module": return "создаю VBA-модуль";
                case "excel.vba_replace_module": return "обновляю VBA-модуль";
                case "excel.run_macro": return "запускаю макрос";
                default: return command.ToolId;
            }
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
                    riskAllowed = route.RiskAllowed,
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

    }
}
