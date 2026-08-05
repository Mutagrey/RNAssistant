using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Services
{
    internal static class AgentProtocolHistory
    {
        private const int MaxDataChars = 6000;
        private const int MaxSummaryChars = 1200;

        public static void AppendToolExchange(
            ICollection<ChatMessage> protocolMessages,
            AgentPlannerAttempt attempt,
            ToolCommand command,
            ToolResult result,
            AppSettings settings)
        {
            if (protocolMessages == null || command == null) return;
            var callId = string.IsNullOrWhiteSpace(command.ToolCallId)
                ? "call_" + Guid.NewGuid().ToString("N")
                : command.ToolCallId;
            command.ToolCallId = callId;
            var apiTool = attempt == null || attempt.RequestOptions == null
                ? null
                : (attempt.RequestOptions.Tools ?? new LlmToolDefinition[0]).FirstOrDefault(tool =>
                    tool != null && string.Equals(tool.ToolId, command.ToolId, StringComparison.OrdinalIgnoreCase));
            var apiName = !string.IsNullOrWhiteSpace(command.ToolApiName)
                ? command.ToolApiName
                : apiTool == null ? command.ToolId.Replace('.', '_') : apiTool.ApiName;
            command.ToolApiName = apiName;
            var argumentsJson = JsonConvert.SerializeObject(command.Arguments ?? new Dictionary<string, object>());
            var toolCall = new LlmToolCall
            {
                Id = callId,
                Name = apiName,
                ArgumentsJson = argumentsJson
            };

            var resultJson = JsonConvert.SerializeObject(new
            {
                protocolVersion = AgentDecisionProtocol.Version,
                callId = callId,
                toolId = command.ToolId,
                ok = result != null && result.Success,
                status = result == null ? "failed" : result.Status,
                summary = BoundText(result == null ? "Tool returned no result." : result.Message, MaxSummaryChars),
                data = ParseProtocolData(result == null ? null : result.DataJson),
                error = result != null && result.Success ? null : new
                {
                    code = result == null ? "missing_result" : result.ErrorCode,
                    message = BoundText(result == null ? "Tool returned no result." : result.Message, MaxSummaryChars),
                    retryable = result == null ? (bool?)false : result.Retryable
                }
            });

            var role = NormalizeToolResultRole(settings == null ? null : settings.ToolResultRole);
            if (string.Equals(role, "tool", StringComparison.Ordinal))
            {
                var nativeCalls = attempt == null || attempt.Completion == null ? null : attempt.Completion.ToolCalls;
                var assistantCall = nativeCalls != null && nativeCalls.Count == 1
                    ? new LlmToolCall
                    {
                        Id = callId,
                        Type = string.IsNullOrWhiteSpace(nativeCalls[0].Type) ? "function" : nativeCalls[0].Type,
                        Name = string.IsNullOrWhiteSpace(nativeCalls[0].Name) ? apiName : nativeCalls[0].Name,
                        ArgumentsJson = string.IsNullOrWhiteSpace(nativeCalls[0].ArgumentsJson) ? argumentsJson : nativeCalls[0].ArgumentsJson
                    }
                    : toolCall;
                protocolMessages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = string.Empty,
                    ToolCalls = new List<LlmToolCall> { assistantCall }
                });
                protocolMessages.Add(new ChatMessage
                {
                    Role = "tool",
                    ToolCallId = callId,
                    ToolName = apiName,
                    Content = resultJson
                });
                return;
            }

            if (attempt != null && !string.IsNullOrWhiteSpace(attempt.Text))
            {
                protocolMessages.Add(new ChatMessage { Role = "assistant", Content = attempt.Text });
            }
            protocolMessages.Add(new ChatMessage { Role = role, Content = "TOOL_RESULT:\n" + resultJson });
        }

        private static object ParseProtocolData(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (value.Length > MaxDataChars)
            {
                return new
                {
                    truncated = true,
                    originalChars = value.Length,
                    preview = value.Substring(0, MaxDataChars)
                };
            }
            try { return JToken.Parse(value); }
            catch (JsonException) { return value; }
        }

        private static string BoundText(string value, int maxChars)
        {
            value = value ?? string.Empty;
            return value.Length <= maxChars ? value : value.Substring(0, maxChars) + "…";
        }

        private static string NormalizeToolResultRole(string role)
        {
            if (string.Equals(role, "developer", StringComparison.OrdinalIgnoreCase)) return "developer";
            if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase)) return "user";
            return "tool";
        }

    }
}
