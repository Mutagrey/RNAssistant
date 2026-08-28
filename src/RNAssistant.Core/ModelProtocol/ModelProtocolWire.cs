using System;
using System.Collections.Generic;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.ModelProtocol
{
    // One active wire contract for model attempts, transcript envelopes and probes.
    // No version selection or historical fallback: all active responses use v3.
    public static class ModelProtocolWire
    {
        public static LlmRequestOptions CreateRequestOptions(string responseMode, IEnumerable<ToolDefinition> tools)
        {
            var jsonSchema = string.Equals(AgentResponseModes.Normalize(responseMode),
                AgentResponseModes.JsonSchema, StringComparison.Ordinal);
            return new LlmRequestOptions
            {
                ResponseFormat = jsonSchema ? LlmResponseFormats.JsonSchema : LlmResponseFormats.JsonObject,
                ResponseSchemaName = jsonSchema ? ConversationResponseSchemaBuilder.SchemaName : null,
                ResponseSchemaJson = jsonSchema ? ConversationResponseSchemaBuilder.Build(tools) : null
            };
        }

        public static ConversationResponseParseResult Parse(string content, IEnumerable<ToolDefinition> callableTools,
            IEnumerable<ToolDefinition> runnableCatalog, ModelProtocolCallContext context)
        {
            return new ConversationResponseParser().Parse(content, callableTools, runnableCatalog, context);
        }

        public static string Write(string message, IEnumerable<AgentToolCall> calls)
        {
            return new ConversationResponse(message ?? string.Empty, calls ?? new AgentToolCall[0]).ToJson();
        }
    }
}
