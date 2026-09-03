using RNAssistant.Core.Tools;
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Tools.Contracts;
using TerminalResult = RNAssistant.Core.Tools.Contracts.ToolResult;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    internal static class AgentJsonProtocol
    {
        internal const int DefaultMaxToolResultDataTokens = 8192;
        private const int MaxToolResultMessageTokens = 512;

        public static string BuildToolResult(ToolInvocation command, TerminalResult result)
        {
            return BuildToolResult(command, new ToolResultMaterialization(result));
        }

        internal static string BuildToolResult(ToolInvocation command, TerminalResult result,
            int maxDataTokens, AppSettings settings = null)
        {
            return BuildToolResult(command, new ToolResultMaterialization(result), maxDataTokens, settings);
        }

        internal static string BuildToolResult(ToolInvocation command, ToolResultMaterialization materialized,
            int maxDataTokens = DefaultMaxToolResultDataTokens, AppSettings settings = null)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (materialized == null) throw new ArgumentNullException(nameof(materialized));
            var result = materialized.Result;
            // Validate the complete terminal contract before budgeting, so truncation
            // cannot hide invalid JSON or a non-resource transport in the source.
            var complete = ToolResultWire.Write(command.ToolCallId, command.ToolId, result, materialized.ResultResource);
            var source = JsonConvert.DeserializeObject<JObject>(complete,
                new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
            var data = BoundData(source["data"], maxDataTokens, settings);
            if (materialized.ResultResource != null)
            {
                if (string.Equals(materialized.ResultResourceKind, ChatArtifactKinds.Chart, StringComparison.OrdinalIgnoreCase))
                    data = new JObject
                    {
                        ["externalized"] = true,
                        ["kind"] = ChatArtifactKinds.Chart,
                        ["original_chars"] = (result.DataJson ?? string.Empty).Length,
                        ["hint"] = "The chart body is available through the exact resource with relation=result."
                    };
                else if (data == null || data.Type == JTokenType.Null)
                    data = new JObject
                    {
                        ["externalized"] = true,
                        ["kind"] = ChatArtifactKinds.ToolResult
                    };
                var boundedData = data as JObject;
                if ((bool?)boundedData?["truncated"] == true)
                    boundedData["hint"] = "The full result is available as a resource with relation=result. Find its semantic target with common.resources_find, then read that target, or request a smaller scope.";
            }
            var bounded = new TerminalResult(result.Status,
                BoundText(result.Message, MaxToolResultMessageTokens, settings),
                data.ToString(Formatting.None), result.Resources);
            return ToolResultWire.Write(command.ToolCallId, command.ToolId, bounded, materialized.ResultResource);
        }

        public static ChatMessage CreateToolResultMessage(ToolInvocation command, TerminalResult result)
        {
            return CreateToolResultMessage(command, result, ToolResultRoles.User);
        }

        public static ChatMessage CreateToolResultMessage(ToolInvocation command, TerminalResult result, string role)
        {
            return CreateToolResultMessage(command, new ToolResultMaterialization(result), DefaultMaxToolResultDataTokens, role);
        }

        internal static ChatMessage CreateToolResultMessage(ToolInvocation command, ToolResultMaterialization result,
            string role = ToolResultRoles.User)
        {
            return CreateToolResultMessage(command, result, DefaultMaxToolResultDataTokens, role);
        }

        internal static ChatMessage CreateToolResultMessage(ToolInvocation command, ToolResultMaterialization result,
            int maxDataTokens, string role, AppSettings settings = null)
        {
            var normalizedRole = ToolResultRoles.Normalize(role);
            var native = string.Equals(normalizedRole, ToolResultRoles.Tool, StringComparison.Ordinal);
            var resultJson = BuildToolResult(command, result, maxDataTokens, settings);
            return new ChatMessage
            {
                Role = normalizedRole,
                ToolCallId = command.ToolCallId,
                ToolName = command.ToolId,
                ToolResultRole = normalizedRole,
                ToolResultProtocolVersion = ToolResultWire.CurrentVersion,
                Content = native ? resultJson : "TOOL_RESULT:\n" + resultJson,
                ProtocolMessage = true
            };
        }

        public static ChatMessage CreateToolCallMessage(
            AgentToolCall call,
            string message,
            RNAssistant.Core.Llm.LlmCompletionResult completion,
            string toolResultRole,
            AcceptedToolCallOrigin origin)
        {
            if (call == null || string.IsNullOrWhiteSpace(call.Id) || string.IsNullOrWhiteSpace(call.Name) || origin == null)
                throw new ArgumentException("Accepted runtime call identity and origin are required.");
            var normalizedRole = ToolResultRoles.Normalize(toolResultRole);
            if (string.Equals(normalizedRole, ToolResultRoles.Tool, StringComparison.Ordinal))
            {
                var nativeMessage = AgentTranscript.CreateAssistantMessage(message ?? string.Empty, completion);
                nativeMessage.ResponseProtocolVersion = AgentResponseProtocol.CurrentVersion;
                nativeMessage.ToolResultProtocolVersion = ToolResultWire.CurrentVersion;
                nativeMessage.ResponseStatus = AgentResponseStatuses.InProgress;
                nativeMessage.ToolResultRole = normalizedRole;
                nativeMessage.ToolCallId = call.Id;
                nativeMessage.AcceptedCallOrigin = origin;
                // Native history is only the matching call/result transport shape;
                // RNAssistant does not advertise a second provider function catalog.
                // Keep the exact public id visible on both the API message and local metadata.
                nativeMessage.ToolName = call.Name;
                nativeMessage.ToolCalls = new List<LlmToolCall>
                {
                    new LlmToolCall
                    {
                        Id = call.Id,
                        Type = "function",
                        Name = call.Name,
                        ArgumentsJson = JsonConvert.SerializeObject(
                            call.Arguments == null
                                ? new Dictionary<string, object>()
                                : call.Arguments)
                    }
                };
                nativeMessage.ProtocolMessage = true;
                return nativeMessage;
            }
            var content = ModelProtocolWire.Write(message, new[] { new ConversationToolCall
                {
                    Name = call.Name,
                    Arguments = JObject.FromObject(call.Arguments ?? new Dictionary<string, object>())
                } });
            var protocolMessage = AgentTranscript.CreateAssistantMessage(content, completion);
            protocolMessage.ResponseProtocolVersion = AgentResponseProtocol.CurrentVersion;
            protocolMessage.ToolResultProtocolVersion = ToolResultWire.CurrentVersion;
            protocolMessage.ResponseStatus = AgentResponseStatuses.InProgress;
            protocolMessage.ProtocolMessage = true;
            protocolMessage.ToolResultRole = normalizedRole;
            protocolMessage.ToolCallId = call.Id;
            protocolMessage.AcceptedCallOrigin = origin;
            protocolMessage.ToolName = call.Name;
            return protocolMessage;
        }

        private static JToken BoundData(JToken parsed, int maxDataTokens, AppSettings settings)
        {
            var compact = parsed.ToString(Formatting.None);
            var estimatedTokens = ModelContextBudget.EstimateTextTokens(compact, settings);
            var boundedTokens = Math.Max(0, maxDataTokens);
            if (estimatedTokens <= boundedTokens)
            {
                return parsed;
            }

            if (boundedTokens == 0) return JValue.CreateNull();

            var previewBudget = Math.Max(0, boundedTokens - 96);
            return new JObject
            {
                ["truncated"] = true,
                ["original_chars"] = compact.Length,
                ["original_estimated_tokens"] = estimatedTokens,
                ["preview"] = ModelContextBudget.TruncateText(compact, previewBudget, settings),
                ["hint"] = "The tool result was too large for the model context. Request a smaller scope."
            };
        }

        private static string BoundText(string value, int maxTokens, AppSettings settings)
        {
            var text = value ?? string.Empty;
            if (ModelContextBudget.EstimateTextTokens(text, settings) <= maxTokens) return text;
            return ModelContextBudget.TruncateText(text, Math.Max(1, maxTokens - 8), settings) + "...[truncated]";
        }

    }
}
