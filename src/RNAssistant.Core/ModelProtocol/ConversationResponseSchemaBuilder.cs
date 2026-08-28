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
        public const string SchemaName = "rnassistant_conversation_response_v3";
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
                        ["id"] = new JObject { ["type"] = "string", ["minLength"] = 1,
                            ["description"] = "Unique tool call id across the accepted run, not just this response." },
                        ["name"] = new JObject { ["type"] = "string", ["const"] = tool.Id },
                        ["arguments"] = ToolSchemaSupport.ForStructuredOutput(parameters)
                    },
                    ["required"] = new JArray("id", "name", "arguments"),
                    ["additionalProperties"] = false
                });
            }
            return new JObject
            {
                ["type"] = "object",
                ["description"] = "Conversation response v3. Return only message and tool_calls. Runtime owns lifecycle and execution health. " +
                    "Write, external and confirmation-required calls must be singleton; only independent read-only calls may be batched, in sequence.",
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
                        ["description"] = "Exact calls to execute now, or [] when the model ends its loop. This is not evidence of successful effects."
                    }
                },
                ["required"] = new JArray("message", "tool_calls"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }
    }
}
