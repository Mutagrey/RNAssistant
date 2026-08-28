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
    // One active wire contract for model attempts, transcript envelopes and probes.
    // No version selection or historical fallback: the coordinated cutover replaces
    // the v2 implementation here with the already introduced v3 contract.
    public static class ModelProtocolWire
    {
        public static LlmRequestOptions CreateRequestOptions(string responseMode, IEnumerable<ToolDefinition> tools)
        {
            var jsonSchema = string.Equals(AgentResponseModes.Normalize(responseMode),
                AgentResponseModes.JsonSchema, StringComparison.Ordinal);
            return new LlmRequestOptions
            {
                ResponseFormat = jsonSchema ? LlmResponseFormats.JsonSchema : LlmResponseFormats.JsonObject,
                ResponseSchemaName = jsonSchema ? AgentResponseSchemaBuilder.SchemaName : null,
                ResponseSchemaJson = jsonSchema ? AgentResponseSchemaBuilder.Build(tools) : null
            };
        }

        public static AgentResponseParseResult Parse(string content, IEnumerable<ToolDefinition> callableTools,
            IEnumerable<ToolDefinition> runnableCatalog, ModelProtocolCallContext context)
        {
            // Preserve current v2 acceptance. Context is supplied by all production
            // callers for the v3 switch; v2 does not yet enforce run IDs/batch safety.
            return new AgentResponseParser().Parse(content, callableTools, runnableCatalog);
        }

        public static string Write(string message, IEnumerable<AgentToolCall> calls)
        {
            var ordered = (calls ?? new AgentToolCall[0]).ToArray();
            return new JObject
            {
                ["status"] = ordered.Length == 0 ? AgentResponseStatuses.Completed : AgentResponseStatuses.InProgress,
                ["message"] = message ?? string.Empty,
                ["tool_calls"] = new JArray(ordered.Select(call => new JObject
                {
                    ["id"] = call == null ? string.Empty : call.Id ?? string.Empty,
                    ["name"] = call == null ? string.Empty : call.Name ?? string.Empty,
                    ["arguments"] = call == null || call.Arguments == null ? new JObject() : JObject.FromObject(call.Arguments)
                }))
            }.ToString(Formatting.None);
        }
    }
}
