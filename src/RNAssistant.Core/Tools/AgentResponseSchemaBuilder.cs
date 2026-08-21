using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Tools
{
    public static class AgentResponseSchemaBuilder
    {
        public const string SchemaName = "rnassistant_agent_response";

        public static string Build(IEnumerable<ToolDefinition> tools)
        {
            var callOptions = new JArray();
            foreach (var tool in tools ?? new ToolDefinition[0])
            {
                JObject parameters;
                string error;
                if (tool == null || !ToolSchemaSupport.TryParse(tool, out parameters, out error)) continue;
                callOptions.Add(new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["id"] = new JObject
                        {
                            ["type"] = "string",
                            ["minLength"] = 1,
                            ["description"] = "Unique tool call id within this response."
                        },
                        ["name"] = new JObject { ["type"] = "string", ["const"] = tool.Id },
                        ["arguments"] = ToolSchemaSupport.ForStructuredOutput(parameters)
                    },
                    ["required"] = new JArray("id", "name", "arguments"),
                    ["additionalProperties"] = false
                });
            }

            var callItems = callOptions.Count > 0
                ? new JObject { ["anyOf"] = callOptions }
                : new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject(),
                    ["required"] = new JArray(),
                    ["additionalProperties"] = false
                };
            var root = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["message"] = new JObject
                    {
                        ["type"] = "string",
                        ["minLength"] = 1,
                        ["description"] = "Visible progress for a tool turn or the user-facing final answer."
                    },
                    ["tool_calls"] = new JObject
                    {
                        ["type"] = "array",
                        ["items"] = callItems,
                        ["maxItems"] = callOptions.Count > 0 ? 32 : 0
                    }
                },
                ["required"] = new JArray("message", "tool_calls"),
                ["additionalProperties"] = false
            };
            return root.ToString(Formatting.None);
        }
    }
}
