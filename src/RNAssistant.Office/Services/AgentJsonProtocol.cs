using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal static class AgentJsonProtocol
    {
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
            var root = new JObject
            {
                ["ok"] = result != null && result.Success,
                ["tool_call_id"] = command == null ? string.Empty : command.ToolCallId ?? string.Empty,
                ["name"] = command == null ? string.Empty : command.ToolId ?? string.Empty,
                ["status"] = result == null ? "failed" : result.Status ?? (result.Success ? "completed" : "failed"),
                ["message"] = result == null ? "Tool returned no result." : result.Message ?? string.Empty,
                ["data"] = ParseData(result == null ? null : result.DataJson),
                ["error"] = result != null && result.Success
                    ? null
                    : new JObject
                    {
                        ["code"] = result == null
                            ? "missing_result"
                            : string.IsNullOrWhiteSpace(result.ErrorCode) ? "tool_failed" : result.ErrorCode,
                        ["message"] = result == null ? "Tool returned no result." : result.Message,
                        ["retryable"] = result == null ? false : result.Retryable ?? false
                    }
            };
            return root.ToString(Formatting.None);
        }

        public static ChatMessage CreateToolResultMessage(ToolCommand command, ToolResult result)
        {
            return new ChatMessage
            {
                Role = "user",
                Content = "TOOL_RESULT:\n" + BuildToolResult(command, result),
                ProtocolMessage = true
            };
        }

        public static ChatMessage CreateToolCallMessage(string rawJson, RNAssistant.Core.Llm.LlmCompletionResult completion)
        {
            var message = AgentTranscript.CreateAssistantMessage(rawJson ?? string.Empty, completion);
            message.ProtocolMessage = true;
            return message;
        }

        private static JToken ParseData(string dataJson)
        {
            if (string.IsNullOrWhiteSpace(dataJson)) return JValue.CreateNull();
            try
            {
                return JToken.Parse(dataJson);
            }
            catch (JsonException)
            {
                return new JValue(dataJson);
            }
        }
    }
}
