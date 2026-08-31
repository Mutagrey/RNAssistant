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
        public const string SchemaName = "rnassistant_conversation_response_v4";
        public const int MaximumToolCalls = 32;

        public static string Build(IEnumerable<ToolDefinition> callableTools)
        {
            var options = new JArray();
            foreach (var tool in (callableTools ?? new ToolDefinition[0])
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Id))
                .GroupBy(item => item.Id, System.StringComparer.Ordinal).Select(group => group.First()))
            {
                JObject parameters;
                string error;
                if (!ToolSchemaSupport.TryParse(tool, out parameters, out error)) continue;
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
            return new JObject
            {
                ["type"] = "object",
                ["description"] = "V4: only message/tool_calls and name/arguments. Runtime owns IDs, lifecycle and effects. " +
                    "Writes, external, confirmation-required and unclassified calls are singleton; batch only independent reads. " +
                    "Before [], check every requested deliverable against tool results; intermediate success is not completion.",
                ["properties"] = new JObject
                {
                    ["message"] = new JObject { ["type"] = "string", ["description"] = "User-facing message; its wording does not determine execution success." },
                    ["tool_calls"] = new JObject
                    {
                        ["type"] = "array",
                        ["items"] = options.Count > 0 ? new JObject { ["anyOf"] = options } : new JObject
                        {
                            ["type"] = "object", ["properties"] = new JObject(),
                            ["required"] = new JArray(), ["additionalProperties"] = false
                        },
                        ["maxItems"] = options.Count > 0 ? MaximumToolCalls : 0,
                        ["description"] = "Calls to execute now. Use [] only when all requested deliverables are complete or blocked; [] proves no effect."
                    }
                },
                ["required"] = new JArray("message", "tool_calls"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }
    }
}
