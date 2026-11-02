using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.ModelProtocol
{
    public static partial class ModelProtocolWire
    {
        // Wire-only instruction projection for the selected output format.
        // Never rewrite user/history or quoted rejected responses.
        public static IReadOnlyList<ChatMessage> ProjectGuidance(IReadOnlyList<ChatMessage> messages,
            bool schemaTransport = true, bool reflectionEnabled = true, string toolResultRole = ToolResultRoles.User)
        {
            if (!schemaTransport && reflectionEnabled) return messages;
            var result = messages.ToArray();
            for (var index = 0; index < result.Length; index++)
            {
                var source = result[index];
                var content = source?.Content ?? string.Empty;
                if (source == null || source.ExcludeFromModelContext || source.AttachmentAnalysis != null ||
                    !string.IsNullOrEmpty(source.ToolCallId) || source.Attachments?.Count > 0 ||
                    source.ResourceEvidence?.Count > 0 || source.ContextClaims?.Count > 0 || source.ToolCalls?.Count > 0 ||
                    (!(index == 0 && (source.Role == "system" || source.Role == "developer" ||
                        source.Role == "user" && content.StartsWith(RNAssistant.Core.Services.RuntimeContextPrompt.AgentHeader, StringComparison.Ordinal))) && !(source.ProtocolMessage &&
                    ReadServiceMessage(source, "format_repair") != null))) continue;
                // Owned instruction and runtime/history are separate messages.
                // A repair also contains untrusted rejected content. Project only
                // its owned instruction, never matching prose inside that content.
                var repair = source.ProtocolMessage && ReadServiceMessage(source, "format_repair") != null
                    ? ReadServiceMessage(source, "format_repair") : null;
                var instruction = repair == null ? content : (string)repair["instruction"];
                var projected = repair == null
                    ? instruction.Replace(JsonObjectGuidance, ResponseGuidance(schemaTransport, reflectionEnabled))
                        .Replace(MessageGuidance, MessageGuidanceFor(reflectionEnabled))
                    : instruction.Replace(RepairResponseGuidance(false, true), RepairResponseGuidance(schemaTransport, reflectionEnabled));
                if (repair != null)
                {
                    if (projected == instruction) continue;
                    repair["instruction"] = projected;
                    projected = repair.ToString(Formatting.None);
                }
                if (projected == content) continue;
                result[index] = new ChatMessage { Id = source.Id, Role = source.Role, Content = projected,
                    ProtocolMessage = source.ProtocolMessage, CreatedUtc = source.CreatedUtc,
                    RunId = source.RunId, Sequence = source.Sequence };
            }
            return result;
        }

        public static JObject ReadServiceMessage(ChatMessage message, string type)
        {
            if (message == null || !message.ProtocolMessage || message.Activity != null) return null;
            try
            {
                var value = JObject.Parse(message.Content ?? "");
                return value["type"]?.Type == JTokenType.String && (string)value["type"] == type ? value : null;
            }
            catch (JsonException) { return null; }
        }

        public static string RuntimeFeedback(AgentModelRequest request)
        {
            var feedback = new JObject { ["type"] = "decision_rejected" };
            if (!string.IsNullOrWhiteSpace(request.CompletionFeedback)) feedback["reason"] = request.CompletionFeedback;
            else feedback["type"] = "runtime_context";
            if (request.ReflectionLimit.HasValue)
            {
                feedback["reflections_remaining"] = Math.Max(0, request.ReflectionLimit.Value - request.ReflectionsUsed);
                feedback["instruction"] = "Use reflection only when useful and within the remaining allowance; otherwise execute or finish.";
            }
            return feedback.Count == 1 ? null : feedback.ToString(Formatting.None);
        }

        public static LlmRequestOptions CreateRequestOptions(string responseMode, IEnumerable<ToolCatalogEntry> tools,
            ModelProtocolCallContext context)
        {
            var jsonSchema = string.Equals(AgentResponseModes.Normalize(responseMode),
                AgentResponseModes.JsonSchema, StringComparison.Ordinal);
            return new LlmRequestOptions
            {
                ResponseFormat = jsonSchema ? LlmResponseFormats.JsonSchema : LlmResponseFormats.JsonObject,
                ResponseSchemaName = jsonSchema ? ConversationResponseSchemaBuilder.SchemaName : null,
                ResponseSchemaJson = jsonSchema ? ConversationResponseSchemaBuilder.Build(tools, context) : null
            };
        }

    }
}
