using System;
using System.Collections.Generic;
using System.Linq;
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

        public static string BuildToolResult(ToolCommand command, TerminalResult result)
        {
            return BuildToolResult(command, new ToolResultMaterialization(result));
        }

        internal static string BuildToolResult(ToolCommand command, TerminalResult result,
            int maxDataTokens, AppSettings settings = null)
        {
            return BuildToolResult(command, new ToolResultMaterialization(result), maxDataTokens, settings);
        }

        internal static string BuildToolResult(ToolCommand command, ToolResultMaterialization materialized,
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
                var boundedData = data as JObject;
                if ((bool?)boundedData?["truncated"] == true)
                    boundedData["hint"] = "The full result is available through the resource with relation=result. Read its exact URI with common.resources_read, or request a smaller scope.";
            }
            var bounded = new TerminalResult(result.Status,
                BoundText(result.Message, MaxToolResultMessageTokens, settings),
                data.ToString(Formatting.None), result.Resources);
            return ToolResultWire.Write(command.ToolCallId, command.ToolId, bounded, materialized.ResultResource);
        }

        public static ChatMessage CreateToolResultMessage(ToolCommand command, TerminalResult result)
        {
            return CreateToolResultMessage(command, result, ToolResultRoles.User);
        }

        public static ChatMessage CreateToolResultMessage(ToolCommand command, TerminalResult result, string role)
        {
            return CreateToolResultMessage(command, new ToolResultMaterialization(result), DefaultMaxToolResultDataTokens, role);
        }

        internal static ChatMessage CreateToolResultMessage(ToolCommand command, ToolResultMaterialization result,
            string role = ToolResultRoles.User)
        {
            return CreateToolResultMessage(command, result, DefaultMaxToolResultDataTokens, role);
        }

        internal static ChatMessage CreateToolResultMessage(ToolCommand command, ToolResultMaterialization result,
            int maxDataTokens, string role, AppSettings settings = null)
        {
            var normalizedRole = ToolResultRoles.Normalize(role);
            var native = string.Equals(normalizedRole, ToolResultRoles.Tool, StringComparison.Ordinal);
            var resultJson = BuildToolResult(command, result, maxDataTokens, settings);
            return new ChatMessage
            {
                Role = normalizedRole,
                ToolCallId = command.ToolCallId,
                ToolName = native ? ApiToolName(command.ToolId) : command.ToolId,
                ToolResultRole = normalizedRole,
                ToolResultProtocolVersion = ToolResultWire.CurrentVersion,
                Content = native ? resultJson : "TOOL_RESULT:\n" + resultJson,
                ProtocolMessage = true
            };
        }

        internal static void FailClosedOversizedCapabilityEvidence(ToolCommand command,
            ToolResultMaterialization materialized, int maxDataTokens, AppSettings settings = null)
        {
            var result = materialized == null ? null : materialized.Result;
            if (command == null || result == null || result.Status != ToolResultStatus.Ok ||
                !string.Equals(command.ToolId, CapabilityDiscoveryExecutor.ReadToolId, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(result.DataJson)) return;

            JObject data;
            try
            {
                data = JsonConvert.DeserializeObject<JObject>(result.DataJson,
                    new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
            }
            catch (JsonException) { return; }
            if (data == null) return;
            var kind = (string)data["kind"] ?? string.Empty;
            var isCoreEvidence = (string.Equals(kind, "tool-schema", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kind, "skill", StringComparison.OrdinalIgnoreCase)) &&
                (bool?)data["loaded"] == true && (bool?)data["complete"] == true && (bool?)data["truncated"] == false;
            if (!isCoreEvidence) return;

            var compact = data.ToString(Formatting.None);
            var estimatedTokens = ModelContextBudget.EstimateTextTokens(compact, settings);
            if (estimatedTokens <= Math.Max(0, maxDataTokens)) return;

            materialized.ReplaceResult(TerminalResult.Error(
                "Capability was found but its complete evidence did not fit the remaining model context, so it was not loaded. Reduce context or start a new chat; do not retry unchanged.",
                new JObject
                {
                    ["code"] = "capability_evidence_context_too_large",
                    ["kind"] = kind,
                    ["id"] = data["id"] == null ? JValue.CreateNull() : data["id"].DeepClone(),
                    ["revision"] = data["revision"] == null ? JValue.CreateNull() : data["revision"].DeepClone(),
                    ["loaded"] = false,
                    ["complete"] = false,
                    ["truncated"] = true,
                    ["original_chars"] = compact.Length,
                    ["original_estimated_tokens"] = estimatedTokens,
                    ["available_tokens"] = Math.Max(0, maxDataTokens)
                }.ToString(Formatting.None), result.Resources));
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
                // ToolCalls keeps the provider-safe name; ToolName is local replay metadata and preserves the canonical id.
                nativeMessage.ToolName = call.Name;
                nativeMessage.ToolCalls = new List<LlmToolCall>
                {
                    new LlmToolCall
                    {
                        Id = call.Id,
                        Type = "function",
                        Name = ApiToolName(call.Name),
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
                { Name = call.Name, Arguments = call.Arguments } });
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

        internal static string ApiToolName(string toolId)
        {
            var source = string.IsNullOrWhiteSpace(toolId) ? "tool" : toolId;
            var chars = source.Select(character =>
                (character >= 'a' && character <= 'z' ||
                 character >= 'A' && character <= 'Z' ||
                 character >= '0' && character <= '9' ||
                 character == '_' || character == '-')
                    ? character
                    : '_').ToArray();
            var value = "rna_" + new string(chars);
            return value.Length <= 64 ? value : value.Substring(0, 64);
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
