using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Tools
{
    public sealed class AgentPlannerResponseParser
    {
        public AgentPlannerParseResult Parse(string text)
        {
            return Parse(text, null);
        }

        public AgentPlannerParseResult Parse(string text, IEnumerable<ToolDefinition> tools)
        {
            if (string.IsNullOrWhiteSpace(text)) return AgentPlannerParseResult.Fail("empty_response", "Agent decision is empty.");
            var trimmed = text.TrimStart('\uFEFF').Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal) || !trimmed.EndsWith("}", StringComparison.Ordinal))
            {
                return AgentPlannerParseResult.Fail("not_json_object", "Agent decision must be exactly one JSON object.");
            }

            JObject obj;
            try { obj = JObject.Parse(trimmed); }
            catch (JsonException ex) { return AgentPlannerParseResult.Fail("invalid_json", ex.Message); }

            var allowed = new HashSet<string>(new[] { "protocolVersion", "kind", "decisionSummary", "goal", "plan", "tool", "message" }, StringComparer.Ordinal);
            var extra = obj.Properties().FirstOrDefault(property => !allowed.Contains(property.Name));
            if (extra != null) return AgentPlannerParseResult.Fail("unexpected_field", "Agent decision contains unsupported field: " + extra.Name);

            if (obj["protocolVersion"] == null || obj["protocolVersion"].Type != JTokenType.Integer || obj["protocolVersion"].Value<int>() != AgentDecisionProtocol.Version)
            {
                return AgentPlannerParseResult.Fail("invalid_protocol_version", "protocolVersion must be 1.");
            }
            var kind = ReadString(obj["kind"]);
            if (!KnownKind(kind)) return AgentPlannerParseResult.Fail("invalid_kind", "Agent decision kind is invalid.");
            var summary = ReadString(obj["decisionSummary"]);
            if (string.IsNullOrWhiteSpace(summary)) return AgentPlannerParseResult.Fail("missing_decision_summary", "decisionSummary is required.");
            var missing = allowed.FirstOrDefault(field => obj[field] == null);
            if (missing != null) return AgentPlannerParseResult.Fail("missing_field", "Agent decision is missing required field: " + missing);

            var response = new AgentPlannerResponse
            {
                ProtocolVersion = AgentDecisionProtocol.Version,
                Kind = kind,
                DecisionSummary = summary,
                Goal = ReadString(obj["goal"]),
                Message = ReadString(obj["message"])
            };

            if (string.Equals(kind, AgentResponseKinds.Plan, StringComparison.OrdinalIgnoreCase))
            {
                if (!IsNull(obj["tool"]) || !IsNull(obj["message"]))
                {
                    return AgentPlannerParseResult.Fail("invalid_plan", "plan requires tool and message to be null.");
                }
                var plan = obj["plan"] as JArray;
                if (string.IsNullOrWhiteSpace(response.Goal) || plan == null || plan.Count == 0)
                {
                    return AgentPlannerParseResult.Fail("invalid_plan", "plan requires a goal and at least one step.");
                }
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var token in plan)
                {
                    var step = token as JObject;
                    if (step == null || step.Properties().Any(property => property.Name != "id" && property.Name != "title"))
                    {
                        return AgentPlannerParseResult.Fail("invalid_plan_step", "Each plan step may contain only id and title.");
                    }
                    var id = ReadString(step["id"]);
                    var title = ReadString(step["title"]);
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) || !ids.Add(id))
                    {
                        return AgentPlannerParseResult.Fail("invalid_plan_step", "Plan step id/title must be non-empty and ids must be unique.");
                    }
                    response.Plan.Add(new AgentPlanStep { Id = id, Title = title, Status = "pending" });
                }
                return AgentPlannerParseResult.Ok(response);
            }

            if (string.Equals(kind, AgentResponseKinds.Tool, StringComparison.OrdinalIgnoreCase))
            {
                if (!IsNull(obj["goal"]) || !IsNull(obj["plan"]) || !IsNull(obj["message"]))
                {
                    return AgentPlannerParseResult.Fail("invalid_tool", "tool requires goal, plan, and message to be null.");
                }
                var toolObject = obj["tool"] as JObject;
                if (toolObject == null || toolObject.Properties().Any(property => property.Name != "toolId" && property.Name != "arguments"))
                {
                    return AgentPlannerParseResult.Fail("invalid_tool", "tool decision requires a tool object with toolId and arguments.");
                }
                var toolId = ReadString(toolObject["toolId"]);
                var arguments = toolObject["arguments"] as JObject;
                if (string.IsNullOrWhiteSpace(toolId) || arguments == null)
                {
                    return AgentPlannerParseResult.Fail("invalid_tool", "toolId and arguments object are required.");
                }
                var definition = (tools ?? new ToolDefinition[0]).FirstOrDefault(tool => tool != null && string.Equals(tool.Id, toolId, StringComparison.OrdinalIgnoreCase));
                if (tools != null && definition == null) return AgentPlannerParseResult.Fail("unknown_tool", "Tool is not in the current tool slice: " + toolId);
                if (definition != null)
                {
                    JObject schema;
                    string schemaError;
                    if (!ToolSchemaSupport.TryNormalize(definition, out schema, out schemaError)) return AgentPlannerParseResult.Fail("invalid_tool_schema", schemaError);
                    string argumentError;
                    if (!ToolSchemaSupport.ValidateArguments(arguments, schema, true, out argumentError)) return AgentPlannerParseResult.Fail("invalid_arguments", argumentError);
                }
                var step = new AgentPlannerStep { ToolId = toolId, Reason = summary };
                ToolArgumentNormalizer.AddProperties(arguments, step.Arguments);
                response.Tool = step;
                return AgentPlannerParseResult.Ok(response);
            }

            if (!IsNull(obj["goal"]) || !IsNull(obj["plan"]) || !IsNull(obj["tool"]))
            {
                return AgentPlannerParseResult.Fail("invalid_terminal", kind + " requires goal, plan, and tool to be null.");
            }
            if (string.IsNullOrWhiteSpace(response.Message))
            {
                return AgentPlannerParseResult.Fail("missing_message", kind + " requires message.");
            }
            return AgentPlannerParseResult.Ok(response);
        }

        public AgentPlannerParseResult ParseNative(LlmCompletionResult completion, IEnumerable<ToolDefinition> tools, IEnumerable<LlmToolDefinition> apiTools)
        {
            var calls = completion == null ? null : completion.ToolCalls;
            if (calls == null || calls.Count == 0)
            {
                var contentDecision = Parse(completion == null ? null : completion.Content, tools);
                return contentDecision.Success && string.Equals(contentDecision.Response.Kind, AgentResponseKinds.Tool, StringComparison.OrdinalIgnoreCase)
                    ? AgentPlannerParseResult.Fail("native_tool_call_required", "native_tool_calls mode requires an API function call for tool actions.")
                    : contentDecision;
            }
            if (calls.Count != 1) return AgentPlannerParseResult.Fail("multiple_tool_calls", "Exactly one native tool call is allowed per model turn.");
            var call = calls[0];
            var toolId = ToolSchemaSupport.ResolveToolId(call.Name, apiTools);
            if (string.IsNullOrWhiteSpace(toolId)) return AgentPlannerParseResult.Fail("unknown_tool", "Native tool call name is not in the current tool slice: " + call.Name);
            JObject arguments;
            try { arguments = JObject.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson); }
            catch (JsonException ex) { return AgentPlannerParseResult.Fail("invalid_arguments", ex.Message); }
            var synthetic = new JObject
            {
                ["protocolVersion"] = AgentDecisionProtocol.Version,
                ["kind"] = AgentResponseKinds.Tool,
                ["decisionSummary"] = "Call " + toolId,
                ["goal"] = null,
                ["plan"] = null,
                ["tool"] = new JObject { ["toolId"] = toolId, ["arguments"] = arguments },
                ["message"] = null
            };
            var parsed = Parse(synthetic.ToString(Formatting.None), tools);
            if (parsed.Success)
            {
                parsed.Response.Tool.ToolCallId = call.Id;
            }
            return parsed;
        }

        private static bool KnownKind(string kind)
        {
            return string.Equals(kind, AgentResponseKinds.Plan, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kind, AgentResponseKinds.Tool, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kind, AgentResponseKinds.Clarify, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kind, AgentResponseKinds.Final, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kind, AgentResponseKinds.CannotComplete, StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadString(JToken token)
        {
            return token == null || token.Type == JTokenType.Null ? null : token.Type == JTokenType.String ? token.Value<string>() : null;
        }

        private static bool IsNull(JToken token)
        {
            return token != null && token.Type == JTokenType.Null;
        }
    }
}
