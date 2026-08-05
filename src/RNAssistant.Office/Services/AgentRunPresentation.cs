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
                session.Messages.Add(AgentTranscript.CreateAssistantMessage(assistantText, completion, new ChatActivity
                {
                    Kind = "diagnostic",
                    Title = title,
                    Subtitle = "strict_json",
                    Status = "failed",
                    ExecutionStatus = parseResult == null ? "unknown" : parseResult.ErrorCode,
                    ResultMessage = "Модель вернула некорректный формат плана: " + (parseResult == null ? "unknown" : parseResult.ErrorCode) + ".",
                    DataJson = JsonConvert.SerializeObject(new
                    {
                        errorCode = parseResult == null ? "unknown" : parseResult.ErrorCode,
                        errorMessage = parseResult == null ? string.Empty : parseResult.ErrorMessage,
                        responsePreview = AgentText.Truncate(rawText, 1200)
                    })
                }));
            }
            return assistantText;
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
