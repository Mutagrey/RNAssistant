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
        private static readonly string[] CanonicalFields =
        {
            "protocolVersion", "kind", "decisionSummary", "goal", "plan", "tool", "message"
        };

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

            string compatibilityCode;
            string compatibilityError;
            if (!TryNormalizeCompatibilityEnvelope(obj, out compatibilityCode, out compatibilityError))
            {
                return Fail(obj, compatibilityCode, compatibilityError);
            }

            var allowed = new HashSet<string>(CanonicalFields, StringComparer.Ordinal);
            var extra = obj.Properties().FirstOrDefault(property => !allowed.Contains(property.Name));
            if (extra != null) return Fail(obj, "unexpected_field", "Agent decision contains unsupported field: " + extra.Name);

            var protocolVersion = obj["protocolVersion"];
            int parsedVersion;
            if (!IsAbsentOrNull(protocolVersion) &&
                !TryReadProtocolVersion(protocolVersion, out parsedVersion))
            {
                return Fail(obj, "invalid_protocol_version", "protocolVersion must be 1 when provided.");
            }
            var kind = ReadString(obj["kind"]);
            if (string.IsNullOrWhiteSpace(kind)) kind = InferKind(obj);
            if (!KnownKind(kind)) return Fail(obj, "invalid_kind", "Agent decision kind is invalid or cannot be inferred.");
            kind = kind.ToLowerInvariant();

            var rawSummary = ReadString(obj["decisionSummary"]);
            var rawGoal = ReadString(obj["goal"]);
            var rawMessage = ReadString(obj["message"]);

            var response = new AgentPlannerResponse
            {
                ProtocolVersion = AgentDecisionProtocol.Version,
                Kind = kind,
                Goal = rawGoal,
                Message = rawMessage
            };

            string planError;
            if (!TryReadPlan(obj["plan"], response.Plan, out planError))
            {
                return Fail(obj, "invalid_plan_step", planError);
            }

            if (string.Equals(kind, AgentResponseKinds.Plan, StringComparison.OrdinalIgnoreCase))
            {
                if (!IsAbsentOrNull(obj["tool"]) || !string.IsNullOrWhiteSpace(response.Message))
                {
                    return Fail(obj, "invalid_plan", "plan cannot contain an executable tool or terminal message.");
                }
                if (response.Plan.Count == 0)
                {
                    return Fail(obj, "invalid_plan", "plan requires at least one usable step.");
                }
                response.Goal = FirstNonEmpty(response.Goal, rawSummary, "Рабочий план");
                response.DecisionSummary = FirstNonEmpty(rawSummary, response.Goal, "Обновляю рабочий план.");
                return AgentPlannerParseResult.Ok(response);
            }

            if (string.Equals(kind, AgentResponseKinds.Tool, StringComparison.OrdinalIgnoreCase))
            {
                var toolTokens = new List<JObject>();
                string toolListError;
                if (!TryReadTools(obj["tool"], toolTokens, out toolListError))
                {
                    return Fail(obj, "invalid_tool", toolListError);
                }
                foreach (var toolObject in toolTokens)
                {
                    string toolId;
                    JObject arguments;
                    string toolError;
                    if (!TryNormalizeTool(toolObject, out toolId, out arguments, out toolError))
                    {
                        return Fail(obj, "invalid_tool", toolError);
                    }
                    var definition = (tools ?? new ToolDefinition[0]).FirstOrDefault(tool => tool != null && string.Equals(tool.Id, toolId, StringComparison.OrdinalIgnoreCase));
                    if (tools != null && definition == null) return Fail(obj, "unknown_tool", "Tool is not in the current tool slice: " + toolId);
                    if (definition != null)
                    {
                        JObject schema;
                        string schemaError;
                        if (!ToolSchemaSupport.TryNormalize(definition, out schema, out schemaError)) return Fail(obj, "invalid_tool_schema", schemaError);
                        string argumentError;
                        if (!ToolSchemaSupport.ValidateArguments(arguments, schema, true, out argumentError)) return Fail(obj, "invalid_arguments", argumentError);
                    }
                    var step = new AgentPlannerStep { ToolId = toolId };
                    ToolArgumentNormalizer.AddProperties(arguments, step.Arguments);
                    response.Tools.Add(step);
                }
                response.DecisionSummary = FirstNonEmpty(
                    rawSummary,
                    rawMessage,
                    response.Tools.Count == 1
                        ? "Выполняю действие: " + response.Tools[0].ToolId + "."
                        : "Выполняю пакет действий: " + response.Tools.Count + ".");
                foreach (var step in response.Tools) step.Reason = response.DecisionSummary;
                return AgentPlannerParseResult.Ok(response);
            }

            if (!IsAbsentOrNull(obj["tool"]))
            {
                return Fail(obj, "invalid_terminal", kind + " cannot contain an executable tool.");
            }
            if (string.IsNullOrWhiteSpace(response.Message))
            {
                response.Message = rawSummary;
            }
            if (string.IsNullOrWhiteSpace(response.Message))
            {
                return Fail(obj, "missing_message", kind + " requires message or decisionSummary text.");
            }
            response.DecisionSummary = FirstNonEmpty(rawSummary, TerminalSummary(kind));
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
            if (calls.Count > AgentDecisionProtocol.MaxToolCallsPerDecision)
            {
                return AgentPlannerParseResult.Fail("too_many_tool_calls", "A model turn may select at most " + AgentDecisionProtocol.MaxToolCallsPerDecision + " tools.");
            }
            var toolArray = new JArray();
            var resolved = new List<Tuple<LlmToolCall, string>>();
            foreach (var call in calls)
            {
                var toolId = ToolSchemaSupport.ResolveToolId(call.Name, apiTools);
                if (string.IsNullOrWhiteSpace(toolId)) return AgentPlannerParseResult.Fail("unknown_tool", "Native tool call name is not in the current tool slice: " + call.Name);
                JObject arguments;
                try { arguments = JObject.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson); }
                catch (JsonException ex) { return AgentPlannerParseResult.Fail("invalid_arguments", ex.Message); }
                resolved.Add(Tuple.Create(call, toolId));
                toolArray.Add(new JObject { ["toolId"] = toolId, ["arguments"] = arguments });
            }
            var synthetic = new JObject
            {
                ["protocolVersion"] = AgentDecisionProtocol.Version,
                ["kind"] = AgentResponseKinds.Tool,
                ["decisionSummary"] = VisibleNativeSummary(completion, resolved.Select(item => item.Item2).ToList()),
                ["goal"] = null,
                ["plan"] = null,
                ["tool"] = toolArray,
                ["message"] = null
            };
            var parsed = Parse(synthetic.ToString(Formatting.None), tools);
            if (parsed.Success)
            {
                for (var index = 0; index < parsed.Response.Tools.Count; index++)
                {
                    parsed.Response.Tools[index].ToolCallId = resolved[index].Item1.Id;
                }
            }
            return parsed;
        }

        private static string VisibleNativeSummary(LlmCompletionResult completion, IReadOnlyList<string> toolIds)
        {
            var content = completion == null ? null : completion.Content;
            return string.IsNullOrWhiteSpace(content)
                ? (toolIds == null || toolIds.Count <= 1
                    ? "Выполняю следующее действие: " + (toolIds == null || toolIds.Count == 0 ? string.Empty : toolIds[0]) + "."
                    : "Выполняю пакет действий: " + toolIds.Count + ".")
                : content.Trim();
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

        private static bool IsAbsentOrNull(JToken token)
        {
            return token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined;
        }

        private static bool TryReadProtocolVersion(JToken token, out int version)
        {
            version = 0;
            if (token != null && token.Type == JTokenType.Integer)
            {
                version = token.Value<int>();
                return version == AgentDecisionProtocol.Version;
            }
            return token != null && token.Type == JTokenType.String &&
                int.TryParse(token.Value<string>(), out version) &&
                version == AgentDecisionProtocol.Version;
        }

        private static bool TryReadPlan(JToken token, ICollection<AgentPlanStep> target, out string error)
        {
            error = null;
            if (IsAbsentOrNull(token)) return true;
            var envelope = token as JObject;
            if (envelope != null) token = envelope["steps"];
            var array = token as JArray;
            if (array == null)
            {
                error = "plan must be an array, null, omitted, or an object containing a steps array.";
                return false;
            }

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < array.Count; index++)
            {
                var item = array[index];
                string id;
                string title;
                if (item != null && item.Type == JTokenType.String)
                {
                    id = "step_" + (index + 1);
                    title = item.Value<string>();
                }
                else
                {
                    var step = item as JObject;
                    if (step == null)
                    {
                        error = "Each plan step must be a string or object.";
                        return false;
                    }
                    var allowed = new HashSet<string>(new[]
                    {
                        "id", "title", "action", "description", "text", "name", "expected", "status"
                    }, StringComparer.OrdinalIgnoreCase);
                    var extra = step.Properties().FirstOrDefault(property => !allowed.Contains(property.Name));
                    if (extra != null)
                    {
                        error = "Plan step contains unsupported field: " + extra.Name;
                        return false;
                    }
                    id = FirstNonEmpty(ReadString(step["id"]), "step_" + (index + 1));
                    title = FirstNonEmpty(
                        ReadString(step["title"]),
                        ReadString(step["action"]),
                        ReadString(step["description"]),
                        ReadString(step["text"]),
                        ReadString(step["name"]));
                }

                if (string.IsNullOrWhiteSpace(title))
                {
                    error = "Each plan step requires title, action, description, text, or name.";
                    return false;
                }
                id = UniqueStepId(id, ids);
                target.Add(new AgentPlanStep { Id = id, Title = title.Trim(), Status = "pending" });
            }
            return true;
        }

        private static string UniqueStepId(string proposed, ISet<string> ids)
        {
            var root = string.IsNullOrWhiteSpace(proposed) ? "step" : proposed.Trim();
            var candidate = root;
            var suffix = 2;
            while (!ids.Add(candidate))
            {
                candidate = root + "_" + suffix;
                suffix += 1;
            }
            return candidate;
        }

        private static bool TryNormalizeTool(JObject tool, out string toolId, out JObject arguments, out string error)
        {
            toolId = null;
            arguments = null;
            error = null;
            if (tool == null)
            {
                error = "tool decision requires one tool object.";
                return false;
            }

            var allowed = new HashSet<string>(new[]
            {
                "toolId", "id", "name", "arguments", "args", "function", "type"
            }, StringComparer.OrdinalIgnoreCase);
            var extra = tool.Properties().FirstOrDefault(property => !allowed.Contains(property.Name));
            if (extra != null)
            {
                error = "Tool object contains unsupported field: " + extra.Name;
                return false;
            }

            var function = tool["function"] as JObject;
            if (tool["function"] != null && function == null)
            {
                error = "tool function must be an object when provided.";
                return false;
            }
            if (function != null)
            {
                var functionExtra = function.Properties().FirstOrDefault(property =>
                    !string.Equals(property.Name, "name", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(property.Name, "arguments", StringComparison.OrdinalIgnoreCase));
                if (functionExtra != null)
                {
                    error = "Tool function contains unsupported field: " + functionExtra.Name;
                    return false;
                }
            }

            var names = new[]
            {
                ReadString(tool["toolId"]),
                ReadString(tool["name"]),
                ReadString(function == null ? null : function["name"])
            }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToList();
            if (names.Count == 0 && function == null)
            {
                var idAlias = ReadString(tool["id"]);
                if (!string.IsNullOrWhiteSpace(idAlias)) names.Add(idAlias.Trim());
            }
            if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            {
                error = "Tool id aliases conflict.";
                return false;
            }
            toolId = names.FirstOrDefault();

            var argumentCandidates = new[]
            {
                tool["arguments"],
                tool["args"],
                function == null ? null : function["arguments"]
            }.Where(value => !IsAbsentOrNull(value)).ToList();
            var argumentsToken = argumentCandidates.FirstOrDefault();
            if (argumentCandidates.Skip(1).Any(value => !JToken.DeepEquals(argumentsToken, value)))
            {
                error = "Tool argument aliases conflict.";
                return false;
            }
            if (argumentsToken != null && argumentsToken.Type == JTokenType.String)
            {
                try { argumentsToken = JObject.Parse(argumentsToken.Value<string>() ?? "{}"); }
                catch (JsonException ex)
                {
                    error = "Tool arguments JSON is invalid: " + ex.Message;
                    return false;
                }
            }
            arguments = argumentsToken as JObject;
            if (arguments == null && IsAbsentOrNull(argumentsToken)) arguments = new JObject();
            if (arguments == null)
            {
                error = "tool arguments must be a JSON object.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(toolId))
            {
                error = "toolId (or compatibility alias id/name) is required.";
                return false;
            }
            return true;
        }

        private static bool TryReadTools(JToken token, ICollection<JObject> target, out string error)
        {
            error = null;
            if (token is JObject)
            {
                target.Add((JObject)token);
                return true;
            }
            var array = token as JArray;
            if (array == null || array.Count == 0)
            {
                error = "tool decision requires a non-empty tool array (a legacy single object is also accepted).";
                return false;
            }
            if (array.Count > AgentDecisionProtocol.MaxToolCallsPerDecision)
            {
                error = "tool decision may contain at most " + AgentDecisionProtocol.MaxToolCallsPerDecision + " calls.";
                return false;
            }
            foreach (var item in array)
            {
                var tool = item as JObject;
                if (tool == null)
                {
                    error = "Every tool array item must be an object.";
                    return false;
                }
                target.Add(tool);
            }
            return true;
        }

        private static bool TryNormalizeCompatibilityEnvelope(JObject obj, out string errorCode, out string error)
        {
            errorCode = null;
            error = null;
            if (!MoveAlias(obj, "protocolVersion", "protocol_version", out error))
            {
                errorCode = "conflicting_alias";
                return false;
            }
            if (!MoveAlias(obj, "decisionSummary", "decision_summary", out error))
            {
                errorCode = "conflicting_alias";
                return false;
            }

            var action = obj["action"] as JObject;
            if (action != null)
            {
                var type = FirstNonEmpty(ReadString(action["type"]), ReadString(action["kind"]));
                if (string.Equals(type, "reply", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type, "answer", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type, "respond", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type, "final", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TrySetCompatibilityKind(obj, AgentResponseKinds.Final, out errorCode, out error)) return false;
                    FillIfMissing(obj, "message", FirstNonEmpty(ReadString(action["content"]), ReadString(action["text"]), ReadString(action["message"])));
                }
                else if (string.Equals(type, AgentResponseKinds.Clarify, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(type, AgentResponseKinds.CannotComplete, StringComparison.OrdinalIgnoreCase))
                {
                    if (!TrySetCompatibilityKind(obj, type.ToLowerInvariant(), out errorCode, out error)) return false;
                    FillIfMissing(obj, "message", FirstNonEmpty(ReadString(action["content"]), ReadString(action["text"]), ReadString(action["message"])));
                }
                else if (string.Equals(type, AgentResponseKinds.Tool, StringComparison.OrdinalIgnoreCase))
                {
                    if (!TrySetCompatibilityKind(obj, AgentResponseKinds.Tool, out errorCode, out error)) return false;
                    if (IsAbsentOrNull(obj["tool"])) obj["tool"] = (action["tool"] ?? action).DeepClone();
                }
                else
                {
                    errorCode = "unsupported_action";
                    error = "Compatibility action must be reply, final, clarify, cannot_complete, or tool.";
                    return false;
                }
                obj.Remove("action");
            }

            var callsToken = FirstToken(obj["toolCalls"], obj["tool_calls"]);
            if (!IsAbsentOrNull(callsToken))
            {
                var calls = callsToken as JArray;
                if (calls == null || calls.Count == 0 || calls.Count > AgentDecisionProtocol.MaxToolCallsPerDecision)
                {
                    errorCode = "invalid_tool_calls";
                    error = "Compatibility toolCalls must contain 1-" + AgentDecisionProtocol.MaxToolCallsPerDecision + " calls.";
                    return false;
                }
                if (!IsAbsentOrNull(obj["tool"]))
                {
                    errorCode = "multiple_tool_calls";
                    error = "Canonical tool and compatibility toolCalls cannot be combined.";
                    return false;
                }
                var normalizedCalls = new JArray();
                var terminalMessage = string.Empty;
                foreach (var token in calls)
                {
                    var call = token as JObject;
                    if (call == null)
                    {
                        errorCode = "invalid_tool";
                        error = "toolCalls items must be objects.";
                        return false;
                    }
                    string callId;
                    JObject callArguments;
                    string callError;
                    if (!TryNormalizeTool(call, out callId, out callArguments, out callError))
                    {
                        errorCode = "invalid_tool";
                        error = callError;
                        return false;
                    }
                    if (string.Equals(callId, "answer", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(callId, "reply", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(callId, "respond", StringComparison.OrdinalIgnoreCase))
                    {
                        terminalMessage = FirstNonEmpty(ReadString(callArguments["text"]), ReadString(callArguments["content"]), ReadString(callArguments["message"]));
                    }
                    else
                    {
                        normalizedCalls.Add(call.DeepClone());
                    }
                }
                if (!string.IsNullOrWhiteSpace(terminalMessage))
                {
                    if (normalizedCalls.Count > 0 || calls.Count != 1)
                    {
                        errorCode = "conflicting_envelope";
                        error = "A terminal pseudo-tool cannot be combined with executable tool calls.";
                        return false;
                    }
                    if (!TrySetCompatibilityKind(obj, AgentResponseKinds.Final, out errorCode, out error)) return false;
                    FillIfMissing(obj, "message", terminalMessage);
                }
                else
                {
                    if (!TrySetCompatibilityKind(obj, AgentResponseKinds.Tool, out errorCode, out error)) return false;
                    obj["tool"] = normalizedCalls;
                }
                obj.Remove("toolCalls");
                obj.Remove("tool_calls");
            }
            return true;
        }

        private static bool TrySetCompatibilityKind(
            JObject obj,
            string normalizedKind,
            out string errorCode,
            out string errorMessage)
        {
            errorCode = null;
            errorMessage = null;
            var existingKind = ReadString(obj?["kind"]);
            if (!string.IsNullOrWhiteSpace(existingKind) &&
                !string.Equals(existingKind, normalizedKind, StringComparison.OrdinalIgnoreCase))
            {
                errorCode = "conflicting_envelope";
                errorMessage = "Compatibility envelope conflicts with the explicit decision kind.";
                return false;
            }

            FillIfMissing(obj, "kind", normalizedKind);
            return true;
        }

        private static string InferKind(JObject obj)
        {
            if (!IsAbsentOrNull(obj["tool"])) return AgentResponseKinds.Tool;
            var plan = obj["plan"];
            var planArray = plan as JArray;
            var planEnvelope = plan as JObject;
            if (planArray != null && planArray.Count > 0 || planEnvelope != null && planEnvelope["steps"] is JArray)
            {
                return AgentResponseKinds.Plan;
            }
            if (!string.IsNullOrWhiteSpace(ReadString(obj["message"]))) return AgentResponseKinds.Final;
            return null;
        }

        private static string TerminalSummary(string kind)
        {
            if (string.Equals(kind, AgentResponseKinds.Clarify, StringComparison.OrdinalIgnoreCase)) return "Нужно уточнение.";
            if (string.Equals(kind, AgentResponseKinds.CannotComplete, StringComparison.OrdinalIgnoreCase)) return "Не могу завершить задачу.";
            return "Завершаю задачу.";
        }

        private static AgentPlannerParseResult Fail(JObject obj, string code, string message)
        {
            var result = AgentPlannerParseResult.Fail(code, message);
            if (obj != null)
            {
                result.RecoveredDecisionSummary = FirstNonEmpty(
                    ReadString(obj["decisionSummary"]),
                    ReadString(obj["decision_summary"]));
                result.RecoveredGoal = ReadString(obj["goal"]);
            }
            return result;
        }

        private static bool MoveAlias(JObject obj, string canonical, string alias, out string error)
        {
            error = null;
            if (obj == null || obj[alias] == null) return true;
            if (obj[canonical] != null && !JToken.DeepEquals(obj[canonical], obj[alias]))
            {
                error = canonical + " conflicts with compatibility alias " + alias + ".";
                return false;
            }
            if (obj[canonical] == null) obj[canonical] = obj[alias];
            obj.Remove(alias);
            return true;
        }

        private static void FillIfMissing(JObject obj, string name, string value)
        {
            if (obj == null || string.IsNullOrWhiteSpace(value) || !IsAbsentOrNull(obj[name])) return;
            obj[name] = value;
        }

        private static JToken FirstToken(params JToken[] values)
        {
            foreach (var value in values ?? new JToken[0])
            {
                if (!IsAbsentOrNull(value)) return value;
            }
            return null;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values ?? new string[0])
            {
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            return null;
        }
    }
}
