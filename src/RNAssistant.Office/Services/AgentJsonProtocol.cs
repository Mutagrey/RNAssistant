using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    internal static class AgentJsonProtocol
    {
        internal const int DefaultMaxToolResultDataTokens = 8192;
        private const int MaxToolResultMessageTokens = 512;

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
            return BuildToolResult(command, result, DefaultMaxToolResultDataTokens, null);
        }

        internal static string BuildToolResult(
            ToolCommand command,
            ToolResult result,
            int maxDataTokens,
            AppSettings settings = null)
        {
            var message = BoundText(
                result == null ? "Tool returned no result." : result.Message ?? string.Empty,
                MaxToolResultMessageTokens,
                settings);
            var root = new JObject
            {
                ["ok"] = result != null && result.Success,
                ["tool_call_id"] = command == null ? string.Empty : command.ToolCallId ?? string.Empty,
                ["name"] = command == null ? string.Empty : command.ToolId ?? string.Empty,
                ["status"] = result == null ? "failed" : result.Status ?? (result.Success ? "completed" : "failed"),
                ["message"] = message,
                ["data"] = ParseData(result == null ? null : result.DataJson, maxDataTokens, settings),
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
            var resources = (result == null ? null : result.ModelResourceRefs ?? new ResourceRef[0])
                .Where(reference => reference != null && !string.IsNullOrWhiteSpace(reference.Uri))
                .GroupBy(reference => reference.Uri + "\n" + (reference.Revision ?? string.Empty), StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            if (resources.Count > 0)
            {
                root["resources"] = new JArray(resources.Select(reference =>
                {
                    var value = new JObject { ["uri"] = reference.Uri };
                    if (!string.IsNullOrWhiteSpace(reference.Revision)) value["revision"] = reference.Revision;
                    if (SameReference(reference, result == null ? null : result.ModelResultResourceRef))
                    {
                        value["relation"] = "result";
                        if (!string.IsNullOrWhiteSpace(result.ModelResultResourceKind))
                        {
                            value["kind"] = result.ModelResultResourceKind;
                        }
                    }
                    return value;
                }));
                if (result != null && string.Equals(
                    result.ModelResultResourceKind,
                    ChatArtifactKinds.Chart,
                    StringComparison.OrdinalIgnoreCase))
                {
                    root["data"] = new JObject
                    {
                        ["externalized"] = true,
                        ["kind"] = ChatArtifactKinds.Chart,
                        ["original_chars"] = (result.DataJson ?? string.Empty).Length,
                        ["hint"] = "The chart body is available through the exact resource with relation=result."
                    };
                }
                var boundedData = root["data"] as JObject;
                if ((bool?)boundedData?["truncated"] == true)
                {
                    boundedData["hint"] =
                        "The full result is available through the resource with relation=result. Read its exact URI with common.resources_read, or request a smaller scope.";
                }
            }
            return root.ToString(Formatting.None);
        }

        public static ChatMessage CreateToolResultMessage(ToolCommand command, ToolResult result)
        {
            return CreateToolResultMessage(command, result, DefaultMaxToolResultDataTokens, ToolResultRoles.User);
        }

        public static ChatMessage CreateToolResultMessage(ToolCommand command, ToolResult result, string role)
        {
            return CreateToolResultMessage(command, result, DefaultMaxToolResultDataTokens, role);
        }

        internal static ChatMessage CreateToolResultMessage(
            ToolCommand command,
            ToolResult result,
            int maxDataTokens,
            string role,
            AppSettings settings = null)
        {
            var normalizedRole = ToolResultRoles.Normalize(role);
            var resultJson = BuildToolResult(command, result, maxDataTokens, settings);
            if (string.Equals(normalizedRole, ToolResultRoles.Tool, StringComparison.Ordinal))
            {
                return new ChatMessage
                {
                    Role = ToolResultRoles.Tool,
                    ToolCallId = command == null ? string.Empty : command.ToolCallId ?? string.Empty,
                    ToolName = ApiToolName(command == null ? null : command.ToolId),
                    ToolResultRole = ToolResultRoles.Tool,
                    Content = resultJson,
                    ProtocolMessage = true
                };
            }
            return new ChatMessage
            {
                Role = normalizedRole,
                ToolCallId = command == null ? string.Empty : command.ToolCallId ?? string.Empty,
                ToolName = command == null ? string.Empty : command.ToolId ?? string.Empty,
                ToolResultRole = normalizedRole,
                Content = "TOOL_RESULT:\n" + resultJson,
                ProtocolMessage = true
            };
        }

        internal static void FailClosedOversizedCapabilityEvidence(
            ToolCommand command,
            ToolResult result,
            int maxDataTokens,
            AppSettings settings = null)
        {
            if (command == null || result == null || !result.Success ||
                !string.Equals(command.ToolId, CapabilityDiscoveryExecutor.ReadToolId, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(result.DataJson)) return;

            JObject data;
            try { data = JObject.Parse(result.DataJson); }
            catch (JsonException) { return; }
            var kind = (string)data["kind"] ?? string.Empty;
            var isCoreEvidence = (string.Equals(kind, "tool-schema", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kind, "skill", StringComparison.OrdinalIgnoreCase)) &&
                (bool?)data["loaded"] == true &&
                (bool?)data["complete"] == true &&
                (bool?)data["truncated"] == false;
            if (!isCoreEvidence) return;

            var compact = data.ToString(Formatting.None);
            var estimatedTokens = ModelContextBudget.EstimateTextTokens(compact, settings);
            if (estimatedTokens <= Math.Max(0, maxDataTokens)) return;

            result.Success = false;
            result.Status = "failed";
            result.ErrorCode = "capability_evidence_context_too_large";
            result.Retryable = false;
            result.Message =
                "Capability was found but its complete evidence did not fit the remaining model context, so it was not loaded. Reduce context or start a new chat; do not retry unchanged.";
            result.DataJson = new JObject
            {
                ["kind"] = kind,
                ["id"] = data["id"] == null ? JValue.CreateNull() : data["id"].DeepClone(),
                ["revision"] = data["revision"] == null ? JValue.CreateNull() : data["revision"].DeepClone(),
                ["loaded"] = false,
                ["complete"] = false,
                ["truncated"] = true,
                ["original_chars"] = compact.Length,
                ["original_estimated_tokens"] = estimatedTokens,
                ["available_tokens"] = Math.Max(0, maxDataTokens)
            }.ToString(Formatting.None);
        }

        public static ChatMessage CreateToolCallMessage(
            AgentToolCall call,
            string message,
            RNAssistant.Core.Llm.LlmCompletionResult completion,
            string toolResultRole)
        {
            var normalizedRole = ToolResultRoles.Normalize(toolResultRole);
            if (string.Equals(normalizedRole, ToolResultRoles.Tool, StringComparison.Ordinal))
            {
                var nativeMessage = AgentTranscript.CreateAssistantMessage(message ?? string.Empty, completion);
                nativeMessage.ResponseProtocolVersion = AgentResponseProtocol.CurrentVersion;
                nativeMessage.ResponseStatus = AgentResponseStatuses.InProgress;
                nativeMessage.ToolResultRole = normalizedRole;
                nativeMessage.ToolCallId = call == null ? string.Empty : call.Id ?? string.Empty;
                // ToolCalls keeps the provider-safe name; ToolName is local replay metadata and preserves the canonical id.
                nativeMessage.ToolName = call == null ? string.Empty : call.Name ?? string.Empty;
                nativeMessage.ToolCalls = new List<LlmToolCall>
                {
                    new LlmToolCall
                    {
                        Id = call == null ? string.Empty : call.Id ?? string.Empty,
                        Type = "function",
                        Name = ApiToolName(call == null ? null : call.Name),
                        ArgumentsJson = JsonConvert.SerializeObject(
                            call == null || call.Arguments == null
                                ? new Dictionary<string, object>()
                                : call.Arguments)
                    }
                };
                nativeMessage.ProtocolMessage = true;
                return nativeMessage;
            }
            var content = ModelProtocolWire.Write(message, new[] { call });
            var protocolMessage = AgentTranscript.CreateAssistantMessage(content, completion);
            protocolMessage.ResponseProtocolVersion = AgentResponseProtocol.CurrentVersion;
            protocolMessage.ResponseStatus = AgentResponseStatuses.InProgress;
            protocolMessage.ProtocolMessage = true;
            protocolMessage.ToolResultRole = normalizedRole;
            protocolMessage.ToolCallId = call == null ? string.Empty : call.Id ?? string.Empty;
            protocolMessage.ToolName = call == null ? string.Empty : call.Name ?? string.Empty;
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

        private static JToken ParseData(string dataJson, int maxDataTokens, AppSettings settings)
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

        private static bool SameReference(ResourceRef left, ResourceRef right)
        {
            return left != null && right != null &&
                string.Equals(left.Uri, right.Uri, StringComparison.Ordinal) &&
                string.Equals(left.Revision ?? string.Empty, right.Revision ?? string.Empty, StringComparison.Ordinal);
        }
    }
}
