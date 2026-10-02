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
        public const string SchemaName = "rnassistant_conversation_response_v6";
        public const int MaximumToolCalls = 32;

        public static string Build(IEnumerable<ToolCatalogEntry> callableTools)
        {
            var options = new JArray();
            foreach (var tool in (callableTools ?? new ToolCatalogEntry[0])
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
                ["description"] = "V6: message/action/tool_calls. Runtime owns IDs, lifecycle and effects. " +
                    "Only independent local reads may be batched. Return every mutation and other call alone; wait for the result before proposing another mutation. " +
                    "Use tool for calls, continue for a short no-call progress step, done only for a completed answer, blocked or needs_input for unfinished work. Action is not execution evidence.",
                ["properties"] = new JObject
                {
                    ["message"] = new JObject { ["type"] = "string", ["description"] = "User-facing message. On tool turns, briefly connect a relevant observed finding, the purpose of the actual upcoming calls, and what their results will clarify. Omit parts not yet known; do not invent findings or narrate private reasoning. Wording never determines execution success; runtime owns status and effects." },
                    ["action"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray("tool", "continue", "done", "blocked", "needs_input"),
                        ["description"] = "tool requires calls; continue is a no-call progress step; done/blocked/needs_input end the turn with no calls."
                    },
                    ["tool_calls"] = new JObject
                    {
                        ["type"] = "array",
                        ["items"] = options.Count > 0 ? new JObject { ["anyOf"] = options } : new JObject
                        {
                            ["type"] = "object", ["properties"] = new JObject(),
                            ["required"] = new JArray(), ["additionalProperties"] = false
                        },
                        ["maxItems"] = options.Count > 0 ? MaximumToolCalls : 0,
                        ["description"] = "Calls to execute now. Only action=tool may have calls."
                    }
                },
                ["required"] = new JArray("message", "action", "tool_calls"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }
    }
}
