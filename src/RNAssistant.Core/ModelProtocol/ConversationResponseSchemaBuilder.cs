using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.ModelProtocol
{
    public static class ConversationResponseSchemaBuilder
    {
        public const string SchemaName = "rnassistant_conversation_response_v7";
        public const int MaximumToolCalls = 32;

        public static string Build(IEnumerable<ToolCatalogEntry> callableTools, ModelProtocolCallContext context)
        {
            if (context == null || !context.IsComplete)
                throw new System.ArgumentException("V7 schema requires a complete local sequential-batch context.", nameof(context));
            var reads = new JArray();
            var singleCalls = new JArray();
            foreach (var tool in (callableTools ?? new ToolCatalogEntry[0])
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Id))
                .GroupBy(item => item.Id, System.StringComparer.Ordinal).Select(group => group.First()))
            {
                JObject parameters;
                string error;
                if (!ToolSchemaSupport.TryParse(tool, out parameters, out error)) continue;
                var options = context.AllowsSequentialBatch(tool) ? reads : singleCalls;
                options.Add(new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["name"] = new JObject { ["type"] = "string", ["const"] = tool.Id },
                        ["arguments"] = ToolSchemaSupport.ForStructuredOutput(parameters)
                    },
                    ["required"] = new JArray("name", "arguments"),
                    ["additionalProperties"] = false
                });
            }
            // Disjoint call catalogs keep every argument contract exactly once.
            // Mixed read/mutation arrays cannot satisfy either branch.
            var calls = reads.Count > 0 && singleCalls.Count > 0
                ? new JObject { ["anyOf"] = new JArray(CallArray(singleCalls, 1), CallArray(reads, MaximumToolCalls)) }
                : reads.Count > 0 ? CallArray(reads, MaximumToolCalls) : CallArray(singleCalls, singleCalls.Count > 0 ? 1 : 0);
            calls["description"] = "Calls to execute now. Only action=tool may have calls.";
            return new JObject
            {
                ["type"] = "object",
                ["description"] = "V7: message/action/tool_calls. Runtime owns IDs, lifecycle and effects. " +
                    "Only independent local reads may be batched. Return every mutation and other call alone; wait for the result before proposing another mutation. " +
                    ModelProtocolWire.ActionGuidance,
                ["properties"] = new JObject
                {
                    ["message"] = new JObject { ["type"] = "string", ["description"] = ModelProtocolWire.MessageGuidance },
                    ["action"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray("tool", "done", "blocked", "needs_input"),
                        ["description"] = "tool requires calls; done/blocked/needs_input end the turn with no calls."
                    },
                    ["tool_calls"] = calls
                },
                ["required"] = new JArray("message", "action", "tool_calls"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }

        private static JObject CallArray(JArray options, int maxItems)
        {
            return new JObject
            {
                ["type"] = "array",
                ["items"] = options.Count > 0 ? new JObject { ["anyOf"] = options } : new JObject
                {
                    ["type"] = "object", ["properties"] = new JObject(),
                    ["required"] = new JArray(), ["additionalProperties"] = false
                },
                ["maxItems"] = maxItems
            };
        }
    }
}
