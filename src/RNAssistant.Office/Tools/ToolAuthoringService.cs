using RNAssistant.Core.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class ToolAuthoringService
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly ToolStore _toolStore;
        private readonly Func<string, bool> _isProtectedToolId;

        internal ToolAuthoringService(
            IOfficeApplicationAdapter adapter,
            ToolStore toolStore,
            Func<string, bool> isProtectedToolId)
        {
            _adapter = adapter;
            _toolStore = toolStore;
            _isProtectedToolId = isProtectedToolId;
        }

        internal bool CanUse { get { return _toolStore != null; } }

        internal ToolAuthoringOutcome Read(
            IDictionary<string, object> arguments)
        {
            if (_toolStore == null)
            {
                return ToolAuthoringOutcome.Error(
                    "Tool authoring store is not available.", null,
                    "tool_store_unavailable", false);
            }
            var id = ToolArgumentReader.String(
                arguments, "id", string.Empty);
            if (string.IsNullOrWhiteSpace(id))
            {
                return ListTools();
            }
            var tool = VisibleTools().FirstOrDefault(candidate =>
                string.Equals(candidate.Id, id,
                    StringComparison.OrdinalIgnoreCase));
            if (tool == null)
            {
                return ToolAuthoringOutcome.Error(
                    "Custom tool not found: " + id, null,
                    "tool_not_found", false);
            }
            return ToolAuthoringOutcome.Ok(
                "Custom tool read: " + tool.Id,
                ToolPayload(tool).ToString(Formatting.None));
        }

        internal ToolAuthoringOutcome Validate(
            IDictionary<string, object> arguments)
        {
            var parameterError = ValidateParameterInput(arguments);
            if (parameterError != null) return parameterError;
            var tool = ReadToolDefinition(arguments);
            var reserved = ValidateAuthoredToolId(tool.Id);
            if (reserved != null) return reserved;
            var validation = ValidateToolDefinition(tool);
            if (!validation.Success) return validation;
            return ToolAuthoringOutcome.Ok(
                "Tool definition is valid: " + tool.Id,
                ToolPayload(tool).ToString(Formatting.None));
        }

        internal ToolAuthoringOutcome ValidateDefinition(
            ToolCatalogEntry tool)
        {
            var reserved = ValidateAuthoredToolId(
                tool == null ? null : tool.Id);
            return reserved ?? ValidateToolDefinition(tool);
        }

        private ToolAuthoringOutcome ListTools()
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
            return ToolAuthoringOutcome.Ok(
                "Custom tools listed.", JsonConvert.SerializeObject(tools));
        }

        private static ToolCatalogEntry ReadToolDefinition(
            IDictionary<string, object> arguments)
        {
            var id = ToolArgumentReader.String(arguments, "id", string.Empty);
            var components = ReadComponents(ToolArgumentReader.String(
                arguments, "components", "[]"));
            return NormalizeVbaEntryCode(new ToolCatalogEntry
            {
                Id = id,
                Host = ToolArgumentReader.String(arguments, "host", DefaultHostFromId(id)),
                Name = ToolArgumentReader.String(arguments, "name", id),
                Description = ToolArgumentReader.String(arguments, "description", string.Empty),
                ArgumentSchemaJson = ResolveParameterSchema(arguments, "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}"),
                Executor = ToolArgumentReader.String(arguments, "executor", "vba"),
                Readme = ToolArgumentReader.String(arguments, "readme", string.Empty),
                Enabled = ReadBool(arguments, "enabled", true),
                RequiresConfirmation = ReadBool(arguments, "requiresConfirmation", false),
                MutatesDocument = ReadBool(arguments, "mutatesDocument", false),
                MutatesLocalState = ReadBool(arguments, "mutatesLocalState", false),
                AgentCanRun = ReadBool(arguments, "agentCanRun", true),
                BuiltIn = false,
                RiskLevel = ReadInt(arguments, "riskLevel", 0),
                UseWhen = ToolArgumentReader.String(arguments, "useWhen", string.Empty),
                DoNotUseWhen = ToolArgumentReader.String(arguments, "doNotUseWhen", string.Empty),
                CapabilityStatus = ToolArgumentReader.String(arguments, "capabilityStatus", "available"),
                Limitations = ToolArgumentReader.String(arguments, "limitations", string.Empty),
                Components = components
            });
        }

        private static ToolCatalogEntry UpdateToolDefinition(
            ToolCatalogEntry existing,
            IDictionary<string, object> arguments)
        {
            var tool = existing.Clone();
            tool.StoragePath = existing.StoragePath;
            SetString(arguments, "host", value => tool.Host = value);
            SetString(arguments, "name", value => tool.Name = value);
            SetString(arguments, "description", value => tool.Description = value);
            if (HasArgument(arguments, "parameters") || HasArgument(arguments, "parameterDefinitions"))
            {
                tool.ArgumentSchemaJson = ResolveParameterSchema(arguments, tool.ArgumentSchemaJson);
            }
            SetString(arguments, "executor", value => tool.Executor = value);

            SetString(arguments, "readme", value => tool.Readme = value);
            SetString(arguments, "useWhen", value => tool.UseWhen = value);
            SetString(arguments, "doNotUseWhen", value => tool.DoNotUseWhen = value);
            SetString(arguments, "capabilityStatus", value => tool.CapabilityStatus = value);
            SetString(arguments, "limitations", value => tool.Limitations = value);
            SetBool(arguments, "enabled", value => tool.Enabled = value);
            SetBool(arguments, "requiresConfirmation", value => tool.RequiresConfirmation = value);
            SetBool(arguments, "mutatesDocument", value => tool.MutatesDocument = value);
            SetBool(arguments, "mutatesLocalState", value => tool.MutatesLocalState = value);
            SetBool(arguments, "agentCanRun", value => tool.AgentCanRun = value);
            if (HasArgument(arguments, "riskLevel")) tool.RiskLevel = ReadInt(arguments, "riskLevel", tool.RiskLevel);
            if (HasArgument(arguments, "components")) tool.Components = ReadComponents(ToolArgumentReader.String(arguments, "components", "[]"));
            return NormalizeVbaEntryCode(tool);
        }

        private static ToolAuthoringOutcome ValidateParameterInput(
            IDictionary<string, object> arguments)
        {
            if (HasArgument(arguments, "parameters") && HasArgument(arguments, "parameterDefinitions"))
            {
                return ToolAuthoringOutcome.Error(
                    "Supply either parameters or parameterDefinitions, not both. Prefer parameterDefinitions in Agent mode.",
                    null,
                    "tool_parameters_ambiguous",
                    true);
            }
            if (!HasArgument(arguments, "parameterDefinitions")) return null;
            try
            {
                BuildParameterSchema(ToolArgumentReader.String(arguments, "parameterDefinitions", "[]"));
                return null;
            }
            catch (InvalidOperationException ex)
            {
                return ToolAuthoringOutcome.Error(ex.Message, null, "invalid_tool_parameter_definitions", true);
            }
            catch (JsonException ex)
            {
                return ToolAuthoringOutcome.Error("parameterDefinitions must be a native JSON array: " + ex.Message, null, "invalid_tool_parameter_definitions", true);
            }
        }

        private static string ResolveParameterSchema(
            IDictionary<string, object> arguments, string fallback)
        {
            if (HasArgument(arguments, "parameterDefinitions"))
            {
                return BuildParameterSchema(ToolArgumentReader.String(arguments, "parameterDefinitions", "[]"));
            }
            return ToolArgumentReader.String(arguments, "parameters", fallback);
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

        private static ToolCatalogEntry NormalizeVbaEntryCode(ToolCatalogEntry tool)
        {
            if (tool != null && string.Equals(tool.Executor, "vba", StringComparison.OrdinalIgnoreCase))
            {
                var entry = (tool.Components ?? new List<ToolPackageComponentDefinition>()).FirstOrDefault();
                tool.Code = entry == null ? string.Empty : entry.Code ?? string.Empty;
            }
            return tool;
        }

        private static List<ToolPackageComponentDefinition> ReadComponents(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<ToolPackageComponentDefinition>();
            try
            {
                return JArray.Parse(json).OfType<JObject>().Select(component => new ToolPackageComponentDefinition
                {
                    Name = (string)component["name"],
                    Type = (string)component["type"],
                    FileName = (string)component["fileName"],
                    Code = (string)component["code"] ?? string.Empty
                }).ToList();
            }
            catch (JsonException)
            {
                return new List<ToolPackageComponentDefinition>();
            }
        }

        private static JObject ToolPayload(ToolCatalogEntry tool)
        {
            tool = tool ?? new ToolCatalogEntry();
            return new JObject
            {
                ["id"] = tool.Id ?? string.Empty,
                ["host"] = tool.Host ?? string.Empty,
                ["name"] = tool.Name ?? string.Empty,
                ["description"] = tool.Description ?? string.Empty,
                ["parameters"] = ParseJsonObject(tool.ArgumentSchemaJson),
                ["executor"] = tool.Executor ?? string.Empty,
                ["components"] = new JArray((tool.Components ?? new List<ToolPackageComponentDefinition>())
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

        private static bool HasArgument(
            IDictionary<string, object> arguments, string name)
        {
            return arguments != null && arguments.ContainsKey(name);
        }

        private static bool HasMutableArguments(
            IDictionary<string, object> arguments)
        {
            return arguments != null && arguments.Keys.Any(name =>
                !string.Equals(name, "id", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, "mode", StringComparison.OrdinalIgnoreCase));
        }

        private static void SetString(
            IDictionary<string, object> arguments,
            string name, Action<string> apply)
        {
            if (HasArgument(arguments, name) && apply != null)
                apply(ToolArgumentReader.String(
                    arguments, name, string.Empty));
        }

        private static void SetBool(
            IDictionary<string, object> arguments,
            string name, Action<bool> apply)
        {
            if (HasArgument(arguments, name) && apply != null)
                apply(ReadBool(arguments, name, false));
        }

        private static int ReadInt(
            IDictionary<string, object> arguments,
            string name, int fallback)
        {
            if (arguments == null || !arguments.ContainsKey(name) ||
                arguments[name] == null)
            {
                return fallback;
            }
            int value;
            return int.TryParse(Convert.ToString(arguments[name]), out value)
                ? value : fallback;
        }

        private IEnumerable<ToolCatalogEntry> VisibleTools()
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

        private static bool ReadBool(
            IDictionary<string, object> arguments,
            string name, bool fallback)
        {
            var raw = ToolArgumentReader.String(
                arguments, name, fallback ? "true" : "false");
            bool value;
            return bool.TryParse(raw, out value) ? value : fallback;
        }

    }
}
