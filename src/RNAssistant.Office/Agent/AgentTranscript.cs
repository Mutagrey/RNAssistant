using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office
{
    internal static class AgentTranscript
    {
        public static void AddLocalResultMessage(ChatSession session, ToolCommand command, ToolResult result)
        {
            session.Messages.Add(CreateLocalResultMessage(command, result));
        }

        public static ChatMessage CreateLocalResultMessage(ToolCommand command, ToolResult result)
        {
            var activity = CreateToolActivity(command, result, "tool");
            return new ChatMessage
            {
                Role = "assistant",
                Content = CreateToolFallbackContent(activity),
                Activity = activity
            };
        }

        public static ChatMessage CreateAssistantMessage(string content, LlmCompletionResult completion, ChatActivity activity = null)
        {
            return new ChatMessage
            {
                Role = "assistant",
                Content = content ?? string.Empty,
                Activity = activity,
                PromptTokens = completion == null ? null : completion.PromptTokens,
                CompletionTokens = completion == null ? null : completion.CompletionTokens,
                TotalTokens = completion == null ? null : completion.TotalTokens,
                UsageJson = completion == null ? null : completion.UsageJson
            };
        }

        public static ChatMessage CreateAgentPlanChatMessage(IReadOnlyList<ToolCommand> commands, LlmCompletionResult completion)
        {
            return CreateAssistantMessage(CreateAgentPlanMessage(commands), completion, CreateAgentPlanActivity(commands));
        }

        public static object DescribeResult(ToolCommand command, ToolResult result)
        {
            return new
            {
                toolId = command == null ? string.Empty : command.ToolId,
                description = command == null ? string.Empty : command.Description,
                success = result != null && result.Success,
                status = result == null ? string.Empty : result.Status,
                pendingId = result == null ? string.Empty : result.PendingId,
                message = result == null ? string.Empty : result.Message,
                dataJson = result == null ? null : result.DataJson
            };
        }

        public static string CreateAgentPlanMessage(IReadOnlyList<ToolCommand> commands)
        {
            var builder = new StringBuilder();
            builder.AppendLine("### Agent plan");
            if (commands == null || commands.Count == 0)
            {
                builder.AppendLine("No executable steps were returned.");
                return builder.ToString();
            }

            for (var i = 0; i < commands.Count; i++)
            {
                var command = commands[i];
                var title = command == null || string.IsNullOrWhiteSpace(command.Description)
                    ? (command == null ? "Tool step" : command.ToolId)
                    : command.Description;
                builder.AppendLine((i + 1) + ". " + title + " (`" + (command == null ? string.Empty : command.ToolId) + "`)");
            }

            return builder.ToString();
        }

        public static ChatActivity CreateAgentPlanActivity(IReadOnlyList<ToolCommand> commands)
        {
            return CreateAgentPlanActivity(commands, null);
        }

        public static ChatActivity CreateAgentPlanActivity(IReadOnlyList<ToolCommand> commands, IReadOnlyList<ToolCommandParseDiagnostic> diagnostics)
        {
            var activity = new ChatActivity
            {
                Kind = "plan",
                Title = "Agent plan",
                Subtitle = commands == null ? "No executable steps" : commands.Count + " step(s)",
                Status = commands == null || commands.Count == 0 ? "failed" : "planned"
            };

            foreach (var command in commands ?? new ToolCommand[0])
            {
                var title = command == null || string.IsNullOrWhiteSpace(command.Description)
                    ? (command == null ? "Tool step" : command.ToolId)
                    : command.Description;
                activity.Children.Add(new ChatActivity
                {
                    Kind = "tool",
                    Title = title,
                    Subtitle = command == null ? string.Empty : command.ToolId,
                    Status = "planned",
                    ToolId = command == null ? string.Empty : command.ToolId,
                    ArgumentsJson = command == null ? null : JsonConvert.SerializeObject(command.Arguments, Formatting.Indented)
                });
            }

            foreach (var diagnostic in diagnostics ?? new ToolCommandParseDiagnostic[0])
            {
                if (diagnostic == null || string.IsNullOrWhiteSpace(diagnostic.Code))
                {
                    continue;
                }

                activity.Children.Add(new ChatActivity
                {
                    Kind = "diagnostic",
                    Title = "Protocol diagnostic",
                    Subtitle = diagnostic.Code,
                    Status = diagnostic.Recovered ? "completed" : "failed",
                    ExecutionStatus = diagnostic.Recovered ? "recovered" : "failed",
                    ResultMessage = diagnostic.Message
                });
            }

            return activity;
        }

        public static ChatActivity CreateToolActivity(ToolCommand command, ToolResult result, string kind)
        {
            var success = result != null && result.Success;
            var message = result == null ? string.Empty : result.Message ?? string.Empty;
            var executionStatus = NormalizeExecutionStatus(result);
            var title = command == null || string.IsNullOrWhiteSpace(command.Description)
                ? (command == null ? "Tool step" : command.ToolId)
                : command.Description;

            var activity = new ChatActivity
            {
                Kind = string.IsNullOrWhiteSpace(kind) ? "tool" : kind,
                Title = title,
                Subtitle = command == null ? string.Empty : command.ToolId,
                Status = ToActivityStatus(result),
                ExecutionStatus = executionStatus,
                PendingId = result == null ? null : result.PendingId,
                ToolId = command == null ? string.Empty : command.ToolId,
                ArgumentsJson = command == null ? null : JsonConvert.SerializeObject(command.Arguments, Formatting.Indented),
                ResultMessage = message,
                DataJson = result == null ? null : result.DataJson
            };

            foreach (var child in ParsePipelineChildren(activity.DataJson))
            {
                activity.Children.Add(child);
            }

            return activity;
        }

        public static bool IsWaitingResult(ToolResult result)
        {
            var status = NormalizeExecutionStatus(result);
            return string.Equals(status, "waiting_confirmation", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "skipped_auto_run", StringComparison.OrdinalIgnoreCase);
        }

        public static string CreateRunSummary(IReadOnlyList<object> results)
        {
            var count = results == null ? 0 : results.Count;
            if (count == 0)
            {
                return "Agent completed without a final text response.";
            }

            var text = JsonConvert.SerializeObject(results);
            if (text.IndexOf("waiting_confirmation", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Agent paused for tool confirmation.";
            }
            if (text.IndexOf("skipped_auto_run", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Agent prepared tool calls, but auto-run is disabled.";
            }
            if (text.IndexOf("\"success\":false", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Agent stopped after a tool error.";
            }

            return "Agent executed " + count + " tool step(s).";
        }

        private static string ToActivityStatus(ToolResult result)
        {
            if (result != null && result.Success)
            {
                return "completed";
            }

            var status = NormalizeExecutionStatus(result);
            if (string.Equals(status, "waiting_confirmation", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "skipped_auto_run", StringComparison.OrdinalIgnoreCase))
            {
                return "waiting";
            }
            if (string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return "cancelled";
            }

            return "failed";
        }

        private static string NormalizeExecutionStatus(ToolResult result)
        {
            if (result == null)
            {
                return "failed";
            }

            if (!string.IsNullOrWhiteSpace(result.Status))
            {
                return result.Status;
            }

            return result.Success ? "completed" : "failed";
        }

        private static string CreateToolFallbackContent(ChatActivity activity)
        {
            var builder = new StringBuilder();
            builder.Append("Agent step: ");
            builder.AppendLine(string.IsNullOrWhiteSpace(activity == null ? null : activity.Title) ? "Tool step" : activity.Title);
            if (!string.IsNullOrWhiteSpace(activity == null ? null : activity.ToolId))
            {
                builder.AppendLine("Tool: " + activity.ToolId);
            }
            builder.AppendLine("Status: " + (activity == null ? "completed" : activity.Status));
            return builder.ToString();
        }

        private static IEnumerable<ChatActivity> ParsePipelineChildren(string dataJson)
        {
            if (string.IsNullOrWhiteSpace(dataJson))
            {
                return new ChatActivity[0];
            }

            try
            {
                var root = JObject.Parse(dataJson);
                var steps = root["steps"] as JArray;
                if (steps == null || steps.Count == 0)
                {
                    return new ChatActivity[0];
                }

                var children = new List<ChatActivity>();
                foreach (var stepToken in steps)
                {
                    var step = stepToken as JObject;
                    if (step == null)
                    {
                        continue;
                    }

                    var toolId = (string)step["toolId"];
                    var id = (string)step["id"];
                    var successToken = step["success"];
                    var success = successToken != null && successToken.Type == JTokenType.Boolean && successToken.Value<bool>();
                    var status = (string)step["status"];
                    if (string.IsNullOrWhiteSpace(status))
                    {
                        status = success ? "completed" : "failed";
                    }
                    children.Add(new ChatActivity
                    {
                        Kind = "tool",
                        Title = string.IsNullOrWhiteSpace(id) ? toolId : id,
                        Subtitle = toolId,
                        Status = success ? "completed" : "failed",
                        ExecutionStatus = status,
                        ToolId = toolId,
                        ResultMessage = (string)step["message"],
                        DataJson = (string)step["dataJson"]
                    });
                }

                return children;
            }
            catch (JsonException)
            {
                return new ChatActivity[0];
            }
        }

        public static bool ShouldForceAgentToolUse(string text, string host)
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

            return Regex.IsMatch(value, "(лист|таблиц|диапазон|ячейк|график|диаграмм|html|page|страниц|report|отчет|component|компонент|ui|sheet|table|range|cell|chart|slide|слайд|document|документ|selection|выдел|mail|email|письм)");
        }

        public static bool CanRetryToolError(ToolResult result)
        {
            var message = result == null ? string.Empty : result.Message ?? string.Empty;
            return !IsWaitingResult(result) &&
                message.IndexOf("requires confirmation", StringComparison.OrdinalIgnoreCase) < 0 &&
                message.IndexOf("Auto tool execution is disabled", StringComparison.OrdinalIgnoreCase) < 0;
        }
    }
}
