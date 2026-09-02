using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    // Durable results retain exact runtime evidence. This projection is the only
    // representation of switched resource/capability results sent to a model.
    internal static class ModelToolResultProjection
    {
        private const string Prefix = "TOOL_RESULT:\n";

        internal static ChatMessage Project(
            ChatMessage source,
            IEnumerable<ToolCatalogEntry> tools = null,
            IEnumerable<SkillDefinition> skills = null)
        {
            var projected = HistoricalContextProjector.Project(source);
            if (projected == null) return null;
            projected.ResourceRefs = new List<ResourceRef>();
            projected.HtmlWorkspaceCheckpoint = null;
            if (!IsSwitchedResult(source))
            {
                if (source.ToolResultProtocolVersion != ToolResultWire.CurrentVersion)
                    projected.Content = source.Content;
                return projected;
            }

            ToolResultWireReadResult wire;
            string error;
            if (!ToolResultHistoryReader.TryRead(source, out wire, out error))
            {
                return InvalidSwitchedResult(projected, source);
            }

            var data = ParseData(wire.Result.DataJson);
            var status = wire.Result.Status;
            var message = wire.Result.Message;
            if (IsCapabilityResult(wire.Name) && status == ToolResultStatus.Ok &&
                tools != null && skills != null &&
                !MatchesCurrentCapability(data, tools, skills))
            {
                status = ToolResultStatus.Error;
                message = "Capability evidence no longer matches the current catalog. Read the exact id again.";
                data = StaleCapabilityData(data);
            }
            else if (IsResourceResult(wire.Name))
            {
                RemoveResourceRuntimeState(data);
            }
            else
            {
                RemoveCapabilityRuntimeState(data);
            }

            var modelResult = new RNAssistant.Core.Tools.Contracts.ToolResult(
                status,
                message,
                data == null ? "null" : data.ToString(Formatting.None),
                new ResourceRef[0]);
            var json = ToolResultWire.Write(wire.ToolCallId, wire.Name, modelResult);
            projected.Content = string.Equals(projected.Role, ToolResultRoles.Tool, StringComparison.Ordinal)
                ? json
                : Prefix + json;
            return projected;
        }

        private static ChatMessage InvalidSwitchedResult(
            ChatMessage projected,
            ChatMessage source)
        {
            var name = CanonicalSwitchedName(source == null ? null : source.ToolName);
            var callId = source == null ? null : source.ToolCallId;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(callId))
            {
                projected.Content = string.Empty;
                projected.ExcludeFromModelContext = true;
                return projected;
            }
            var result = RNAssistant.Core.Tools.Contracts.ToolResult.Error(
                "Stored tool evidence is invalid and cannot be replayed. Run the semantic read again.",
                new JObject
                {
                    ["code"] = "tool_result_projection_invalid"
                }.ToString(Formatting.None));
            var json = ToolResultWire.Write(callId, name, result);
            projected.Content = string.Equals(projected.Role, ToolResultRoles.Tool, StringComparison.Ordinal)
                ? json
                : Prefix + json;
            return projected;
        }

        private static string CanonicalSwitchedName(string name)
        {
            if (IsResourceResult(name) || IsCapabilityResult(name)) return name;
            return null;
        }

        internal static bool ValidateAcceptedCall(ToolCall call, out string error)
        {
            error = null;
            if (call == null || string.IsNullOrWhiteSpace(call.Name)) return true;
            string schema;
            switch (call.Name)
            {
                case "common.resources_list":
                case "common.resources_resolve":
                case "common.resources_search":
                    error = "Public resource list/resolve/search calls were retired by 11O1.";
                    return false;
                case ResourceToolCatalog.FindToolId:
                    schema = ResourceFindToolHandler.Descriptor.ParametersJson;
                    break;
                case ResourceToolCatalog.ReadToolId:
                    schema = ResourceReadToolHandler.Descriptor.ParametersJson;
                    break;
                case CapabilityToolCatalog.SearchToolId:
                    schema = CapabilityCatalogService.SearchSchema();
                    break;
                case CapabilityToolCatalog.ReadToolId:
                    schema = CapabilityCatalogService.ReadSchema(null, null);
                    break;
                default:
                    return true;
            }

            try
            {
                var arguments = JsonConvert.DeserializeObject<JObject>(
                    call.ArgumentsJson ?? "{}",
                    new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
                var parsedSchema = JObject.Parse(schema);
                string validationError;
                if (arguments != null && ToolSchemaSupport.ValidateArguments(
                    arguments, parsedSchema, false, out validationError)) return true;
                error = "Stored " + call.Name + " arguments do not match the current semantic schema.";
                return false;
            }
            catch (JsonException)
            {
                error = "Stored " + call.Name + " arguments are not a JSON object for the current semantic schema.";
                return false;
            }
        }

        private static bool IsSwitchedResult(ChatMessage message)
        {
            return message != null && CanonicalSwitchedName(message.ToolName) != null;
        }

        private static bool IsResourceResult(string name)
        {
            return string.Equals(name, ResourceToolCatalog.FindToolId, StringComparison.Ordinal) ||
                string.Equals(name, ResourceToolCatalog.ReadToolId, StringComparison.Ordinal);
        }

        private static bool IsCapabilityResult(string name)
        {
            return string.Equals(name, CapabilityToolCatalog.SearchToolId, StringComparison.Ordinal) ||
                string.Equals(name, CapabilityToolCatalog.ReadToolId, StringComparison.Ordinal);
        }

        private static JToken ParseData(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return JValue.CreateNull();
            try
            {
                return JsonConvert.DeserializeObject<JToken>(value,
                    new JsonSerializerSettings { DateParseHandling = DateParseHandling.None }) ??
                    JValue.CreateNull();
            }
            catch (JsonException)
            {
                return JValue.CreateNull();
            }
        }

        private static void RemoveResourceRuntimeState(JToken token)
        {
            var value = token as JObject;
            if (value != null)
            {
                foreach (var property in value.Properties().ToList())
                {
                    if (IsResourceRuntimeField(property.Name)) property.Remove();
                    else RemoveResourceRuntimeState(property.Value);
                }
                return;
            }
            var array = token as JArray;
            if (array == null) return;
            foreach (var item in array) RemoveResourceRuntimeState(item);
        }

        private static bool IsResourceRuntimeField(string name)
        {
            var normalized = (name ?? string.Empty).Replace("_", string.Empty).ToLowerInvariant();
            if (normalized == "id" || normalized.EndsWith("id", StringComparison.Ordinal) ||
                normalized.EndsWith("ids", StringComparison.Ordinal)) return true;
            return normalized == "resource" || normalized == "resources" ||
                normalized == "uri" || normalized.EndsWith("uri", StringComparison.Ordinal) ||
                normalized == "provider" || normalized.EndsWith("provider", StringComparison.Ordinal) ||
                normalized.Contains("revision") || normalized.Contains("cursor") ||
                normalized.Contains("offset") || normalized.Contains("hash") ||
                normalized.Contains("fingerprint") || normalized == "etag" ||
                normalized == "position" || normalized == "pagesize" ||
                normalized == "progresscharacters";
        }

        private static void RemoveCapabilityRuntimeState(JToken token)
        {
            var root = token as JObject;
            if (root == null) return;
            RemoveProperties(root,
                "catalogRevision", "revision", "skillRevision", "snapshotRevision",
                "previousSnapshotRevision", "admission", "progressCharacters");
            var items = root["items"] as JArray;
            if (items != null)
            {
                foreach (var item in items.OfType<JObject>())
                    RemoveProperties(item, "revision", "catalogRevision");
            }
            var references = root["references"] as JArray;
            if (references != null)
            {
                foreach (var item in references.OfType<JObject>())
                    RemoveProperties(item, "revision", "skillRevision");
            }
        }

        private static void RemoveProperties(JObject value, params string[] names)
        {
            foreach (var name in names) value.Property(name)?.Remove();
        }

        private static bool MatchesCurrentCapability(
            JToken token,
            IEnumerable<ToolCatalogEntry> tools,
            IEnumerable<SkillDefinition> skills)
        {
            var data = token as JObject;
            if (data == null) return true;
            var toolCatalog = (tools ?? new ToolCatalogEntry[0]).ToList();
            var skillCatalog = (skills ?? new SkillDefinition[0]).ToList();
            var kind = (string)data["kind"] ?? string.Empty;
            if (string.Equals(kind, "capability-search", StringComparison.Ordinal))
            {
                return string.Equals(
                    (string)data["catalogRevision"],
                    CapabilityCatalogService.CatalogRevision(toolCatalog, skillCatalog),
                    StringComparison.Ordinal);
            }

            var id = (string)data["id"] ?? string.Empty;
            if (string.Equals(kind, "tool-schema", StringComparison.Ordinal))
            {
                var tool = toolCatalog.FirstOrDefault(item => item != null &&
                    string.Equals(item.Id, id, StringComparison.Ordinal));
                return tool != null && string.Equals(
                    (string)data["revision"],
                    CapabilityCatalogService.Revision(tool),
                    StringComparison.Ordinal);
            }
            var skill = skillCatalog.FirstOrDefault(item => item != null && item.Enabled &&
                string.Equals(item.Id, id, StringComparison.Ordinal));
            if (string.Equals(kind, "skill", StringComparison.Ordinal))
            {
                return skill != null && string.Equals(
                    (string)data["revision"], SkillRevision.Compute(skill), StringComparison.Ordinal);
            }
            if (string.Equals(kind, "reference", StringComparison.Ordinal))
            {
                var path = (string)data["path"] ?? string.Empty;
                var references = skill == null
                    ? (IEnumerable<SkillReferenceMetadata>)new SkillReferenceMetadata[0]
                    : skill.References ?? new List<SkillReferenceMetadata>();
                var reference = references
                    .FirstOrDefault(item => item != null &&
                        string.Equals(item.Path, path, StringComparison.Ordinal));
                return skill != null && reference != null &&
                    string.Equals((string)data["skillRevision"], SkillRevision.Compute(skill), StringComparison.Ordinal) &&
                    string.Equals((string)data["revision"], reference.Revision, StringComparison.Ordinal);
            }
            return true;
        }

        private static JObject StaleCapabilityData(JToken source)
        {
            var original = source as JObject;
            return new JObject
            {
                ["kind"] = original == null || original["kind"] == null
                    ? JValue.CreateNull() : original["kind"].DeepClone(),
                ["id"] = original == null || original["id"] == null
                    ? JValue.CreateNull() : original["id"].DeepClone(),
                ["path"] = original == null || original["path"] == null
                    ? JValue.CreateNull() : original["path"].DeepClone(),
                ["code"] = "capability_evidence_stale",
                ["loaded"] = false,
                ["complete"] = false
            };
        }
    }
}
