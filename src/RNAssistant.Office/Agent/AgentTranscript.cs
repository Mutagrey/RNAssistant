using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office
{
    internal static class AgentTranscript
    {
        private const int MaxTranscriptReasoningChars = 24000;

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
                ExcludeFromModelContext = true,
                Activity = activity
            };
        }

        public static ChatMessage CreateAssistantMessage(
            string content,
            LlmCompletionResult completion,
            ChatActivity activity = null,
            string decisionSummary = null,
            string goal = null)
        {
            var reasoning = completion == null ? null : completion.ReasoningContent;
            var transcriptReasoningTruncated = !string.IsNullOrEmpty(reasoning) && reasoning.Length > MaxTranscriptReasoningChars;
            return new ChatMessage
            {
                Role = "assistant",
                Content = content ?? string.Empty,
                DecisionSummary = decisionSummary,
                Goal = goal,
                ExcludeFromModelContext = activity != null,
                Activity = activity,
                PromptTokens = completion == null ? null : completion.PromptTokens,
                CompletionTokens = completion == null ? null : completion.CompletionTokens,
                TotalTokens = completion == null ? null : completion.TotalTokens,
                UsageJson = completion == null ? null : completion.UsageJson,
                ReasoningContent = transcriptReasoningTruncated
                    ? reasoning.Substring(0, MaxTranscriptReasoningChars)
                    : reasoning,
                ReasoningTokens = completion == null ? null : completion.ReasoningTokens,
                ReasoningTruncated = completion != null && (completion.ReasoningTruncated || transcriptReasoningTruncated)
            };
        }

        public static object DescribeResult(ToolCommand command, ToolResult result)
        {
            return new
            {
                toolId = command == null ? string.Empty : command.ToolId,
                description = command == null ? string.Empty : command.Description,
                success = result != null && result.Success,
                status = result == null ? string.Empty : result.Status,
                errorCode = result == null ? string.Empty : result.ErrorCode,
                retryable = result == null ? null : result.Retryable,
                pendingId = result == null ? string.Empty : result.PendingId,
                message = result == null ? string.Empty : result.Message,
                dataJson = result == null ? null : result.DataJson
            };
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
                ErrorCode = result == null ? null : result.ErrorCode,
                Retryable = result == null ? null : result.Retryable,
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
                return "Выполнение завершилось без итогового ответа модели.";
            }

            var text = JsonConvert.SerializeObject(results);
            if (text.IndexOf("waiting_confirmation", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Выполнение приостановлено: требуется подтверждение действия.";
            }
            if (text.IndexOf("skipped_auto_run", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Действия подготовлены, но автоматический запуск отключён.";
            }
            if (text.IndexOf("\"success\":false", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Выполнение остановлено после ошибки инструмента. Откройте шаг с ошибкой для диагностики.";
            }

            return "Готово: выполнено действий — " + count + ".";
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
                    var retryableToken = step["retryable"];
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
                        ErrorCode = (string)step["errorCode"],
                        Retryable = retryableToken == null || retryableToken.Type == JTokenType.Null
                            ? (bool?)null
                            : retryableToken.Value<bool>(),
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

        public static bool CanRetryToolError(ToolResult result)
        {
            if (result != null && result.Retryable.HasValue)
            {
                return result.Retryable.Value;
            }

            var message = result == null ? string.Empty : result.Message ?? string.Empty;
            return !IsWaitingResult(result) &&
                message.IndexOf("requires confirmation", StringComparison.OrdinalIgnoreCase) < 0 &&
                message.IndexOf("Auto tool execution is disabled", StringComparison.OrdinalIgnoreCase) < 0;
        }
    }
}
