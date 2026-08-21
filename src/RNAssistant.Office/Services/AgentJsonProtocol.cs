using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal static class AgentJsonProtocol
    {
        internal const int DefaultMaxToolResultDataTokens = 8192;
        private const int MaxToolResultMessageTokens = 512;

        public static ChatMessage CreateFormatRepairMessage(string error, int attempt, int maxAttempts)
        {
            var root = new JObject
            {
                ["error"] = string.IsNullOrWhiteSpace(error) ? "Invalid Agent JSON response." : error.Trim(),
                ["attempt"] = attempt,
                ["max_attempts"] = maxAttempts,
                ["instruction"] =
                    "Return a new response to the current user request as exactly one JSON object with message and tool_calls. " +
                    "Do not use Markdown, fences, or surrounding prose. To answer, clarify, refuse, or report inability, " +
                    "put the user-facing text in message and return an empty tool_calls array. " +
                    "To use tools, return calls with unique id, exact name, and object arguments."
            };
            return new ChatMessage
            {
                Role = "user",
                Content = "FORMAT_REPAIR:\n" + root.ToString(Formatting.None),
                ProtocolMessage = true
            };
        }

        public static ToolCommand ToCommand(AgentToolCall call)
        {
            return new ToolCommand
            {
                ToolId = call == null ? string.Empty : call.Name,
                ToolCallId = call == null ? string.Empty : call.Id,
                Arguments = call == null
                    ? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    : call.Arguments ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            };
        }

        public static string BuildToolResult(ToolCommand command, ToolResult result)
        {
            return BuildToolResult(command, result, DefaultMaxToolResultDataTokens);
        }

        internal static string BuildToolResult(ToolCommand command, ToolResult result, int maxDataTokens)
        {
            var message = BoundText(
                result == null ? "Tool returned no result." : result.Message ?? string.Empty,
                MaxToolResultMessageTokens);
            var root = new JObject
            {
                ["ok"] = result != null && result.Success,
                ["tool_call_id"] = command == null ? string.Empty : command.ToolCallId ?? string.Empty,
                ["name"] = command == null ? string.Empty : command.ToolId ?? string.Empty,
                ["status"] = result == null ? "failed" : result.Status ?? (result.Success ? "completed" : "failed"),
                ["message"] = message,
                ["data"] = ParseData(result == null ? null : result.DataJson, maxDataTokens),
                ["error"] = result != null && result.Success
                    ? null
                    : new JObject
                    {
                        ["code"] = result == null
                            ? "missing_result"
                            : string.IsNullOrWhiteSpace(result.ErrorCode) ? "tool_failed" : result.ErrorCode,
                        ["message"] = message,
                        ["retryable"] = result == null ? false : result.Retryable ?? false
                    }
            };
            return root.ToString(Formatting.None);
        }

        public static ChatMessage CreateToolResultMessage(ToolCommand command, ToolResult result)
        {
            return CreateToolResultMessage(command, result, DefaultMaxToolResultDataTokens);
        }

        internal static ChatMessage CreateToolResultMessage(ToolCommand command, ToolResult result, int maxDataTokens)
        {
            return new ChatMessage
            {
                Role = "user",
                Content = "TOOL_RESULT:\n" + BuildToolResult(command, result, maxDataTokens),
                ProtocolMessage = true
            };
        }

        public static ChatMessage CreateToolCallMessage(
            AgentToolCall call,
            string message,
            RNAssistant.Core.Llm.LlmCompletionResult completion)
        {
            var content = new JObject
            {
                ["message"] = message ?? string.Empty,
                ["tool_calls"] = new JArray
                {
                    new JObject
                    {
                        ["id"] = call == null ? string.Empty : call.Id ?? string.Empty,
                        ["name"] = call == null ? string.Empty : call.Name ?? string.Empty,
                        ["arguments"] = call == null || call.Arguments == null
                            ? new JObject()
                            : JObject.FromObject(call.Arguments)
                    }
                }
            }.ToString(Formatting.None);
            var protocolMessage = AgentTranscript.CreateAssistantMessage(content, completion);
            protocolMessage.ProtocolMessage = true;
            return protocolMessage;
        }

        private static JToken ParseData(string dataJson, int maxDataTokens)
        {
            if (string.IsNullOrWhiteSpace(dataJson)) return JValue.CreateNull();
            JToken parsed;
            try
            {
                parsed = JToken.Parse(dataJson);
            }
            catch (JsonException)
            {
                parsed = new JValue(dataJson);
            }

            var compact = parsed.ToString(Formatting.None);
            var estimatedTokens = ModelContextBudget.EstimateTextTokens(compact);
            var boundedTokens = Math.Max(0, maxDataTokens);
            if (estimatedTokens <= boundedTokens)
            {
                return parsed;
            }

            var previewBudget = Math.Max(0, boundedTokens - 96);
            return new JObject
            {
                ["truncated"] = true,
                ["original_chars"] = compact.Length,
                ["original_estimated_tokens"] = estimatedTokens,
                ["preview"] = ModelContextBudget.TruncateText(compact, previewBudget),
                ["hint"] = "The tool result was too large for the model context. Request a smaller scope."
            };
        }

        private static string BoundText(string value, int maxTokens)
        {
            var text = value ?? string.Empty;
            if (ModelContextBudget.EstimateTextTokens(text) <= maxTokens) return text;
            return ModelContextBudget.TruncateText(text, Math.Max(1, maxTokens - 8)) + "...[truncated]";
        }
    }
}
