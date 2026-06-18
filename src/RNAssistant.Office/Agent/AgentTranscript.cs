using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

namespace RNAssistant.Office
{
    internal static class AgentTranscript
    {
        public static void AddLocalResultMessage(ChatSession session, SkillCommand command, SkillResult result)
        {
            var success = result != null && result.Success;
            var message = result == null ? string.Empty : result.Message ?? string.Empty;
            var waiting = !success && message.IndexOf("requires confirmation", StringComparison.OrdinalIgnoreCase) >= 0;
            var title = string.IsNullOrWhiteSpace(command.Description) ? command.SkillId : command.Description;
            session.Messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = "### Agent step: " + title +
                    "\n- Tool: `" + command.SkillId + "`" +
                    "\n- Status: " + (success ? "completed" : (waiting ? "waiting for confirmation" : "failed")) +
                    "\n- Result: " + message +
                    (string.IsNullOrWhiteSpace(result == null ? null : result.DataJson) ? string.Empty : "\n```json\n" + result.DataJson + "\n```")
            });
        }

        public static ChatMessage CreateAssistantMessage(string content, LlmCompletionResult completion)
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

            builder.AppendLine();
            builder.AppendLine("```json");
            builder.AppendLine(JsonConvert.SerializeObject(commands.Select(command => new
            {
                description = command == null ? string.Empty : command.Description,
                skillId = command == null ? string.Empty : command.SkillId,
                arguments = command == null ? null : command.Arguments
            }), Formatting.Indented));
            builder.AppendLine("```");
            return builder.ToString();
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
