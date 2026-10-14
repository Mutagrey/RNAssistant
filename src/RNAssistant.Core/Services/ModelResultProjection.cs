using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;

namespace RNAssistant.Core.Services
{
    // Pure result projection shared by the frozen compiler. A domain may supply
    // additional sanitization; no callback may read live state or execute a tool.
    public sealed class ModelResultProjection
    {
        private const string Prefix = "TOOL_RESULT:\n";
        private readonly Func<string, ToolResultMaterialization, IEnumerable<ToolCatalogEntry>,
            IEnumerable<SkillDefinition>, ToolResultMaterialization> _projectData;

        public ModelResultProjection(Func<string, ToolResultMaterialization, IEnumerable<ToolCatalogEntry>,
            IEnumerable<SkillDefinition>, ToolResultMaterialization> projectData = null)
        { _projectData = projectData; }

        public ChatMessage Project(
            ChatMessage source,
            IEnumerable<ToolCatalogEntry> tools = null,
            IEnumerable<SkillDefinition> skills = null,
            Action<List<ContextMessagePresentation>> recordPresentation = null)
        {
            var projected = HistoricalContextProjector.Project(source);
            if (projected == null) return null;
            projected.ResourceRefs = new List<ResourceRef>();
            projected.ResultPayload = null;
            projected.HtmlWorkspaceCheckpoint = null;
            if (source.ToolResultProtocolVersion != ToolResultWire.CurrentVersion)
            {
                projected.Content = source.Content;
                return projected;
            }
            if (string.Equals(source.Role, "assistant",
                StringComparison.OrdinalIgnoreCase))
            {
                projected.Content = SanitizeAcceptedAssistantContent(source);
                return projected;
            }

            ToolResultWireReadResult wire;
            string error;
            if (!ToolResultHistoryReader.TryRead(source, out wire, out error))
            {
                return InvalidCurrentResult(projected, source);
            }

            var data = wire.Data;
            var materialized = new ToolResultMaterialization(
                wire.Result, resultResource: wire.ResultResource, data: data);
            // Common families have one projection in both hosts. Domain projection
            // runs only for other tools; recovery is rendered once from typed facts.
            var model = IsResourceEvidence(wire.Name) || IsWorkspaceMutation(wire.Name) || _projectData == null
                ? ProjectData(wire.Name, materialized)
                : _projectData(wire.Name, materialized, tools, skills);
            var recovery = source.ExecutionProgress?.Recovery;
            if (recovery != null)
            {
                var visible = model.Data as JObject ?? new JObject { ["content"] = model.Data };
                visible["recovery"] = recovery.ModelProjection();
                model.ReplaceResult(model.Result, visible);
            }
            recordPresentation?.Invoke(DescribeProjectedData(data, model.Data));
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

        private static List<ContextMessagePresentation> DescribeProjectedData(JToken original, JToken visible)
        {
            var parts = new List<ContextMessagePresentation>();
            if (JToken.DeepEquals(original, visible))
                parts.Add(new ContextMessagePresentation { Kind = "data", Presentation = ContextPresentationKind.Full,
                    Reason = "Все данные результата сохранены после model projection." });
            else
            {
                var originalObject = original as JObject;
                var visibleObject = visible as JObject;
                if (originalObject != null && visibleObject != null)
                    foreach (var property in originalObject.Properties())
                        if (visibleObject.Property(property.Name) != null && JToken.DeepEquals(property.Value, visibleObject[property.Name]))
                            parts.Add(new ContextMessagePresentation { Kind = "data." + property.Name,
                                Presentation = ContextPresentationKind.Full, Reason = "Поле сохранено без изменений." });
            }
            return parts;
        }

        public static void RemoveQuestionRuntimeState(string name, JToken data)
        {
            var root = data as JObject;
            if (name != UserQuestionToolCatalog.AskToolId || root == null) return;
            root.Remove("questionSetId");
            foreach (var question in (root["questions"] as JArray ?? new JArray()).OfType<JObject>())
            {
                question.Remove("id");
                foreach (var option in (question["options"] as JArray ?? new JArray()).OfType<JObject>())
                    option.Remove("id");
            }
        }

        public static void RemoveTaskListRuntimeState(string name, JToken data)
        {
            var root = data as JObject;
            if (!TaskListToolCatalog.Owns(name) || root == null) return;
            root.Remove("artifactId"); root.Remove("revision");
            var task = root["taskList"] as JObject;
            if (task == null) return;
            task.Remove("id");
            foreach (var step in (task["steps"] as JArray ?? new JArray()).OfType<JObject>()) step.Remove("id");
        }

        public static void RemovePlanRuntimeState(string name, JToken data)
        {
            var root = data as JObject;
            if (!PlanDocumentToolCatalog.Owns(name) || root == null) return;
            if (root["revision"] != null && root["version"] == null) root["version"] = root["revision"].DeepClone();
            foreach (var key in new[] { "planId", "artifactId", "revision", "restoredFromArtifactId", "referencingMessageIds" }) root.Remove(key);
        }

        public static bool IsResourceEvidence(string name)
        {
            return string.Equals(name, "common.resources_find", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "common.resources_read", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "common.resources_search", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "common.resources_outline", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsExactReadEvidence(string name)
        {
            return IsResourceEvidence(name) ||
                string.Equals(name, "common.capabilities_search", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "common.capabilities_read", StringComparison.OrdinalIgnoreCase);
        }

        private static ToolResultMaterialization ProjectData(string name, ToolResultMaterialization source)
        {
            if (!IsResourceEvidence(name) && !IsWorkspaceMutation(name)) return GenericForModel(name, source);
            var data = IsWorkspaceMutation(name)
                ? SelectFields(source.Data, "path", "targetPath", "changed", "restored", "moved", "deleted", "code")
                : ProjectResourceData(name, source.Data);
            if (IsResourceEvidence(name)) SanitizeResourceEnvelope(data, source.Result.Resources);
            var result = new ToolResult(source.Result.Status,
                SanitizeOperationalText(RemoveRuntimeResourceValues(source.Result.Message, source.Result.Resources)),
                data.ToString(Formatting.None), new ResourceRef[0]);
            return new ToolResultMaterialization(result, source.ModelAttachments, data: data);
        }

        private static bool IsWorkspaceMutation(string name)
        {
            return new[] { "files.create", "files.patch", "files.replace", "files.copy", "files.move", "files.delete", "files.restore" }
                .Contains(name, StringComparer.Ordinal);
        }

        // Only the service-owned envelope is selected. Text, tables and user JSON
        // are copied intact; field names inside business data carry no authority.
        public static JObject SelectFields(JToken data, params string[] fields)
        {
            var source = data as JObject;
            var result = new JObject();
            if (source != null)
                foreach (var property in source.Properties())
                    if (fields.Contains(property.Name, StringComparer.Ordinal) && property.Value.Type != JTokenType.Null)
                        result[property.Name] = property.Value.DeepClone();
            return result;
        }

        public static JObject ProjectResourceData(string name, JToken data)
        {
            var result = name == "common.resources_find"
                ? SelectFields(data, "scope", "query", "items", "total", "complete", "availabilityHint", "unavailableScopes", "directory", "code", "retryable")
                : name == "common.resources_search"
                ? SelectFields(data, "target", "query", "matches", "totalMatches", "scanComplete", "sourceComplete", "code", "retryable")
                : name == "common.resources_outline"
                ? SelectFields(data, "target", "partCount", "totalLines", "nextPart", "sourceComplete", "symbolLanguage", "symbolsComplete", "parts", "code", "retryable")
                : SelectFields(data, "version", "historical", "startLine", "requestedLines", "section", "partNumber", "partCount", "kind", "target", "type", "scope",
                    "representation", "text", "table", "returnedCharacters", "totalCharacters", "complete", "sourceComplete",
                    "rawContentIncluded", "references", "claims", "claimsUnavailable", "usage", "code", "retryable");
            if (name == "common.resources_find" && result["items"] is JArray)
                result["items"] = new JArray(((JArray)result["items"]).Select(item => SelectFields(item,
                    "target", "type", "title", "representations", "sectionTitle", "description", "usage", "snippet")));
            if (name == "common.resources_search" && result["matches"] is JArray)
                result["matches"] = new JArray(((JArray)result["matches"]).Select(item => SelectFields(item,
                    "snippet", "partNumber", "sectionTitle")));
            if (name == "common.resources_outline" && result["parts"] is JArray)
                result["parts"] = new JArray(((JArray)result["parts"]).Select(item =>
                {
                    var projected = SelectFields(item, "partNumber", "firstLine", "lastLine", "characters", "symbols");
                    if (projected["symbols"] is JArray)
                        projected["symbols"] = new JArray(((JArray)projected["symbols"])
                            .Select(symbol => SelectFields(symbol, "name", "kind", "line")));
                    return projected;
                }));
            if (result["unavailableScopes"] is JArray && !result["unavailableScopes"].HasValues) result.Remove("unavailableScopes");
            if ((string)result["type"] == "shared context" && result["text"] != null)
            {
                result.Remove("text");
                result["claimsUnavailable"] = true;
                result["usage"] = "Shared claims require the authority-filtered model context compiler.";
            }
            return result;
        }

        public static ToolResultMaterialization GenericForModel(
            string name,
            ToolResultMaterialization source)
        {
            var data = ProjectGenericData(name, source);
            var result = new RNAssistant.Core.Tools.Contracts.ToolResult(
                source.Result.Status,
                RemoveRuntimeResourceValues(source.Result.Message, source.Result.Resources),
                data.ToString(Formatting.None),
                new ResourceRef[0]);
            return new ToolResultMaterialization(
                result, source.ModelAttachments, data: data);
        }

        // Shared data projection; domain owners finish their metadata before serialization.
        public static JToken ProjectGenericData(string name, ToolResultMaterialization source)
        {
            var data = source.Data.DeepClone();
            RemoveQuestionRuntimeState(name, data);
            RemoveTaskListRuntimeState(name, data);
            RemovePlanRuntimeState(name, data);
            if (IsResourceEvidence(name)) SanitizeResourceEnvelope(data, source.Result.Resources);
            else RemoveRuntimeResourceValues(data, source.Result.Resources);
            var objectData = data as JObject;
            if (objectData != null && source.ResultResource != null &&
                (JToken.DeepEquals(objectData["payload_externalized"], new JValue(true)) ||
                 JToken.DeepEquals(objectData["externalized"], new JValue(true)) ||
                 JToken.DeepEquals(objectData["truncated"], new JValue(true)) && objectData["preview"] != null && objectData["original_chars"] != null))
            {
                objectData["hint"] =
                    "The complete result is stored durably. Find its semantic target with common.resources_find before reading it; do not use runtime resource references.";
            }
            return data;
        }

        private static void RemoveRuntimeResourceValues(
            JToken token,
            IEnumerable<ResourceRef> references)
        {
            if (token == null) return;
            var value = token as JValue;
            if (value != null)
            {
                if (value.Type == JTokenType.String)
                    value.Value = RemoveRuntimeResourceValues(
                        (string)value.Value, references);
                return;
            }
            foreach (var child in token.Children())
                RemoveRuntimeResourceValues(child, references);
        }

        // Exact resource text and snippets are user data. Redacting URI-shaped
        // literals there would make the model observe different source bytes.
        public static void SanitizeResourceEnvelope(JToken data, IEnumerable<ResourceRef> references)
        {
            var root = data as JObject;
            if (root == null) return;
            foreach (var property in root.Properties())
            {
                if (property.Name == "text" || property.Name == "table" || property.Name == "items" ||
                    property.Name == "matches" || property.Name == "parts") continue;
                RemoveRuntimeResourceValues(property.Value, references);
            }
        }

        public static string RemoveRuntimeResourceValues(
            string value,
            IEnumerable<ResourceRef> references)
        {
            var result = value ?? string.Empty;
            foreach (var uri in (references ?? new ResourceRef[0])
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Uri))
                .Select(item => item.Uri)
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(item => item.Length))
            {
                result = result.Replace(uri, "[runtime resource]");
            }
            return Regex.Replace(result,
                "rna://[^\\s\\\"'<>]+", "[runtime resource]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        public static string SanitizeRuntimeText(string value)
        {
            return RemoveRuntimeResourceValues(value, null);
        }

        public static string SanitizeOperationalText(string value)
        {
            var result = SanitizeRuntimeText(value);
            result = Regex.Replace(result,
                @"\s*Source SHA-256:\s*[0-9a-f]{64}\.?", string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return Regex.Replace(result,
                @"(?<![0-9a-f])[0-9a-f]{64}(?![0-9a-f])", "[runtime hash]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        public static string SanitizeClaimText(StructuredContextClaim claim)
        {
            var text = claim == null ? string.Empty : claim.Text;
            return (claim?.SourceRoles ?? new List<string>()).Any(role =>
                string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
                ? SanitizeOperationalText(text)
                : SanitizeRuntimeText(text);
        }

        private static string SanitizeAcceptedAssistantContent(ChatMessage source)
        {
            if (string.Equals(source.ToolResultRole, ToolResultRoles.Tool, StringComparison.Ordinal))
                return SanitizeOperationalText(source.Content);

            // The non-native accepted call is a canonical v7 envelope. Preserve the
            // exact tool_calls suffix: it may contain literal source text or hashes.
            const string prefix = "{\"message\":";
            var content = source.Content ?? string.Empty;
            if (!content.StartsWith(prefix, StringComparison.Ordinal) ||
                content.Length <= prefix.Length || content[prefix.Length] != '"')
                return content;
            var escaped = false;
            for (var index = prefix.Length + 1; index < content.Length; index++)
            {
                if (escaped) { escaped = false; continue; }
                if (content[index] == '\\') { escaped = true; continue; }
                if (content[index] != '"') continue;
                var encoded = content.Substring(prefix.Length, index - prefix.Length + 1);
                string message;
                try { message = JsonConvert.DeserializeObject<string>(encoded); }
                catch (JsonException) { return content; }
                var sanitized = SanitizeOperationalText(message);
                return string.Equals(message, sanitized, StringComparison.Ordinal)
                    ? content
                    : prefix + JsonConvert.SerializeObject(sanitized) + content.Substring(index + 1);
            }
            return content;
        }

        private static ChatMessage InvalidCurrentResult(
            ChatMessage projected,
            ChatMessage source)
        {
            var name = source == null ? null : source.ToolName;
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

        public static void RemoveResourceRuntimeState(JToken token, bool preserveTable = false)
        {
            var value = token as JObject;
            if (value != null)
            {
                foreach (var property in value.Properties().ToList())
                {
                    if (IsResourceRuntimeField(property.Name)) property.Remove();
                    // Only the resource-read root owns this ResourceTableBatch.
                    // Its columns/rows are user data, including nested values whose
                    // keys can coincide with runtime metadata names.
                    else if (!preserveTable || property.Name != "table")
                        RemoveResourceRuntimeState(property.Value);
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

    }
}
