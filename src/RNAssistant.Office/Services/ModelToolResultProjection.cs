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
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    // Durable results retain exact runtime evidence. This projection is the only
    // representation of results from each R61-switched family sent to a model.
    internal static class ModelToolResultProjection
    {
        internal static readonly RNAssistant.Core.Services.ModelResultProjection Instance =
            new RNAssistant.Core.Services.ModelResultProjection(ForModel);

        internal static ChatMessage Project(ChatMessage source, IEnumerable<ToolCatalogEntry> tools = null,
            IEnumerable<SkillDefinition> skills = null, Action<List<ContextMessagePresentation>> recordPresentation = null,
            IEnumerable<ToolCatalogEntry> callableTools = null)
        { return Instance.Project(source, tools, skills, recordPresentation, callableTools); }

        private static ToolResultMaterialization GenericForModel(string name, ToolResultMaterialization source)
        {
            var data = RNAssistant.Core.Services.ModelResultProjection.ProjectGenericData(name, source);
            RemoveOfficeRuntimeState(name, data);
            var message = RNAssistant.Core.Services.ModelResultProjection.RemoveRuntimeResourceValues(
                source.Result.Message, source.Result.Resources);
            var result = new ToolResult(source.Result.Status,
                IsOfficeRuntimeStateResult(name) ? SanitizeOperationalText(message) : message,
                data.ToString(Formatting.None), new ResourceRef[0]);
            return new ToolResultMaterialization(result, source.ModelAttachments, data: data);
        }

        private static bool IsOfficeRuntimeStateResult(string name)
        {
            return string.Equals(name, WordToolIds.ReplaceText, StringComparison.Ordinal) ||
                string.Equals(name, PowerPointToolIds.ReplaceText, StringComparison.Ordinal) ||
                string.Equals(name, "outlook.draft.create", StringComparison.Ordinal) ||
                string.Equals(name, "outlook.mail.update", StringComparison.Ordinal);
        }

        private static void RemoveOfficeRuntimeState(string name, JToken data)
        {
            var root = data as JObject;
            if (root == null) return;
            if (string.Equals(name, WordToolIds.ReplaceText, StringComparison.Ordinal) ||
                string.Equals(name, PowerPointToolIds.ReplaceText, StringComparison.Ordinal))
                RemoveProperties(root, "scopeSha256");
            else if (string.Equals(name, "outlook.draft.create", StringComparison.Ordinal))
                RemoveProperties(root, "targetEntryId", "draftEntryId");
            else if (string.Equals(name, "outlook.mail.update", StringComparison.Ordinal))
                RemoveProperties(root, "entryId", "stateSha256");
        }

        internal static string SanitizeRuntimeText(string value)
        { return RNAssistant.Core.Services.ModelResultProjection.SanitizeRuntimeText(value); }

        internal static string SanitizeOperationalText(string value)
        { return RNAssistant.Core.Services.ModelResultProjection.SanitizeOperationalText(value); }

        internal static string SanitizeClaimText(StructuredContextClaim claim)
        { return RNAssistant.Core.Services.ModelResultProjection.SanitizeClaimText(claim); }

        private static ToolResultMaterialization ForModel(
            string name,
            ToolResultMaterialization source,
            IEnumerable<ToolCatalogEntry> tools = null,
            IEnumerable<SkillDefinition> skills = null,
            IEnumerable<ToolCatalogEntry> callableTools = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (!IsCapabilityResult(name) && !IsPlanningResult(name) && !IsHtmlResult(name) &&
                !IsAuthoringResult(name) && !IsVbaResult(name) && !IsExcelResult(name))
                return GenericForModel(name, source);
            var data = IsExcelResult(name) || IsVbaResult(name) || IsAuthoringResult(name)
                ? source.Data : source.Data.DeepClone();
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
                data = ToolAuthoringCatalog.Owns(name) ? ToolAuthoringToolProjection.ForModel(data) :
                    SkillAuthoringCatalog.Owns(name) ? SkillAuthoringToolHandler.ForModel(data) :
                    PromptToolProjection.ForModel(data);
            }
            else if (IsExcelResult(name))
            {
                data = ProjectExcelData(data);
            }
            else if (IsVbaResult(name))
            {
                var runtimeValues = VbaRuntimeValues(data)
                    .Where(item => item.Length >= 4)
                    .Distinct(StringComparer.Ordinal)
                    .OrderByDescending(item => item.Length)
                    .ToArray();
                message = SanitizeVbaMessage(message, runtimeValues);
                data = ProjectVbaData(data, runtimeValues);
            }
            else
            {
                if (name == CapabilityToolCatalog.ReadToolId && status == ToolResultStatus.Ok &&
                    CapabilityCatalogService.CompactCallableSchema(data, callableTools))
                    message = "Tool schema is in the current request: " + (string)data["id"] + ".";
                CapabilityCatalogService.RemoveRuntimeState(data);
            }

            // Result prose is operational metadata, not resource content. Apply the
            // same exact-reference boundary as data for every switched family.
            message = SanitizeOperationalText(
                RNAssistant.Core.Services.ModelResultProjection.RemoveRuntimeResourceValues(message, source.Result.Resources));

            var result = new RNAssistant.Core.Tools.Contracts.ToolResult(
                status,
                message,
                data == null ? "null" : data.ToString(Formatting.None),
                new ResourceRef[0]);
            return new ToolResultMaterialization(
                result, source.ModelAttachments, data: data);
        }

        internal static bool ValidateAcceptedCall(ToolCall call, out string error)
        {
            error = null;
            if (call == null || string.IsNullOrWhiteSpace(call.Name)) return true;
            if (CapabilityNaming.IsRetiredTool(call.Name) ||
                VbaToolCatalog.Owns(call.Name) && call.Name != VbaToolCatalog.RunMacro ||
                AuthoredFileToolCatalog.Owns(call.Name) || MarkdownDocumentToolCatalog.Owns(call.Name) ||
                call.Name == HtmlWorkspaceToolCatalog.WriteFileToolId || call.Name == HtmlWorkspaceToolCatalog.ApplyPatchToolId ||
                call.Name == HtmlWorkspaceToolCatalog.DeleteToolId)
            {
                error = "This conversation uses retired tool contracts. Start a new chat; stored history is preserved.";
                return false;
            }
            if (call.Name.StartsWith("rna_", StringComparison.OrdinalIgnoreCase))
            {
                error = "Synthetic rna_* tool names are not part of the public catalog.";
                return false;
            }
            string schema;
            switch (call.Name)
            {
                case ResourceMutationToolCatalog.Write:
                case ResourceMutationToolCatalog.Patch:
                case ResourceMutationToolCatalog.Replace:
                case ResourceMutationToolCatalog.Copy:
                case ResourceMutationToolCatalog.Move:
                case ResourceMutationToolCatalog.Delete:
                case ResourceMutationToolCatalog.Restore:
                    schema = ResourceMutationToolCatalog.GetTools().Single(tool => tool.Id == call.Name).ArgumentSchemaJson;
                    break;
                case "common.resources_list":
                case "common.resources_resolve":
                    error = "Public resource list/resolve calls were retired by 11O1.";
                    return false;
                case "plan.document.create":
                case "plan.document.update":
                    error = "Public Plan create/update calls were replaced by plan.document.save in 11O2.";
                    return false;
                case "task.list.create":
                case "task.list.update":
                case "task.list.close":
                    error = "Public Task List lifecycle calls were replaced by task.list.set in 11O2.";
                    return false;
                case "html.workspace.inspect":
                    error = "Public HTML inspection was internalized in 11O3.";
                    return false;
                case "html.workspace.set.active":
                    error = "Public HTML preview selection was internalized in 11O3.";
                    return false;
                case "html.workspace.upsert":
                case "html.workspace.upsert.file":
                    error = "Public HTML upsert was split into semantic file/data writes in 11O3.";
                    return false;
                case "common.tools_validate":
                    error = "Tool validation is internal to upsert and Library in 11O4.";
                    return false;
                case "common.tools_definition_read":
                    error = "Tool source reads use core.find/read and exact catalog evidence.";
                    return false;
                case "common.prompts_read":
                    error = "Prompt reads use core.find/read and exact catalog evidence.";
                    return false;
                case "excel.read_range":
                    error = "Excel range reads use core.read and exact document evidence.";
                    return false;
                case "outlook.read_mail":
                case "outlook.collect_mail":
                    error = "Mail reads use core.find/read and exact document evidence.";
                    return false;
                case "powerpoint.read_slides":
                    error = "PowerPoint slide reads use core.read and exact document evidence.";
                    return false;
                case "word.read_text":
                    error = "Word text reads use core.read and exact document evidence.";
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
                case ResourceToolCatalog.SearchToolId:
                    schema = ResourceSearchToolHandler.Descriptor.ParametersJson;
                    break;
                case ResourceToolCatalog.OutlineToolId:
                    schema = ResourceOutlineToolHandler.Descriptor.ParametersJson;
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
                case HtmlWorkspaceToolCatalog.WriteDataToolId:
                    schema = HtmlWorkspaceToolService.WriteDataSchema();
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
                    if (call.Name == ResourceMutationToolCatalog.Patch)
                    {
                        foreach (var section in ResourcePatch.Parse((string)arguments["patch"]))
                        {
                            if (ResourceGatewayService.IsUriLikeIntentTarget(section.Target))
                                throw new TextPatternException("invalid_patch", "Use semantic targets in patch sections.");
                            TextPatch.Parse(section.Body);
                        }
                    }
                    var semanticTargetField = SemanticTargetField(call.Name);
                    if (semanticTargetField != null &&
                        ResourceGatewayService.IsUriLikeIntentTarget(
                            (string)arguments[semanticTargetField]))
                    {
                        error = "Stored " + call.Name + "." + semanticTargetField +
                            " contains a URI instead of a current semantic target.";
                        return false;
                    }
                    if (ResourceMutationToolCatalog.Owns(call.Name) && ResourceGatewayService.IsUriLikeIntentTarget((string)arguments["destination"]))
                    {
                        error = "Stored destination contains a URI instead of a semantic target.";
                        return false;
                    }
                    return true;
                }
                error = "Stored " + call.Name + " arguments do not match the current semantic schema.";
                return false;
            }
            catch (Exception ex) when (ex is JsonException || ex is TextPatternException)
            {
                error = "Stored " + call.Name + " arguments are not a JSON object for the current semantic schema.";
                return false;
            }
        }

        private static string SemanticTargetField(string toolName)
        {
            if (ResourceMutationToolCatalog.Owns(toolName)) return "target";
            if (string.Equals(toolName, ResourceToolCatalog.ReadToolId,
                    StringComparison.Ordinal) ||
                string.Equals(toolName, HtmlWorkspaceToolCatalog.BindDataToolId,
                    StringComparison.Ordinal) ||
                string.Equals(toolName, VbaToolCatalog.RestoreBackup,
                    StringComparison.Ordinal))
                return "target";
            return null;
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

        private static void RemovePlanningRuntimeState(string name, JToken token)
        {
            var root = token as JObject;
            if (root == null) return;
            if (string.Equals(name, UserQuestionToolCatalog.AskToolId,
                StringComparison.Ordinal))
            {
                ModelResultProjection.RemoveQuestionRuntimeState(name, token);
                return;
            }
            if (PlanDocumentToolCatalog.Owns(name))
            {
                ModelResultProjection.RemovePlanRuntimeState(name, token);
                return;
            }
            ModelResultProjection.RemoveTaskListRuntimeState(name, token);
        }

        private static void RemoveHtmlRuntimeState(JToken token)
        {
            ModelResultProjection.RemoveResourceRuntimeState(token);
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

        private static bool IsExcelResult(string name)
        {
            return ExcelReadToolIds.Owns(name) || ExcelWriteToolIds.Owns(name) || ExcelFindReplaceToolIds.Owns(name) ||
                ExcelChartToolIds.Owns(name) || ExcelRangeMutationToolIds.Owns(name) || ExcelTableToolIds.Owns(name) || ExcelSheetToolIds.Owns(name);
        }

        private static JToken ProjectExcelData(JToken data)
        {
            var result = ModelResultProjection.SelectFields(data, "sheet", "address", "target", "kind", "operation",
                "rows", "columns", "cellCount", "rowsScanned", "cellsScanned", "consistency", "name", "displayName", "range", "hasHeaders", "style", "verification",
                "workbook", "items", "item", "content", "header", "headers", "sample", "numericColumns", "blankCells", "formulaCells",
                "query", "mode", "scope", "matches", "matchCount", "replacements", "returnedCount", "truncated", "complete",
                "activeSheet", "chartCount", "chartIndex", "chartName", "chartType", "title", "xAxisTitle", "yAxisTitle",
                "left", "top", "width", "height", "series", "code", "retryable", "details", "field");
            // Details are operational metadata. Values/previews/samples remain exact.
            if (result["details"] is JObject)
                result["details"] = ModelResultProjection.SelectFields(result["details"],
                    "code", "message", "sheet", "address", "chartIndex", "field", "kind");
            foreach (var key in new[] { "details", "verification" })
                if (result[key]?.Type == JTokenType.String) result[key] = SanitizeOperationalText((string)result[key]);
            if (result["details"] is JObject && result["details"]["message"]?.Type == JTokenType.String)
                result["details"]["message"] = SanitizeOperationalText((string)result["details"]["message"]);
            return result;
        }

        private static readonly HashSet<string> VbaModelFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "code", "retryable", "target", "moduleName", "componentType",
            "changed", "created", "mode", "macroName", "argumentCount", "previousModuleName", "newModuleName",
            "nameNormalized", "requestedModuleName", "normalizedModuleName", "requestedNewModuleName", "normalizedNewModuleName",
            "currentType", "targetComponentType", "backupType", "actualExists", "actualSourceExists", "actualTargetExists",
            "actualSourceComponentType", "expectedSourceComponentType", "expectedSourceExists", "expectedTargetExists",
            "vbeNormalized", "lineCount", "expectedLineCount", "actualLineCount", "expectedComponentType", "actualComponentType",
            "hunkIndex", "findMatchCount", "matchCount", "matchMode", "startLine", "startColumn", "locations", "locationsComplete",
            "oldLength", "newLength", "operations", "operationData", "stage", "rollbackDisposition", "failure", "rollbackFailure",
            "message", "rootMessage", "errorCode", "rootCode", "rootType", "details", "restore",
            "inspectTool", "discoveryScope", "requiredRepresentation", "completeSourceRequired", "retryAfterRefresh",
            "retrySameTool", "reconcileBeforeOverwrite", "reloadEditor", "creationTool", "creationMode", "discoveryTool", "patchTool", "suggestedMode"
        };

        private static JToken ProjectVbaData(JToken data, IEnumerable<string> runtimeValues)
        {
            var array = data as JArray;
            if (array != null) return new JArray(array.Select(item => ProjectVbaData(item, runtimeValues)));
            if (!(data is JObject)) return data?.DeepClone();
            return new JObject(((JObject)data).Properties()
                .Where(property => VbaModelFields.Contains(property.Name) && property.Value.Type != JTokenType.Null)
                .Select(property => new JProperty(property.Name,
                    property.Value.Type == JTokenType.String &&
                        (property.Name == "message" || property.Name == "rootMessage" || property.Name == "details")
                    ? new JValue(SanitizeVbaMessage((string)property.Value, runtimeValues))
                    : ProjectVbaData(property.Value, runtimeValues))));
        }

        private static string SanitizeVbaMessage(
            string message, IEnumerable<string> runtimeValues)
        {
            var result = message ?? string.Empty;
            foreach (var value in runtimeValues ?? Enumerable.Empty<string>())
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
            var root = token as JContainer;
            return root == null ? Enumerable.Empty<string>() : root.Descendants().OfType<JProperty>()
                .Where(property => IsVbaRuntimeField(property.Name) && property.Value.Type == JTokenType.String)
                .Select(property => (string)property.Value);
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
            if (string.Equals(kind, "tool-schema", StringComparison.Ordinal) ||
                string.Equals(kind, "skill-tool", StringComparison.Ordinal))
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
                ["complete"] = false
            };
        }
    }
}
