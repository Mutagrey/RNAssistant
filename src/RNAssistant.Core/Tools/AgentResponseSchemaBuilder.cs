using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Tools
{
    public static class AgentResponseSchemaBuilder
    {
        public const string SchemaName = "rnassistant_conversation_response_v2";
        public const int MaximumToolCalls = 32;

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
            var statuses = new JArray(
                AgentResponseStatuses.Completed,
                AgentResponseStatuses.AwaitingUser,
                AgentResponseStatuses.Blocked,
                AgentResponseStatuses.Refused);
            if (callOptions.Count > 0) statuses.Add(AgentResponseStatuses.InProgress);
            var root = new JObject
            {
                ["type"] = "object",
                ["description"] = "Conversation response v2. Choose tool_calls first, then set status: in_progress requires one or more calls; every terminal status requires no calls. Cross-field consistency is also enforced locally.",
                ["properties"] = new JObject
                {
                    ["message"] = new JObject
                    {
                        ["type"] = "string",
                        ["minLength"] = 1,
                        ["description"] = "User-facing progress, answer, clarification, blocker, or refusal. Its wording never determines status."
                    },
                    ["tool_calls"] = new JObject
                    {
                        ["type"] = "array",
                        ["items"] = callItems,
                        ["description"] = "Exact actions to execute now. Must be non-empty for in_progress and empty for every terminal status.",
                        ["maxItems"] = callOptions.Count > 0 ? MaximumToolCalls : 0
                    },
                    ["status"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = statuses,
                        ["description"] = callOptions.Count > 0
                            ? "Explicit run state chosen after tool_calls. Use in_progress only with calls; otherwise use completed, awaiting_user, blocked, or refused."
                            : "Explicit terminal run state. in_progress is unavailable because this request has no callable tools."
                    }
                },
                ["required"] = new JArray("message", "tool_calls", "status"),
                ["additionalProperties"] = false
            };
            return root.ToString(Formatting.None);
        }
    }
}
