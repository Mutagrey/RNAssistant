using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
    // representation of results from each R61-switched family sent to a model.
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

            var data = ToolResultWire.ParseData(wire.Result.DataJson);
            var materialized = new ToolResultMaterialization(
                wire.Result, resultResource: wire.ResultResource, data: data);
            var model = ForModel(wire.Name, materialized, tools, skills);
            var json = ToolResultWire.WriteParsed(
                wire.ToolCallId,
                wire.Name,
                model.Result,
                model.Data,
                model.ResultResource);
            projected.Content = string.Equals(projected.Role, ToolResultRoles.Tool, StringComparison.Ordinal)
                ? json
                : Prefix + json;
            return projected;
        }

        internal static ToolResultMaterialization ForModel(
            string name,
            ToolResultMaterialization source,
            IEnumerable<ToolCatalogEntry> tools = null,
            IEnumerable<SkillDefinition> skills = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (CanonicalSwitchedName(name) == null) return source;
            var data = source.Data.DeepClone();
            var status = source.Result.Status;
            var message = source.Result.Message;
            if (IsCapabilityResult(name) && status == ToolResultStatus.Ok &&
                tools != null && skills != null &&
                !MatchesCurrentCapability(data, tools, skills))
            {
                status = ToolResultStatus.Error;
                message = "Capability evidence no longer matches the current catalog. Read the exact id again.";
                data = StaleCapabilityData(data);
            }
            else if (IsResourceResult(name))
            {
                RemoveResourceRuntimeState(data);
            }
            else if (IsPlanningResult(name))
            {
                RemovePlanningRuntimeState(name, data);
            }
            else if (IsHtmlResult(name))
            {
                RemoveHtmlRuntimeState(data);
            }
            else if (IsAuthoringResult(name))
            {
                RemoveAuthoringRuntimeState(data);
            }
            else if (IsVbaResult(name))
            {
                message = SanitizeVbaMessage(message, data);
                RemoveVbaRuntimeState(data);
            }
            else
            {
                RemoveCapabilityRuntimeState(data);
            }

            var result = new RNAssistant.Core.Tools.Contracts.ToolResult(
                status,
                message,
                data == null ? "null" : data.ToString(Formatting.None),
                new ResourceRef[0]);
            return new ToolResultMaterialization(
                result, source.ModelAttachments, data: data);
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
            if (IsResourceResult(name) || IsCapabilityResult(name) ||
                IsPlanningResult(name) || IsHtmlResult(name) ||
                IsAuthoringResult(name) || IsVbaResult(name)) return name;
            return null;
        }

        internal static bool ValidateAcceptedCall(ToolCall call, out string error)
        {
            error = null;
            if (call == null || string.IsNullOrWhiteSpace(call.Name)) return true;
            if (call.Name.StartsWith("rna_", StringComparison.OrdinalIgnoreCase))
            {
                error = "Synthetic rna_* tool names are not part of the public catalog.";
                return false;
            }
            string schema;
            switch (call.Name)
            {
                case "common.resources_list":
                case "common.resources_resolve":
                case "common.resources_search":
                    error = "Public resource list/resolve/search calls were retired by 11O1.";
                    return false;
                case "common.plan_doc_create":
                case "common.plan_doc_update":
                    error = "Public Plan create/update calls were replaced by common.plan_doc_save in 11O2.";
                    return false;
                case "common.task_list_create":
                case "common.task_list_update":
                case "common.task_list_close":
                    error = "Public Task List lifecycle calls were replaced by common.task_list_set in 11O2.";
                    return false;
                case "common.html_workspace_inspect":
                    error = "Public HTML inspection was internalized in 11O3.";
                    return false;
                case "common.html_workspace_set_active":
                    error = "Public HTML preview selection was internalized in 11O3.";
                    return false;
                case "common.html_workspace_upsert":
                case "common.html_workspace_upsert_file":
                    error = "Public HTML upsert was split into semantic file/data writes in 11O3.";
                    return false;
                case "common.tools_validate":
                    error = "Tool validation is internal to upsert and Library in 11O4.";
                    return false;
                case "common.tools_definition_read":
                    error = "Tool source reads use common.resources_find/read and exact catalog evidence.";
                    return false;
                case "common.prompts_read":
                    error = "Prompt reads use common.resources_find/read and exact catalog evidence.";
                    return false;
                case "excel.read_range":
                    error = "Excel range reads use common.resources_read and exact document evidence.";
                    return false;
                case "powerpoint.read_slides":
                    error = "PowerPoint slide reads use common.resources_read and exact document evidence.";
                    return false;
                case "word.read_text":
                    error = "Word text reads use common.resources_read and exact document evidence.";
                    return false;
                case "common.vba_inspect":
                case "common.vba_list_modules":
                case "common.vba_read_module":
                case "common.vba_read_lines":
                case "common.vba_search_code":
                case "common.vba_create_module":
                case "common.vba_replace_text":
                case "common.vba_list_backups":
                case "excel.run_macro":
                case "word.run_macro":
                case "powerpoint.run_macro":
                    error = "Legacy VBA discovery, mutation, and host macro ids are not part of the semantic public catalog.";
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
                case UserQuestionToolCatalog.AskToolId:
                    schema = UserQuestionToolCatalog.Schema();
                    break;
                case PlanDocumentToolCatalog.SaveToolId:
                case PlanDocumentToolCatalog.RestoreToolId:
                case PlanDocumentToolCatalog.DeleteToolId:
                    schema = PlanDocumentToolCatalog.SchemaFor(call.Name);
                    break;
                case TaskListToolCatalog.SetToolId:
                    schema = TaskListToolCatalog.Schema();
                    break;
                case HtmlWorkspaceToolCatalog.WriteFileToolId:
                    schema = HtmlWorkspaceToolService.WriteFileSchema();
                    break;
                case HtmlWorkspaceToolCatalog.WriteDataToolId:
                    schema = HtmlWorkspaceToolService.WriteDataSchema();
                    break;
                case HtmlWorkspaceToolCatalog.ApplyPatchToolId:
                    schema = HtmlWorkspaceToolService.ApplyPatchSchema();
                    break;
                case HtmlWorkspaceToolCatalog.DeleteToolId:
                    schema = HtmlWorkspaceToolCatalog.DeleteSchema();
                    break;
                case HtmlWorkspaceToolCatalog.BindDataToolId:
                    schema = HtmlWorkspaceToolService.BindSchema();
                    break;
                case HtmlWorkspaceToolCatalog.RefreshDataToolId:
                    schema = HtmlWorkspaceToolCatalog.RefreshSchema();
                    break;
                case HtmlWorkspaceToolCatalog.FreezeDataToolId:
                    schema = HtmlWorkspaceToolCatalog.FreezeSchema();
                    break;
                case PromptToolCatalog.SaveToolId:
                    schema = PromptToolCatalog.SchemaFor(call.Name);
                    break;
                case ToolAuthoringCatalog.UpsertToolId:
                case ToolAuthoringCatalog.DeleteToolId:
                    schema = ToolAuthoringCatalog.SchemaFor(call.Name);
                    break;
                case SkillAuthoringCatalog.UpsertToolId:
                case SkillAuthoringCatalog.DeleteToolId:
                case SkillAuthoringCatalog.ReferenceUpsertToolId:
                case SkillAuthoringCatalog.ReferenceDeleteToolId:
                    schema = SkillAuthoringCatalog.SchemaFor(call.Name);
                    break;
                case VbaToolCatalog.RestoreBackup:
                case VbaToolCatalog.WriteModule:
                case VbaToolCatalog.RenameModule:
                case VbaToolCatalog.ApplyPatch:
                case VbaToolCatalog.DeleteModule:
                case VbaToolCatalog.RunMacro:
                    schema = VbaToolCatalog.SchemaFor(call.Name);
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
                    arguments, parsedSchema, false, out validationError))
                {
                    if (string.Equals(call.Name, ResourceToolCatalog.ReadToolId,
                            StringComparison.Ordinal) &&
                        ResourceGatewayService.IsRuntimeOwnedIntentTarget(
                            (string)arguments["target"]))
                    {
                        error = "Stored common.resources_read target contains a runtime-owned URI.";
                        return false;
                    }
                    return true;
                }
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

        private static bool IsPlanningResult(string name)
        {
            return string.Equals(name, UserQuestionToolCatalog.AskToolId,
                    StringComparison.Ordinal) ||
                PlanDocumentToolCatalog.Owns(name) ||
                TaskListToolCatalog.Owns(name);
        }

        private static bool IsHtmlResult(string name)
        {
            return HtmlWorkspaceToolCatalog.Owns(name);
        }

        private static bool IsAuthoringResult(string name)
        {
            return PromptToolCatalog.Owns(name) ||
                ToolAuthoringCatalog.Owns(name) ||
                SkillAuthoringCatalog.Owns(name);
        }

        private static bool IsVbaResult(string name)
        {
            return VbaToolCatalog.Owns(name);
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

        private static void RemovePlanningRuntimeState(string name, JToken token)
        {
            var root = token as JObject;
            if (root == null) return;
            if (string.Equals(name, UserQuestionToolCatalog.AskToolId,
                StringComparison.Ordinal))
            {
                RemoveProperties(root, "questionSetId");
                foreach (var question in (root["questions"] as JArray ??
                    new JArray()).OfType<JObject>())
                {
                    RemoveProperties(question, "id");
                    foreach (var option in (question["options"] as JArray ??
                        new JArray()).OfType<JObject>())
                        RemoveProperties(option, "id");
                }
                return;
            }
            if (PlanDocumentToolCatalog.Owns(name))
            {
                if (root["revision"] != null && root["version"] == null)
                    root["version"] = root["revision"].DeepClone();
                RemoveProperties(root, "planId", "artifactId", "revision",
                    "restoredFromArtifactId", "referencingMessageIds");
                return;
            }
            if (!TaskListToolCatalog.Owns(name)) return;
            RemoveProperties(root, "artifactId", "revision");
            var taskList = root["taskList"] as JObject;
            if (taskList == null) return;
            RemoveProperties(taskList, "id");
            foreach (var step in (taskList["steps"] as JArray ??
                new JArray()).OfType<JObject>())
                RemoveProperties(step, "id");
        }

        private static void RemoveHtmlRuntimeState(JToken token)
        {
            RemoveResourceRuntimeState(token);
            var root = token as JObject;
            if (root == null) return;
            RemoveProperties(root, "artifactRef", "updatedUtc", "sourceTool",
                "refreshPolicy", "dryRun", "runtimeExecuted");
            foreach (var item in (root["results"] as JArray ??
                new JArray()).OfType<JObject>())
                RemoveProperties(item, "sourceTool");
            var preflight = root["preflight"] as JObject;
            if (preflight != null)
                RemoveProperties(preflight, "runtimeExecuted");
        }

        private static void RemoveAuthoringRuntimeState(JToken token)
        {
            var value = token as JObject;
            if (value != null)
            {
                RemoveProperties(value, "revision", "previousRevision",
                    "expectedRevision", "beforeSha256", "intendedSha256",
                    "expectedSha256", "actualSha256", "argumentsSha256",
                    "storagePath", "fileName");
                foreach (var property in value.Properties().ToList())
                    RemoveAuthoringRuntimeState(property.Value);
                return;
            }
            var array = token as JArray;
            if (array == null) return;
            foreach (var item in array) RemoveAuthoringRuntimeState(item);
        }

        private static void RemoveVbaRuntimeState(JToken token)
        {
            var value = token as JObject;
            if (value != null)
            {
                foreach (var property in value.Properties().ToList())
                {
                    if (IsVbaRuntimeField(property.Name)) property.Remove();
                    else RemoveVbaRuntimeState(property.Value);
                }
                return;
            }
            var array = token as JArray;
            if (array == null) return;
            foreach (var item in array) RemoveVbaRuntimeState(item);
        }

        private static string SanitizeVbaMessage(string message, JToken data)
        {
            var result = message ?? string.Empty;
            foreach (var value in VbaRuntimeValues(data)
                .Where(item => item.Length >= 4)
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(item => item.Length))
            {
                result = result.Replace(value, "[runtime value]");
            }
            result = Regex.Replace(result,
                "rna://[^\\s\\\"'<>]+", "[runtime resource]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            result = Regex.Replace(result,
                @"(?<![0-9a-f])[0-9a-f]{64}(?![0-9a-f])", "[runtime hash]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            result = Regex.Replace(result,
                @"(?<![0-9a-f])(?:[0-9a-f]{32}|[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})(?![0-9a-f])",
                "[runtime id]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return result;
        }

        private static IEnumerable<string> VbaRuntimeValues(JToken token)
        {
            var value = token as JObject;
            if (value != null)
            {
                foreach (var property in value.Properties())
                {
                    if (IsVbaRuntimeField(property.Name))
                    {
                        foreach (var item in VbaStringValues(property.Value))
                            yield return item;
                    }
                    else
                    {
                        foreach (var item in VbaRuntimeValues(property.Value))
                            yield return item;
                    }
                }
                yield break;
            }
            var array = token as JArray;
            if (array == null) yield break;
            foreach (var child in array)
            {
                foreach (var item in VbaRuntimeValues(child)) yield return item;
            }
        }

        private static IEnumerable<string> VbaStringValues(JToken token)
        {
            var value = token as JValue;
            if (value != null)
            {
                if (value.Type == JTokenType.String &&
                    !string.IsNullOrEmpty((string)value))
                    yield return (string)value;
                yield break;
            }
            if (token == null) yield break;
            foreach (var child in token.Children())
            {
                foreach (var item in VbaStringValues(child)) yield return item;
            }
        }

        private static bool IsVbaRuntimeField(string name)
        {
            var source = name ?? string.Empty;
            var internalId = string.Equals(source, "id",
                    StringComparison.OrdinalIgnoreCase) ||
                source.EndsWith("Id", StringComparison.Ordinal) ||
                source.EndsWith("ID", StringComparison.Ordinal) ||
                source.EndsWith("Ids", StringComparison.Ordinal) ||
                source.EndsWith("IDs", StringComparison.Ordinal) ||
                source.EndsWith("_id", StringComparison.OrdinalIgnoreCase) ||
                source.EndsWith("_ids", StringComparison.OrdinalIgnoreCase);
            var normalized = source
                .Replace("_", string.Empty)
                .ToLowerInvariant();
            return internalId ||
                normalized.Contains("hash") ||
                normalized.Contains("sha256") ||
                normalized.Contains("revision") ||
                normalized.Contains("cursor") ||
                normalized.Contains("offset") ||
                normalized.Contains("guard") ||
                normalized.Contains("fingerprint") ||
                normalized.EndsWith("uri", StringComparison.Ordinal) ||
                normalized == "etag" ||
                normalized == "op" ||
                normalized == "journaled" ||
                normalized == "packagejournaled" ||
                normalized == "journalstatus" ||
                normalized == "terminalrecorded";
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
