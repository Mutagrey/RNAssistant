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
        public static IReadOnlyList<ChatMessage> ProjectGuidance(IReadOnlyList<ChatMessage> messages,
            bool schemaTransport = true, bool reflectionEnabled = true)
        {
            if (!schemaTransport && reflectionEnabled) return messages;
            var result = messages.ToArray();
            for (var index = 0; index < result.Length; index++)
            {
                var source = result[index];
                var content = source?.Content ?? string.Empty;
                var contextStart = content.IndexOf("\n\nRUNTIME_CONTEXT:\n", StringComparison.Ordinal);
                if (source == null || source.ExcludeFromModelContext || source.AttachmentAnalysis != null ||
                    !string.IsNullOrEmpty(source.ToolCallId) || source.Attachments?.Count > 0 ||
                    source.ResourceEvidence?.Count > 0 || source.ContextClaims?.Count > 0 || source.ToolCalls?.Count > 0 ||
                    (!(index == 0 && (source.Role == "system" || source.Role == "developer" ||
                        source.Role == "user" && contextStart >= 0)) && !(source.ProtocolMessage &&
                    content.StartsWith("FORMAT_REPAIR:\n", StringComparison.Ordinal)))) continue;
                // Office can put the owned instruction in the user role. Preserve
                // the runtime context and any USER_REQUEST suffix verbatim.
                var instruction = contextStart >= 0 && index == 0 ? content.Substring(0, contextStart) : content;
                // Repair stores its instruction inside JSON, so replace the exact
                // escaped canonical fragment as well as ordinary prompt prose.
                var projected = ReplaceGuidance(instruction, JsonObjectGuidance, ResponseGuidance(schemaTransport, reflectionEnabled));
                projected = ReplaceGuidance(projected, MessageGuidance, MessageGuidanceFor(reflectionEnabled));
                if (instruction.Length < content.Length) projected += content.Substring(instruction.Length);
                if (projected == content) continue;
                result[index] = new ChatMessage { Id = source.Id, Role = source.Role, Content = projected,
                    ProtocolMessage = source.ProtocolMessage, CreatedUtc = source.CreatedUtc,
                    RunId = source.RunId, Sequence = source.Sequence };
            }
            return result;
        }

        private static string ReplaceGuidance(string content, string original, string replacement)
        {
            var escapedOriginal = Newtonsoft.Json.JsonConvert.ToString(original);
            var escapedReplacement = Newtonsoft.Json.JsonConvert.ToString(replacement);
            return content.Replace(original, replacement).Replace(
                escapedOriginal.Substring(1, escapedOriginal.Length - 2),
                escapedReplacement.Substring(1, escapedReplacement.Length - 2));
        }

        public static string RuntimeFeedback(AgentModelRequest request)
        {
            if (!request.ReflectionLimit.HasValue) return request.CompletionFeedback;
            var remaining = Math.Max(0, request.ReflectionLimit.Value - request.ReflectionsUsed);
            var budget = "REFLECTION_BUDGET: " + request.ReflectionsUsed + " accepted, " + remaining +
                " remaining before a new terminal tool result or user input. " +
                (request.ReflectionLimit.Value == 0 ? "Reflection is disabled for this run. Choose execute or finish now; new input or tool results do not enable reflection. " :
                    remaining == 0 ? "Choose execute or finish now. Reflection is unavailable until new input or a terminal tool result. " :
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
