using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Tools
{
    public sealed class AgentPlannerResponseParser
    {
        public AgentPlannerParseResult Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return AgentPlannerParseResult.Fail("empty_response", "Planner response is empty.");
            }

            var trimmed = UnwrapJsonFence(text);
            if (!trimmed.StartsWith("{", StringComparison.Ordinal) || !trimmed.EndsWith("}", StringComparison.Ordinal))
            {
                return AgentPlannerParseResult.Fail("not_json_object", "Planner response must be exactly one JSON object, optionally wrapped in one JSON code fence.");
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

            foreach (var property in obj.Properties())
            {
                if (!IsAllowedRootProperty(property.Name))
                {
                    return AgentPlannerParseResult.Fail("unexpected_field", "Planner response contains unsupported field: " + property.Name);
                }
            }
            if (!IsStringProperty(obj, "kind", false))
            {
                return AgentPlannerParseResult.Fail("invalid_kind", "Planner response kind must be a string.");
            }
            if (!IsStringProperty(obj, "intent", true))
            {
                return AgentPlannerParseResult.Fail("invalid_intent", "Planner response intent must be a string or null.");
            }
            if (!IsStringProperty(obj, "message", true))
            {
                return AgentPlannerParseResult.Fail("invalid_message", "Planner response message must be a string or null.");
            }
            if (!IsStringProperty(obj, "expectedOutcome", true))
            {
                return AgentPlannerParseResult.Fail("invalid_expected_outcome", "Planner response expectedOutcome must be a string or null.");
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
            else if (!IsKnownIntent(response.Intent))
            {
                return AgentPlannerParseResult.Fail("invalid_intent", "Planner response intent is invalid.");
            }

            var stepsToken = obj["steps"];
            var isToolPlan = string.Equals(response.Kind, AgentResponseKinds.ToolPlan, StringComparison.OrdinalIgnoreCase);
            if ((stepsToken == null || stepsToken.Type == JTokenType.Null) && isToolPlan)
            {
                return AgentPlannerParseResult.Fail("missing_steps", "Planner response requires a steps array.");
            }
            var steps = stepsToken == null || stepsToken.Type == JTokenType.Null
                ? new JArray()
                : stepsToken as JArray;
            if (steps == null)
            {
                return AgentPlannerParseResult.Fail("invalid_steps", "Planner response steps must be an array.");
            }
            foreach (var stepToken in steps)
            {
                var stepObject = stepToken as JObject;
                if (stepObject == null)
                {
                    return AgentPlannerParseResult.Fail("invalid_step", "Each planner step must be an object.");
                }
                foreach (var property in stepObject.Properties())
                {
                    if (!IsAllowedStepProperty(property.Name))
                    {
                        return AgentPlannerParseResult.Fail("unexpected_step_field", "Planner step contains unsupported field: " + property.Name);
                    }
                }
                if (!IsStringProperty(stepObject, "toolId", false))
                {
                    return AgentPlannerParseResult.Fail("missing_tool_id", "Each planner step requires a string toolId.");
                }
                if (!IsStringProperty(stepObject, "reason", true))
                {
                    return AgentPlannerParseResult.Fail("invalid_reason", "Planner step reason must be a string or null.");
                }

                var step = new AgentPlannerStep
                {
                    ToolId = ReadString(stepObject, "toolId"),
                    Reason = ReadString(stepObject, "reason")
                };
                if (string.IsNullOrWhiteSpace(step.ToolId))
                {
                    return AgentPlannerParseResult.Fail("missing_tool_id", "Each planner step requires toolId.");
                }

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

            if (isToolPlan && response.Steps.Count == 0)
            {
                return AgentPlannerParseResult.Fail("missing_steps", "tool_plan response requires at least one step.");
            }

            if (!isToolPlan && response.Steps.Count > 0)
            {
                return AgentPlannerParseResult.Fail("unexpected_steps", "Only tool_plan responses may include steps.");
            }

            if (!isToolPlan &&
                string.IsNullOrWhiteSpace(response.Message))
            {
                return AgentPlannerParseResult.Fail("missing_message", response.Kind + " response requires message.");
            }

            return AgentPlannerParseResult.Ok(response);
        }

        private static bool IsAllowedRootProperty(string name)
        {
            return string.Equals(name, "kind", StringComparison.Ordinal) ||
                string.Equals(name, "intent", StringComparison.Ordinal) ||
                string.Equals(name, "message", StringComparison.Ordinal) ||
                string.Equals(name, "steps", StringComparison.Ordinal) ||
                string.Equals(name, "expectedOutcome", StringComparison.Ordinal);
        }

        private static bool IsAllowedStepProperty(string name)
        {
            return string.Equals(name, "toolId", StringComparison.Ordinal) ||
                string.Equals(name, "arguments", StringComparison.Ordinal) ||
                string.Equals(name, "reason", StringComparison.Ordinal);
        }

        private static bool IsStringProperty(JObject obj, string name, bool allowNullOrMissing)
        {
            var token = obj == null ? null : obj[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return allowNullOrMissing;
            }
            return token.Type == JTokenType.String;
        }

        private static string UnwrapJsonFence(string text)
        {
            var value = (text ?? string.Empty).TrimStart('\uFEFF').Trim();
            if (!value.StartsWith("```", StringComparison.Ordinal) ||
                !value.EndsWith("```", StringComparison.Ordinal))
            {
                return value;
            }

            var newline = value.IndexOf('\n');
            if (newline < 0)
            {
                return value;
            }

            var language = value.Substring(3, newline - 3).Trim();
            if (language.Length > 0 && !string.Equals(language, "json", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            return value.Substring(newline + 1, value.Length - newline - 4).Trim();
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

        private static bool IsKnownIntent(string intent)
        {
            return string.Equals(intent, AgentIntents.Read, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(intent, AgentIntents.Analyze, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(intent, AgentIntents.Mutate, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(intent, AgentIntents.Verify, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(intent, AgentIntents.Answer, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(intent, AgentIntents.Clarify, StringComparison.OrdinalIgnoreCase);
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
