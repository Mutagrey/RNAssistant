using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Tools
{
    public static class AgentDecisionSchemaBuilder
    {
        public static string Build(IEnumerable<ToolDefinition> tools)
        {
            return Build(tools, true);
        }

        public static string Build(IEnumerable<ToolDefinition> tools, bool includeToolDecision)
        {
            var toolOptions = new JArray();
            foreach (var tool in includeToolDecision ? tools ?? new ToolDefinition[0] : new ToolDefinition[0])
            {
                JObject schema;
                string error;
                if (tool == null || !ToolSchemaSupport.TryNormalize(tool, out schema, out error)) continue;
                toolOptions.Add(new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["toolId"] = new JObject { ["type"] = "string", ["const"] = tool.Id },
                        ["arguments"] = ToolSchemaSupport.ForStructuredOutput(schema)
                    },
                    ["required"] = new JArray("toolId", "arguments"),
                    ["additionalProperties"] = false
                });
            }
            toolOptions.Add(new JObject { ["type"] = "null" });

            var planItem = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["id"] = new JObject { ["type"] = "string", ["description"] = "Stable short step id reused when a revised plan keeps this step." },
                    ["title"] = new JObject { ["type"] = "string", ["description"] = "Visible action title. Do not use action or expected fields." }
                },
                ["required"] = new JArray("id", "title"),
                ["additionalProperties"] = false
            };
            var schemaRoot = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["protocolVersion"] = new JObject { ["type"] = "integer", ["const"] = AgentDecisionProtocol.Version },
                    ["kind"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = includeToolDecision
                            ? new JArray(AgentResponseKinds.Plan, AgentResponseKinds.Tool, AgentResponseKinds.Clarify, AgentResponseKinds.Final, AgentResponseKinds.CannotComplete)
                            : new JArray(AgentResponseKinds.Plan, AgentResponseKinds.Clarify, AgentResponseKinds.Final, AgentResponseKinds.CannotComplete)
                    },
                    ["decisionSummary"] = new JObject { ["type"] = "string", ["description"] = "Short visible progress statement, not hidden reasoning." },
                    ["goal"] = NullableString("Visible user outcome for a plan or revised plan."),
                    ["plan"] = new JObject { ["anyOf"] = new JArray(new JObject { ["type"] = "array", ["items"] = planItem }, new JObject { ["type"] = "null" }) },
                    ["tool"] = new JObject { ["anyOf"] = toolOptions },
                    ["message"] = NullableString("User-facing terminal answer or clarification question.")
                },
                ["required"] = new JArray("protocolVersion", "kind", "decisionSummary", "goal", "plan", "tool", "message"),
                ["additionalProperties"] = false
            };
            return schemaRoot.ToString(Formatting.None);
        }

        private static JObject NullableString(string description = null)
        {
            var schema = new JObject { ["type"] = new JArray("string", "null") };
            if (!string.IsNullOrWhiteSpace(description)) schema["description"] = description;
            return schema;
        }
    }
}
