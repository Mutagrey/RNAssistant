using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class ToolAuthoringExecutor
    {
        public const string DefinitionReadToolId = "common.tools_definition_read";

        private readonly IOfficeApplicationAdapter _adapter;
        private readonly ToolStore _toolStore;

        public ToolAuthoringExecutor(IOfficeApplicationAdapter adapter, ToolStore toolStore)
        {
            _adapter = adapter;
            _toolStore = toolStore;
        }

        public IEnumerable<ToolDefinition> GetControllerTools()
        {
            if (_toolStore == null)
            {
                yield break;
            }

            yield return ControllerToolDefinition.Create(DefinitionReadToolId, "Common", "Read-only authoring inspection: Read one custom tool definition including its implementation fields; omit id to list compact custom-tool metadata. This does not load a callable schema.", OptionalIdSchema());
            yield return ControllerToolDefinition.Create("common.tools_validate", "Common", "Read-only: Validate a complete custom pipeline or manifest-based VBA tool definition without saving it. Agent authoring may use compact parameterDefinitions and pipelineSteps; advanced callers may pass complete native parameters/pipeline objects.", ToolPayloadSchema(false));
            yield return ControllerToolDefinition.Create("common.tools_upsert", "Common", "Mutates settings: Create or update one custom tool after validating the effective definition. In Agent mode prefer compact parameterDefinitions and pipelineSteps; parameters/pipeline remain the advanced native forms. Omitted update fields are preserved.", ToolUpsertSchema(), mutatesLocalState: true, requiresConfirmation: true, riskLevel: 1);
            yield return ControllerToolDefinition.Create("common.tools_delete", "Common", "Mutates settings: Delete a custom RNAssistant tool by id.", "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\",\"description\":\"Exact stable identifier.\"}},\"required\":[\"id\"],\"additionalProperties\":false}", mutatesLocalState: true, requiresConfirmation: true, riskLevel: 1);
        }

        public ToolResult ExecuteControllerTool(ToolCommand command, AppSettings settings, bool dryRun, bool manualRun)
        {
            if (_toolStore == null)
            {
                return ToolResult.Fail("Tool authoring store is not available.");
            }

            if (string.Equals(command.ToolId, DefinitionReadToolId, StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(ToolArgumentReader.String(command.Arguments, "id", string.Empty))
                    ? ListTools()
                    : ReadTool(command);
            }

            if (string.Equals(command.ToolId, "common.tools_validate", StringComparison.OrdinalIgnoreCase))
            {
                return ValidateToolPayload(command);
            }

            if (string.Equals(command.ToolId, "common.tools_upsert", StringComparison.OrdinalIgnoreCase))
            {
                return UpsertTool(command, settings, dryRun, manualRun);
            }

            if (string.Equals(command.ToolId, "common.tools_delete", StringComparison.OrdinalIgnoreCase))
            {
                return DeleteTool(command, settings, dryRun, manualRun);
            }

            return ToolResult.Fail("Unknown tool authoring command: " + command.ToolId);
        }

        private ToolResult ListTools()
        {
            var tools = VisibleTools().Select(t => new
            {
                id = t.Id,
                host = t.Host,
                name = t.Name,
                description = t.Description,
                executor = t.Executor,
                enabled = t.Enabled,
                requiresConfirmation = t.RequiresConfirmation,
                mutatesDocument = t.MutatesDocument,
                agentCanRun = t.AgentCanRun,
                riskLevel = t.RiskLevel,
                capabilityStatus = t.CapabilityStatus,
                limitations = t.Limitations
            }).ToArray();
            return ToolResult.Ok("Custom tools listed.", JsonConvert.SerializeObject(tools));
        }

        private ToolResult ReadTool(ToolCommand command)
        {
            var id = ToolArgumentReader.String(command.Arguments, "id", string.Empty);
            var tool = VisibleTools().FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
            if (tool == null)
            {
                return ToolResult.Fail("Custom tool not found: " + id);
            }

            return ToolResult.Ok("Custom tool read: " + tool.Id, ToolPayload(tool).ToString(Formatting.None));
        }

        private ToolResult ValidateToolPayload(ToolCommand command)
        {
            var parameterError = ValidateParameterInput(command);
            if (parameterError != null) return parameterError;
            var pipelineError = ValidatePipelineInput(command);
            if (pipelineError != null) return pipelineError;
            var tool = ReadToolDefinition(command);
            var validation = ValidateToolDefinition(tool);
            if (!validation.Success)
            {
                return validation;
            }

            return ToolResult.Ok("Tool definition is valid: " + tool.Id, ToolPayload(tool).ToString(Formatting.None));
        }

        private ToolResult UpsertTool(ToolCommand command, AppSettings settings, bool dryRun, bool manualRun)
        {
            var parameterError = ValidateParameterInput(command);
            if (parameterError != null) return parameterError;
            var pipelineError = ValidatePipelineInput(command);
            if (pipelineError != null) return pipelineError;
            var id = ToolArgumentReader.String(command.Arguments, "id", string.Empty);
            var mode = ToolArgumentReader.String(command.Arguments, "mode", "upsert");
            var existing = _toolStore.Load().FirstOrDefault(tool => string.Equals(tool.Id, id, StringComparison.OrdinalIgnoreCase));
            if (existing != null && string.Equals(mode, "createOnly", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Fail("Custom tool already exists: " + id + ". Use mode=upsert or updateOnly.", null, "tool_already_exists", false);
            }
            if (existing == null && string.Equals(mode, "updateOnly", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Fail("Custom tool not found: " + id + ". Use mode=upsert or createOnly.", null, "tool_not_found", false);
            }
            if (existing != null && !HasMutableArguments(command))
            {
                return ToolResult.Fail("Tool update requires at least one supplied field besides id/mode.", null, "tool_update_empty", true);
            }

            return existing == null
                ? PersistTool(ReadToolDefinition(command), settings, dryRun, manualRun, "create")
                : PersistTool(UpdateToolDefinition(existing, command), settings, dryRun, manualRun, "update");
        }

        private ToolResult PersistTool(ToolDefinition tool, AppSettings settings, bool dryRun, bool manualRun, string operation)
        {
            if (!dryRun && !manualRun && !(settings ?? new AppSettings()).AutoConfirmToolActions)
            {
                return ToolResult.WaitingConfirmation("Tool " + operation + " requires confirmation: " + (tool == null ? string.Empty : tool.Id));
            }
            var validation = ValidateToolDefinition(tool);
            if (!validation.Success) return validation;
            if (dryRun)
            {
                return ToolResult.Ok("Dry run: would " + operation + " custom tool " + tool.Id, ToolPayload(tool).ToString(Formatting.None));
            }

            var saved = _toolStore.SaveOne(tool);
            return ToolResult.Ok("Custom tool " + (operation == "create" ? "created: " : "updated: ") + tool.Id,
                ToolPayload(saved ?? tool).ToString(Formatting.None));
        }

        private ToolResult DeleteTool(ToolCommand command, AppSettings settings, bool dryRun, bool manualRun)
        {
            var id = ToolArgumentReader.String(command.Arguments, "id", string.Empty);
            if (string.IsNullOrWhiteSpace(id))
            {
                return ToolResult.Fail("Tool id is required.");
            }
            if (!_toolStore.Load().Any(tool => tool != null && string.Equals(tool.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                return ToolResult.Fail("Custom tool not found: " + id, null, "tool_not_found", false);
            }

            if (!dryRun && !manualRun && !(settings ?? new AppSettings()).AutoConfirmToolActions)
            {
                return ToolResult.WaitingConfirmation("Tool delete requires confirmation: " + id);
            }

            if (dryRun)
            {
                return ToolResult.Ok("Dry run: would delete custom tool " + id);
            }

            return _toolStore.Delete(id)
                ? ToolResult.Ok("Custom tool deleted: " + id)
                : ToolResult.Fail("Custom tool not found: " + id);
        }

        private ToolDefinition ReadToolDefinition(ToolCommand command)
        {
            var id = ToolArgumentReader.String(command.Arguments, "id", string.Empty);
            var components = ReadComponents(ToolArgumentReader.String(command.Arguments, "components", "[]"));
            return NormalizeVbaEntryCode(new ToolDefinition
            {
                Id = id,
                Host = ToolArgumentReader.String(command.Arguments, "host", DefaultHostFromId(id)),
                Name = ToolArgumentReader.String(command.Arguments, "name", id),
                Description = ToolArgumentReader.String(command.Arguments, "description", string.Empty),
                ArgumentSchemaJson = ResolveParameterSchema(command, "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}"),
                Executor = ToolArgumentReader.String(command.Arguments, "executor", "pipeline"),
                PipelineJson = ResolvePipelineJson(command, string.Empty),
                Readme = ToolArgumentReader.String(command.Arguments, "readme", string.Empty),
                Enabled = ReadBool(command, "enabled", true),
                RequiresConfirmation = ReadBool(command, "requiresConfirmation", false),
                MutatesDocument = ReadBool(command, "mutatesDocument", false),
                MutatesLocalState = ReadBool(command, "mutatesLocalState", false),
                AgentCanRun = ReadBool(command, "agentCanRun", true),
                BuiltIn = false,
                RiskLevel = ReadInt(command, "riskLevel", 0),
                UseWhen = ToolArgumentReader.String(command.Arguments, "useWhen", string.Empty),
                DoNotUseWhen = ToolArgumentReader.String(command.Arguments, "doNotUseWhen", string.Empty),
                CapabilityStatus = ToolArgumentReader.String(command.Arguments, "capabilityStatus", "available"),
                Limitations = ToolArgumentReader.String(command.Arguments, "limitations", string.Empty),
                Components = components
            });
        }

        private static ToolDefinition UpdateToolDefinition(ToolDefinition existing, ToolCommand command)
        {
            var tool = existing.Clone();
            tool.StoragePath = existing.StoragePath;
            SetString(command, "host", value => tool.Host = value);
            SetString(command, "name", value => tool.Name = value);
            SetString(command, "description", value => tool.Description = value);
            if (HasArgument(command, "parameters") || HasArgument(command, "parameterDefinitions"))
            {
                tool.ArgumentSchemaJson = ResolveParameterSchema(command, tool.ArgumentSchemaJson);
            }
            SetString(command, "executor", value => tool.Executor = value);
            if (HasArgument(command, "pipeline") || HasArgument(command, "pipelineSteps"))
            {
                tool.PipelineJson = ResolvePipelineJson(command, tool.PipelineJson);
            }
            SetString(command, "readme", value => tool.Readme = value);
            SetString(command, "useWhen", value => tool.UseWhen = value);
            SetString(command, "doNotUseWhen", value => tool.DoNotUseWhen = value);
            SetString(command, "capabilityStatus", value => tool.CapabilityStatus = value);
            SetString(command, "limitations", value => tool.Limitations = value);
            SetBool(command, "enabled", value => tool.Enabled = value);
            SetBool(command, "requiresConfirmation", value => tool.RequiresConfirmation = value);
            SetBool(command, "mutatesDocument", value => tool.MutatesDocument = value);
            SetBool(command, "mutatesLocalState", value => tool.MutatesLocalState = value);
            SetBool(command, "agentCanRun", value => tool.AgentCanRun = value);
            if (HasArgument(command, "riskLevel")) tool.RiskLevel = ReadInt(command, "riskLevel", tool.RiskLevel);
            if (HasArgument(command, "components")) tool.Components = ReadComponents(ToolArgumentReader.String(command.Arguments, "components", "[]"));
            return NormalizeVbaEntryCode(tool);
        }

        private static ToolResult ValidateParameterInput(ToolCommand command)
        {
            if (HasArgument(command, "parameters") && HasArgument(command, "parameterDefinitions"))
            {
                return ToolResult.Fail(
                    "Supply either parameters or parameterDefinitions, not both. Prefer parameterDefinitions in Agent mode.",
                    null,
                    "tool_parameters_ambiguous",
                    true);
            }
            if (!HasArgument(command, "parameterDefinitions")) return null;
            try
            {
                BuildParameterSchema(ToolArgumentReader.String(command.Arguments, "parameterDefinitions", "[]"));
                return null;
            }
            catch (InvalidOperationException ex)
            {
                return ToolResult.Fail(ex.Message, null, "invalid_tool_parameter_definitions", true);
            }
            catch (JsonException ex)
            {
                return ToolResult.Fail("parameterDefinitions must be a native JSON array: " + ex.Message, null, "invalid_tool_parameter_definitions", true);
            }
        }

        private static ToolResult ValidatePipelineInput(ToolCommand command)
        {
            if (HasArgument(command, "pipeline") && HasArgument(command, "pipelineSteps"))
            {
                return ToolResult.Fail(
                    "Supply either pipeline or pipelineSteps, not both. Prefer pipelineSteps in Agent mode.",
                    null,
                    "tool_pipeline_ambiguous",
                    true);
            }
            if (!HasArgument(command, "pipelineSteps")) return null;
            try
            {
                BuildPipelineJson(ToolArgumentReader.String(command.Arguments, "pipelineSteps", "[]"));
                return null;
            }
            catch (InvalidOperationException ex)
            {
                return ToolResult.Fail(ex.Message, null, "invalid_tool_pipeline_steps", true);
            }
            catch (JsonException ex)
            {
                return ToolResult.Fail("pipelineSteps must be a native JSON array: " + ex.Message, null, "invalid_tool_pipeline_steps", true);
            }
        }

        private static string ResolveParameterSchema(ToolCommand command, string fallback)
        {
            if (HasArgument(command, "parameterDefinitions"))
            {
                return BuildParameterSchema(ToolArgumentReader.String(command.Arguments, "parameterDefinitions", "[]"));
            }
            return ToolArgumentReader.String(command.Arguments, "parameters", fallback);
        }

        private static string ResolvePipelineJson(ToolCommand command, string fallback)
        {
            if (HasArgument(command, "pipelineSteps"))
            {
                return BuildPipelineJson(ToolArgumentReader.String(command.Arguments, "pipelineSteps", "[]"));
            }
            return ToolArgumentReader.String(command.Arguments, "pipeline", fallback);
        }

        private static string BuildPipelineJson(string stepsJson)
        {
            var sourceSteps = JArray.Parse(string.IsNullOrWhiteSpace(stepsJson) ? "[]" : stepsJson);
            if (sourceSteps.Count == 0) throw new InvalidOperationException("pipelineSteps requires at least one step.");
            var steps = new JArray();
            for (var index = 0; index < sourceSteps.Count; index++)
            {
                var sourceStep = sourceSteps[index] as JObject;
                if (sourceStep == null) throw new InvalidOperationException("pipelineSteps item " + (index + 1) + " must be an object.");
                var toolId = ((string)sourceStep["toolId"] ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(toolId)) throw new InvalidOperationException("pipelineSteps item " + (index + 1) + " requires toolId.");

                var arguments = new JObject();
                var sourceArguments = sourceStep["arguments"] as JArray ?? new JArray();
                foreach (var argumentToken in sourceArguments)
                {
                    var argument = argumentToken as JObject;
                    if (argument == null) throw new InvalidOperationException("pipelineSteps arguments for " + toolId + " must be objects.");
                    var name = ((string)argument["name"] ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Every pipelineSteps argument requires a non-empty name.");
                    if (arguments[name] != null) throw new InvalidOperationException("pipelineSteps contains duplicate argument " + name + " for " + toolId + ".");
                    if (argument["value"] == null) throw new InvalidOperationException("pipelineSteps argument " + name + " requires value; use an explicit JSON null when needed.");
                    arguments[name] = argument["value"].DeepClone();
                }

                var step = new JObject
                {
                    ["toolId"] = toolId,
                    ["arguments"] = arguments
                };
                var id = ((string)sourceStep["id"] ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(id)) step["id"] = id;
                steps.Add(step);
            }
            return new JObject { ["version"] = 1, ["steps"] = steps }.ToString(Formatting.None);
        }

        private static string BuildParameterSchema(string definitionsJson)
        {
            var definitions = JArray.Parse(string.IsNullOrWhiteSpace(definitionsJson) ? "[]" : definitionsJson);
            var properties = new JObject();
            var required = new JArray();
            foreach (var definition in definitions.OfType<JObject>())
            {
                var name = ((string)definition["name"] ?? string.Empty).Trim();
                var type = ((string)definition["type"] ?? string.Empty).Trim();
                var description = ((string)definition["description"] ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Every parameterDefinitions item requires a non-empty name.");
                if (properties[name] != null) throw new InvalidOperationException("parameterDefinitions contains duplicate argument " + name + ".");
                if (string.IsNullOrWhiteSpace(description)) throw new InvalidOperationException("parameterDefinitions." + name + ".description is required.");
                if (!new[] { "string", "integer", "number", "boolean", "array" }.Contains(type, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException("parameterDefinitions." + name + ".type is unsupported.");
                }

                JToken typeToken = type;
                if ((bool?)definition["nullable"] == true) typeToken = new JArray(type, "null");
                var property = new JObject { ["type"] = typeToken, ["description"] = description };
                if (string.Equals(type, "array", StringComparison.Ordinal))
                {
                    var itemsType = ((string)definition["itemsType"] ?? string.Empty).Trim();
                    if (!new[] { "string", "integer", "number", "boolean" }.Contains(itemsType, StringComparer.Ordinal))
                    {
                        throw new InvalidOperationException("parameterDefinitions." + name + ".itemsType is required for an array and must be scalar.");
                    }
                    property["items"] = new JObject { ["type"] = itemsType };
                    CopyNumberConstraint(definition, property, "minItems", true);
                    CopyNumberConstraint(definition, property, "maxItems", true);
                }
                else if (string.Equals(type, "string", StringComparison.Ordinal))
                {
                    var enumValues = definition["enumValues"] as JArray;
                    if (enumValues != null && enumValues.Count > 0)
                    {
                        var allowed = (JArray)enumValues.DeepClone();
                        if ((bool?)definition["nullable"] == true) allowed.Add(JValue.CreateNull());
                        property["enum"] = allowed;
                    }
                    CopyNumberConstraint(definition, property, "minLength", true);
                    CopyNumberConstraint(definition, property, "maxLength", true);
                    CopyDefault(definition, property, "defaultString");
                }
                else if (string.Equals(type, "integer", StringComparison.Ordinal))
                {
                    CopyNumberConstraint(definition, property, "minimum", false);
                    CopyNumberConstraint(definition, property, "maximum", false);
                    CopyDefault(definition, property, "defaultInteger");
                }
                else if (string.Equals(type, "number", StringComparison.Ordinal))
                {
                    CopyNumberConstraint(definition, property, "minimum", false);
                    CopyNumberConstraint(definition, property, "maximum", false);
                    CopyDefault(definition, property, "defaultNumber");
                }
                else
                {
                    CopyDefault(definition, property, "defaultBoolean");
                }
                ValidateConstraintOrder(property, name, "minimum", "maximum");
                ValidateConstraintOrder(property, name, "minLength", "maxLength");
                ValidateConstraintOrder(property, name, "minItems", "maxItems");
                properties[name] = property;
                if ((bool?)definition["required"] == true) required.Add(name);
            }
            if (definitions.Count != properties.Count)
            {
                throw new InvalidOperationException("Every parameterDefinitions item must be an object.");
            }
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }

        private static void CopyDefault(JObject source, JObject target, string name)
        {
            if (source[name] != null && source[name].Type != JTokenType.Null) target["default"] = source[name].DeepClone();
        }

        private static void CopyNumberConstraint(JObject source, JObject target, string name, bool integer)
        {
            var value = source[name];
            if (value == null || value.Type == JTokenType.Null) return;
            if (integer && value.Type != JTokenType.Integer) throw new InvalidOperationException(name + " must be an integer.");
            if (value.Type != JTokenType.Integer && value.Type != JTokenType.Float) throw new InvalidOperationException(name + " must be numeric.");
            target[name] = value.DeepClone();
        }

        private static void ValidateConstraintOrder(JObject schema, string argumentName, string minimumName, string maximumName)
        {
            if (schema[minimumName] != null && schema[maximumName] != null &&
                schema[minimumName].Value<double>() > schema[maximumName].Value<double>())
            {
                throw new InvalidOperationException("parameterDefinitions." + argumentName + " has " + minimumName + " greater than " + maximumName + ".");
            }
        }

        private static ToolDefinition NormalizeVbaEntryCode(ToolDefinition tool)
        {
            if (tool != null && string.Equals(tool.Executor, "vba", StringComparison.OrdinalIgnoreCase))
            {
                var entry = (tool.Components ?? new List<VbaToolComponent>()).FirstOrDefault();
                tool.Code = entry == null ? string.Empty : entry.Code ?? string.Empty;
            }
            return tool;
        }

        private static List<VbaToolComponent> ReadComponents(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<VbaToolComponent>();
            try
            {
                return JArray.Parse(json).OfType<JObject>().Select(component => new VbaToolComponent
                {
                    Name = (string)component["name"],
                    Type = (string)component["type"],
                    FileName = (string)component["fileName"],
                    Code = (string)component["code"] ?? string.Empty
                }).ToList();
            }
            catch (JsonException)
            {
                return new List<VbaToolComponent>();
            }
        }

        private static JObject ToolPayload(ToolDefinition tool)
        {
            tool = tool ?? new ToolDefinition();
            return new JObject
            {
                ["id"] = tool.Id ?? string.Empty,
                ["host"] = tool.Host ?? string.Empty,
                ["name"] = tool.Name ?? string.Empty,
                ["description"] = tool.Description ?? string.Empty,
                ["parameters"] = ParseJsonObject(tool.ArgumentSchemaJson),
                ["executor"] = tool.Executor ?? string.Empty,
                ["pipeline"] = ParseJsonObject(tool.PipelineJson),
                ["components"] = new JArray((tool.Components ?? new List<VbaToolComponent>())
                    .Where(component => component != null)
                    .Select(component => new JObject
                    {
                        ["name"] = component.Name ?? string.Empty,
                        ["type"] = component.Type ?? string.Empty,
                        ["fileName"] = component.FileName ?? string.Empty,
                        ["code"] = component.Code ?? string.Empty
                    })),
                ["readme"] = tool.Readme ?? string.Empty,
                ["enabled"] = tool.Enabled,
                ["requiresConfirmation"] = tool.RequiresConfirmation,
                ["mutatesDocument"] = tool.MutatesDocument,
                ["mutatesLocalState"] = tool.MutatesLocalState,
                ["agentCanRun"] = tool.AgentCanRun,
                ["riskLevel"] = tool.RiskLevel,
                ["useWhen"] = tool.UseWhen ?? string.Empty,
                ["doNotUseWhen"] = tool.DoNotUseWhen ?? string.Empty,
                ["capabilityStatus"] = tool.CapabilityStatus ?? "available",
                ["limitations"] = tool.Limitations ?? string.Empty
            };
        }

        private static JToken ParseJsonObject(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return JValue.CreateNull();
            try
            {
                var parsed = JToken.Parse(json);
                return parsed.Type == JTokenType.Object ? parsed : JValue.CreateNull();
            }
            catch (JsonException)
            {
                return JValue.CreateNull();
            }
        }

        private static bool HasArgument(ToolCommand command, string name)
        {
            return command != null && command.Arguments != null && command.Arguments.ContainsKey(name);
        }

        private static bool HasMutableArguments(ToolCommand command)
        {
            return command != null && command.Arguments != null && command.Arguments.Keys.Any(name =>
                !string.Equals(name, "id", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, "mode", StringComparison.OrdinalIgnoreCase));
        }

        private static void SetString(ToolCommand command, string name, Action<string> apply)
        {
            if (HasArgument(command, name) && apply != null) apply(ToolArgumentReader.String(command.Arguments, name, string.Empty));
        }

        private static void SetBool(ToolCommand command, string name, Action<bool> apply)
        {
            if (HasArgument(command, name) && apply != null) apply(ReadBool(command, name, false));
        }

        private static int ReadInt(ToolCommand command, string name, int fallback)
        {
            if (command == null || command.Arguments == null || !command.Arguments.ContainsKey(name) || command.Arguments[name] == null)
            {
                return fallback;
            }
            int value;
            return int.TryParse(Convert.ToString(command.Arguments[name]), out value) ? value : fallback;
        }

        private IEnumerable<ToolDefinition> VisibleTools()
        {
            return _toolStore.Load().Where(t =>
                t != null &&
                !t.BuiltIn &&
                (string.Equals(t.Host, _adapter.HostName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(t.Host, "Common", StringComparison.OrdinalIgnoreCase)));
        }

        private static string DefaultHostFromId(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return "Common";
            }

            var dot = id.IndexOf('.');
            if (dot <= 0)
            {
                return "Common";
            }

            var prefix = id.Substring(0, dot);
            return string.Equals(prefix, "common", StringComparison.OrdinalIgnoreCase) ? "Common" : prefix;
        }

        private static bool ReadBool(ToolCommand command, string name, bool fallback)
        {
            var raw = ToolArgumentReader.String(command.Arguments, name, fallback ? "true" : "false");
            bool value;
            return bool.TryParse(raw, out value) ? value : fallback;
        }

    }
}
