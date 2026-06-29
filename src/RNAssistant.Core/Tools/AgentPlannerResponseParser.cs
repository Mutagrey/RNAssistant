using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Tools
{
    public sealed class AgentPlannerResponseParser
    {
        public AgentPlannerParseResult ParseStrict(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return AgentPlannerParseResult.Fail("empty_response", "Planner response is empty.");
            }

            var trimmed = text.Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal) || !trimmed.EndsWith("}", StringComparison.Ordinal))
            {
                return AgentPlannerParseResult.Fail("not_json_object", "Planner response must be exactly one JSON object.");
            }

            JObject obj;
            try
            {
                obj = JObject.Parse(trimmed);
            }
            catch (JsonException ex)
            {
                return AgentPlannerParseResult.Fail("invalid_json", ex.Message);
            }

            var response = new AgentPlannerResponse
            {
                Kind = ReadString(obj, "kind"),
                Intent = ReadString(obj, "intent"),
                Message = ReadString(obj, "message"),
                ExpectedOutcome = ReadString(obj, "expectedOutcome")
            };

            if (!IsKnownKind(response.Kind))
            {
                return AgentPlannerParseResult.Fail("invalid_kind", "Planner response kind is invalid.");
            }

            if (string.IsNullOrWhiteSpace(response.Intent))
            {
                response.Intent = DefaultIntent(response.Kind);
            }

            var steps = obj["steps"] as JArray;
            if (steps != null)
            {
                foreach (var stepToken in steps)
                {
                    var stepObject = stepToken as JObject;
                    if (stepObject == null)
                    {
                        return AgentPlannerParseResult.Fail("invalid_step", "Each planner step must be an object.");
                    }

                    var step = new AgentPlannerStep
                    {
                        ToolId = ReadString(stepObject, "toolId"),
                        Reason = ReadString(stepObject, "reason")
                    };

                    var arguments = stepObject["arguments"];
                    if (arguments != null && arguments.Type != JTokenType.Null)
                    {
                        var argObject = arguments as JObject;
                        if (argObject == null)
                        {
                            return AgentPlannerParseResult.Fail("invalid_arguments", "Step arguments must be an object.");
                        }

                        foreach (var property in argObject.Properties())
                        {
                            step.Arguments[property.Name] = ToObjectValue(property.Value);
                        }
                    }

                    response.Steps.Add(step);
                }
            }

            if (string.Equals(response.Kind, AgentResponseKinds.ToolPlan, StringComparison.OrdinalIgnoreCase) && response.Steps.Count == 0)
            {
                return AgentPlannerParseResult.Fail("missing_steps", "tool_plan response requires at least one step.");
            }

            if (!string.Equals(response.Kind, AgentResponseKinds.ToolPlan, StringComparison.OrdinalIgnoreCase) && response.Steps.Count > 0)
            {
                return AgentPlannerParseResult.Fail("unexpected_steps", "Only tool_plan responses may include steps.");
            }

            if (!string.Equals(response.Kind, AgentResponseKinds.ToolPlan, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(response.Message))
            {
                return AgentPlannerParseResult.Fail("missing_message", response.Kind + " response requires message.");
            }

            return AgentPlannerParseResult.Ok(response);
        }

        private static string ReadString(JObject obj, string name)
        {
            var token = obj == null ? null : obj[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }
            return token.Type == JTokenType.String ? token.Value<string>() : token.ToString(Formatting.None);
        }

        private static bool IsKnownKind(string kind)
        {
            return string.Equals(kind, AgentResponseKinds.ToolPlan, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kind, AgentResponseKinds.Final, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kind, AgentResponseKinds.Clarify, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kind, AgentResponseKinds.CannotDo, StringComparison.OrdinalIgnoreCase);
        }

        private static string DefaultIntent(string kind)
        {
            if (string.Equals(kind, AgentResponseKinds.Final, StringComparison.OrdinalIgnoreCase))
            {
                return AgentIntents.Answer;
            }
            if (string.Equals(kind, AgentResponseKinds.Clarify, StringComparison.OrdinalIgnoreCase))
            {
                return AgentIntents.Clarify;
            }
            return AgentIntents.Read;
        }

        private static object ToObjectValue(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }
            if (token.Type == JTokenType.Integer)
            {
                return token.Value<long>();
            }
            if (token.Type == JTokenType.Float)
            {
                return token.Value<double>();
            }
            if (token.Type == JTokenType.Boolean)
            {
                return token.Value<bool>();
            }
            if (token.Type == JTokenType.String)
            {
                return token.Value<string>();
            }
            return token.ToString(Formatting.None);
        }
    }
}
