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

        internal ToolAuthoringOutcome ValidateDefinition(
            ToolCatalogEntry tool)
        {
            var reserved = ValidateAuthoredToolId(
                tool == null ? null : tool.Id);
            return reserved ?? ValidateToolDefinition(tool);
        }

        private static ToolCatalogEntry ReadToolDefinition(
            IDictionary<string, object> arguments)
        {
            var id = ToolArgumentReader.String(arguments, "id", string.Empty);
            var components = ReadComponents(arguments);
            var tool = NormalizeVbaEntryCode(new ToolCatalogEntry
            {
                Id = id,
                // The model-facing contract does not own host. Common is only a
                // pre-validation placeholder; a valid manifest replaces it.
                Host = ToolArgumentReader.String(arguments, "host", "Common"),
                Name = ToolArgumentReader.String(arguments, "name", id),
                Description = ToolArgumentReader.String(arguments, "description", string.Empty),
                ArgumentSchemaJson = ToolArgumentReader.String(arguments,
                    "parameters", "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}"),
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
            var manifest = new VbaToolManifestParser().Parse(tool.Code);
            if (manifest.Success)
                tool.Host = manifest.Tool.Host;
            return tool;
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
            if (HasArgument(arguments, "parameters"))
                tool.ArgumentSchemaJson = ToolArgumentReader.String(
                    arguments, "parameters", tool.ArgumentSchemaJson);
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
            if (HasArgument(arguments, "components"))
                tool.Components = ReadComponents(arguments);
            return NormalizeVbaEntryCode(tool);
        }

        private static ToolCatalogEntry NormalizeVbaEntryCode(ToolCatalogEntry tool)
        {
            if (tool != null && string.Equals(tool.Executor, "vba", StringComparison.OrdinalIgnoreCase))
            {
                var entry = (tool.Components ?? new List<ToolPackageComponentDefinition>()).FirstOrDefault();
                if (entry == null)
                {
                    tool.Code = string.Empty;
                }
                else
                {
                    entry.Code = VbaToolManifestParser.NormalizeManifestComments(
                        entry.Code);
                    tool.Code = entry.Code ?? string.Empty;
                }
            }
            return tool;
        }

        private static List<ToolPackageComponentDefinition> ReadComponents(
            IDictionary<string, object> arguments)
        {
            object raw;
            var components = arguments != null &&
                arguments.TryGetValue("components", out raw)
                ? raw as JArray : null;
            if (components == null)
                return new List<ToolPackageComponentDefinition>();
            return components.OfType<JObject>()
                .Select(component => new ToolPackageComponentDefinition
                {
                    Name = (string)component["name"],
                    Type = (string)component["type"],
                    FileName = (string)component["fileName"],
                    Code = (string)component["code"] ?? string.Empty
                }).ToList();
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

        private static bool HasInternalPolicyArguments(
            IDictionary<string, object> arguments)
        {
            return new[]
            {
                "host", "name", "description", "parameters",
                "executor", "enabled",
                "requiresConfirmation", "mutatesDocument",
                "mutatesLocalState", "agentCanRun", "riskLevel",
                "capabilityStatus"
            }.Any(name => HasArgument(arguments, name));
        }

        private static void ApplyConservativeAuthoringPolicy(
            ToolCatalogEntry tool)
        {
            if (tool == null) return;
            tool.RequiresConfirmation = true;
            tool.MutatesDocument = true;
            tool.MutatesLocalState = true;
            tool.RiskLevel = 3;
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
