using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.ModelProtocol
{
    public static partial class ModelProtocolWire
    {
        // Wire-only projection: retain the complete frozen prompt for explicit
        // json_object fallback. Never rewrite user/history or accepted responses.
        public static IReadOnlyList<ChatMessage> ProjectSchemaGuidance(IReadOnlyList<ChatMessage> messages)
        {
            var result = messages.ToArray();
            for (var index = 0; index < result.Length; index++)
            {
                var source = result[index];
                if (source == null || source.ExcludeFromModelContext || source.AttachmentAnalysis != null ||
                    !string.IsNullOrEmpty(source.ToolCallId) || source.Attachments?.Count > 0 ||
                    source.ResourceEvidence?.Count > 0 || source.ContextClaims?.Count > 0 || source.ToolCalls?.Count > 0 || (index != 0 && !(source.ProtocolMessage &&
                    (source.Content ?? string.Empty).StartsWith("FORMAT_REPAIR:\n", StringComparison.Ordinal)))) continue;
                var content = source.Content ?? string.Empty;
                // Repair stores its instruction inside JSON, so replace the exact
                // escaped canonical fragment as well as ordinary prompt prose.
                var escaped = Newtonsoft.Json.JsonConvert.ToString(JsonObjectGuidance);
                var replacement = Newtonsoft.Json.JsonConvert.ToString(SchemaGuidance);
                var projected = content.Replace(JsonObjectGuidance, SchemaGuidance)
                    .Replace(escaped.Substring(1, escaped.Length - 2), replacement.Substring(1, replacement.Length - 2));
                if (projected == content) continue;
                result[index] = new ChatMessage { Id = source.Id, Role = source.Role, Content = projected,
                    ProtocolMessage = source.ProtocolMessage, CreatedUtc = source.CreatedUtc,
                    RunId = source.RunId, Sequence = source.Sequence };
            }
            return result;
        }

        public static string RuntimeFeedback(AgentModelRequest request)
        {
            if (!request.ReflectionLimit.HasValue) return request.CompletionFeedback;
            var remaining = Math.Max(0, request.ReflectionLimit.Value - request.ReflectionsUsed);
            var budget = "REFLECTION_BUDGET: " + request.ReflectionsUsed + " accepted, " + remaining +
                " remaining before a new terminal tool result or user input. " +
                (remaining == 0 ? "Choose execute or finish now. Reflection is unavailable until new input or a terminal tool result. " :
                    "Reflection is optional: after choosing an approach, execute the next useful step. ") +
                "Only accepted reflections count; format repairs and network retries do not. The overall iteration limit still applies.";
            return string.IsNullOrWhiteSpace(request.CompletionFeedback) ? budget : request.CompletionFeedback + "\n" + budget;
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
