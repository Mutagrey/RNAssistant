using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

namespace RNAssistant.Office
{
    internal static class AgentTranscript
    {
        public static void AddLocalResultMessage(ChatSession session, SkillCommand command, SkillResult result)
        {
            var activity = CreateToolActivity(command, result, "tool");
            session.Messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = CreateToolFallbackContent(activity),
                Activity = activity
            });
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

        public static ChatMessage CreateAgentPlanChatMessage(IReadOnlyList<SkillCommand> commands, LlmCompletionResult completion)
        {
            return CreateAssistantMessage(CreateAgentPlanMessage(commands), completion, CreateAgentPlanActivity(commands));
        }

        public static object DescribeResult(SkillCommand command, SkillResult result)
        {
            return new
            {
                skillId = command == null ? string.Empty : command.SkillId,
                description = command == null ? string.Empty : command.Description,
                success = result != null && result.Success,
                message = result == null ? string.Empty : result.Message,
                dataJson = result == null ? null : result.DataJson
            };
        }

        public static string CreateAgentPlanMessage(IReadOnlyList<SkillCommand> commands)
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
                    ? (command == null ? "Tool step" : command.SkillId)
                    : command.Description;
                builder.AppendLine((i + 1) + ". " + title + " (`" + (command == null ? string.Empty : command.SkillId) + "`)");
            }

            return builder.ToString();
        }

        public static ChatActivity CreateAgentPlanActivity(IReadOnlyList<SkillCommand> commands)
        {
            var activity = new ChatActivity
            {
                Kind = "plan",
                Title = "Agent plan",
                Subtitle = commands == null ? "No executable steps" : commands.Count + " step(s)",
                Status = commands == null || commands.Count == 0 ? "failed" : "planned"
            };

            foreach (var command in commands ?? new SkillCommand[0])
            {
                var title = command == null || string.IsNullOrWhiteSpace(command.Description)
                    ? (command == null ? "Tool step" : command.SkillId)
                    : command.Description;
                activity.Children.Add(new ChatActivity
                {
                    Kind = "tool",
                    Title = title,
                    Subtitle = command == null ? string.Empty : command.SkillId,
                    Status = "planned",
                    ToolId = command == null ? string.Empty : command.SkillId,
                    ArgumentsJson = command == null ? null : JsonConvert.SerializeObject(command.Arguments, Formatting.Indented)
                });
            }

            return activity;
        }

        public static ChatActivity CreateToolActivity(SkillCommand command, SkillResult result, string kind)
        {
            var success = result != null && result.Success;
            var message = result == null ? string.Empty : result.Message ?? string.Empty;
            var waiting = !success && message.IndexOf("requires confirmation", StringComparison.OrdinalIgnoreCase) >= 0;
            var title = command == null || string.IsNullOrWhiteSpace(command.Description)
                ? (command == null ? "Tool step" : command.SkillId)
                : command.Description;

            var activity = new ChatActivity
            {
                Kind = string.IsNullOrWhiteSpace(kind) ? "tool" : kind,
                Title = title,
                Subtitle = command == null ? string.Empty : command.SkillId,
                Status = success ? "completed" : (waiting ? "waiting" : "failed"),
                ToolId = command == null ? string.Empty : command.SkillId,
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
            if (!string.IsNullOrWhiteSpace(activity == null ? null : activity.ResultMessage))
            {
                builder.AppendLine("Result: " + activity.ResultMessage);
            }
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
                    children.Add(new ChatActivity
                    {
                        Kind = "tool",
                        Title = string.IsNullOrWhiteSpace(id) ? toolId : id,
                        Subtitle = toolId,
                        Status = success ? "completed" : "failed",
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

            return Regex.IsMatch(value, "(лист|таблиц|диапазон|ячейк|график|диаграмм|sheet|table|range|cell|chart|slide|слайд|document|документ|selection|выдел|mail|email|письм)");
        }

        public static bool CanRetryToolError(SkillResult result)
        {
            var message = result == null ? string.Empty : result.Message ?? string.Empty;
            return message.IndexOf("requires confirmation", StringComparison.OrdinalIgnoreCase) < 0 &&
                message.IndexOf("Auto tool execution is disabled", StringComparison.OrdinalIgnoreCase) < 0;
        }
    }
}
