using RNAssistant.Core.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class ToolAuthoringService
    {
        private const int PreparedContractVersion = 1;

        internal ToolAuthoringPreparation PrepareMutation(
            string toolId, IDictionary<string, object> arguments)
        {
            if (_toolStore == null)
            {
                return new ToolAuthoringPreparation(
                    ToolAuthoringOutcome.Error(
                        "Tool authoring store is not available.", null,
                        "tool_store_unavailable", false));
            }
            ToolCatalogEntry current;
            ToolCatalogEntry intended;
            string operation;
            ToolAuthoringOutcome error;
            if (string.Equals(toolId, ToolAuthoringCatalog.UpsertToolId,
                StringComparison.Ordinal))
            {
                error = ResolveUpsert(arguments, out current,
                    out intended, out operation);
            }
            else if (string.Equals(toolId, ToolAuthoringCatalog.DeleteToolId,
                StringComparison.Ordinal))
            {
                error = ResolveDelete(arguments, out current);
                intended = null;
                operation = "delete";
            }
            else
            {
                return new ToolAuthoringPreparation(
                    ToolAuthoringOutcome.Error(
                        "Unknown tool authoring mutation: " + toolId,
                        null, "unknown_tool", false));
            }
            if (error != null) return new ToolAuthoringPreparation(error);

            var id = ToolArgumentReader.String(
                arguments, "id", string.Empty);
            var beforeHash = StateHash(current);
            var intendedHash = StateHash(intended);
            var prepared = new JObject
            {
                ["version"] = PreparedContractVersion,
                ["toolId"] = toolId,
                ["id"] = id,
                ["operation"] = operation,
                ["argumentsSha256"] = Hash(ArgumentPayload(arguments)),
                ["beforeSha256"] = beforeHash,
                ["intendedSha256"] = intendedHash
            }.ToString(Formatting.None);
            var preview = new JObject
            {
                ["type"] = "rnassistant.toolAuthoringPreview",
                ["version"] = 1,
                ["id"] = id,
                ["operation"] = operation,
                ["host"] = intended == null
                    ? current == null ? string.Empty : current.Host
                    : intended.Host,
                ["changed"] = !string.Equals(
                    beforeHash, intendedHash, StringComparison.Ordinal),
                ["components"] = new JArray(((intended ?? current)
                    ?.Components ?? new List<ToolPackageComponentDefinition>())
                    .Where(component => component != null)
                    .Select(component => component.Name ?? string.Empty))
            }.ToString(Formatting.None);
            return new ToolAuthoringPreparation(
                ToolAuthoringOutcome.Ok(
                    "Confirmation required to " + operation +
                    " custom tool " + id + ".", preview),
                prepared);
        }

        internal ToolAuthoringOutcome ExecuteMutation(
            string toolId, IDictionary<string, object> arguments,
            string preparedStateJson, Action markDispatchPossible)
        {
            if (_toolStore == null)
            {
                return ToolAuthoringOutcome.Error(
                    "Tool authoring store is not available.", null,
                    "tool_store_unavailable", false);
            }
            JObject prepared;
            try
            {
                prepared = JObject.Parse(preparedStateJson ?? string.Empty);
            }
            catch (JsonException)
            {
                return ToolAuthoringOutcome.Error(
                    "Tool authoring preparation is invalid.", null,
                    "tool_preparation_invalid", false);
            }
            var id = ToolArgumentReader.String(
                arguments, "id", string.Empty);
            if (prepared.Value<int?>("version") != PreparedContractVersion ||
                !string.Equals((string)prepared["toolId"], toolId,
                    StringComparison.Ordinal) ||
                !string.Equals((string)prepared["id"], id,
                    StringComparison.Ordinal) ||
                !string.Equals((string)prepared["argumentsSha256"],
                    Hash(ArgumentPayload(arguments)), StringComparison.Ordinal))
            {
                return ToolAuthoringOutcome.Error(
                    "Tool authoring preparation does not match the accepted call.",
                    null, "tool_preparation_mismatch", false);
            }
            var beforeHash = StateHash(FindStoredTool(id));
            if (!string.Equals((string)prepared["beforeSha256"],
                beforeHash, StringComparison.Ordinal))
            {
                return ToolAuthoringOutcome.Error(
                    "Custom tool changed after confirmation was requested. Read it again before retrying.",
                    null, "tool_definition_changed", true);
            }

            ToolCatalogEntry current;
            ToolCatalogEntry intended;
            string operation;
            ToolAuthoringOutcome error;
            if (string.Equals(toolId, ToolAuthoringCatalog.UpsertToolId,
                StringComparison.Ordinal))
            {
                error = ResolveUpsert(arguments, out current,
                    out intended, out operation);
            }
            else if (string.Equals(toolId, ToolAuthoringCatalog.DeleteToolId,
                StringComparison.Ordinal))
            {
                error = ResolveDelete(arguments, out current);
                intended = null;
                operation = "delete";
            }
            else
            {
                return ToolAuthoringOutcome.Error(
                    "Unknown tool authoring mutation: " + toolId,
                    null, "unknown_tool", false);
            }
            if (error != null) return error;

            if (!string.Equals((string)prepared["operation"], operation,
                    StringComparison.Ordinal) ||
                !string.Equals((string)prepared["intendedSha256"],
                    StateHash(intended), StringComparison.Ordinal))
            {
                return ToolAuthoringOutcome.Error(
                    "Custom tool changed after confirmation was requested. Read it again before retrying.",
                    null, "tool_definition_changed", true);
            }

            var intendedHash = StateHash(intended);
            if (string.Equals(beforeHash, intendedHash,
                StringComparison.Ordinal))
            {
                return ToolAuthoringOutcome.Ok(
                    "Custom tool is already up to date: " + id,
                    intended == null ? null : ToolPayload(intended)
                        .ToString(Formatting.None),
                    ToolAuthoringEffect.VerifiedNoChange);
            }

            if (markDispatchPossible != null) markDispatchPossible();
            if (string.Equals(operation, "delete", StringComparison.Ordinal))
            {
                _toolStore.Delete(id);
            }
            else
            {
                _toolStore.SaveOne(intended);
            }

            var verified = FindStoredTool(id);
            var actualHash = StateHash(verified);
            if (!string.Equals(intendedHash, actualHash,
                StringComparison.Ordinal))
            {
                return ToolAuthoringOutcome.Unknown(
                    "Custom tool did not verify after " + operation +
                    ". Inspect the Tool Library before retrying.",
                    new JObject
                    {
                        ["id"] = id,
                        ["operation"] = operation,
                        ["expectedSha256"] = intendedHash,
                        ["actualSha256"] = actualHash
                    }.ToString(Formatting.None),
                    "tool_authoring_verification_failed");
            }
            return ToolAuthoringOutcome.Ok(
                string.Equals(operation, "delete", StringComparison.Ordinal)
                    ? "Custom tool deleted: " + id
                    : "Custom tool " + (string.Equals(operation, "create",
                        StringComparison.Ordinal) ? "created: " : "updated: ") + id,
                verified == null ? null : ToolPayload(verified)
                    .ToString(Formatting.None),
                ToolAuthoringEffect.VerifiedChange);
        }

        private ToolAuthoringOutcome ResolveUpsert(
            IDictionary<string, object> arguments,
            out ToolCatalogEntry existing,
            out ToolCatalogEntry intended,
            out string operation)
        {
            existing = null;
            intended = null;
            operation = string.Empty;
            var parameterError = ValidateParameterInput(arguments);
            if (parameterError != null) return parameterError;
            var id = ToolArgumentReader.String(arguments, "id", string.Empty);
            var reserved = ValidateAuthoredToolId(id);
            if (reserved != null) return reserved;
            var mode = ToolArgumentReader.String(
                arguments, "mode", "upsert");
            existing = FindStoredTool(id);
            if (existing != null && string.Equals(mode, "createOnly",
                StringComparison.OrdinalIgnoreCase))
            {
                return ToolAuthoringOutcome.Error(
                    "Custom tool already exists: " + id +
                    ". Use mode=upsert or updateOnly.", null,
                    "tool_already_exists", false);
            }
            if (existing == null && string.Equals(mode, "updateOnly",
                StringComparison.OrdinalIgnoreCase))
            {
                return ToolAuthoringOutcome.Error(
                    "Custom tool not found: " + id +
                    ". Use mode=upsert or createOnly.", null,
                    "tool_not_found", false);
            }
            if (existing != null && !HasMutableArguments(arguments))
            {
                return ToolAuthoringOutcome.Error(
                    "Tool update requires at least one supplied field besides id/mode.",
                    null, "tool_update_empty", true);
            }
            intended = existing == null
                ? ReadToolDefinition(arguments)
                : UpdateToolDefinition(existing, arguments);
            var validation = ValidateToolDefinition(intended);
            if (!validation.Success) return validation;
            operation = existing == null ? "create" : "update";
            return null;
        }

        private ToolAuthoringOutcome ResolveDelete(
            IDictionary<string, object> arguments,
            out ToolCatalogEntry existing)
        {
            var id = ToolArgumentReader.String(
                arguments, "id", string.Empty);
            existing = FindStoredTool(id);
            if (existing == null)
            {
                return ToolAuthoringOutcome.Error(
                    "Custom tool not found: " + id, null,
                    "tool_not_found", false);
            }
            return null;
        }

        private ToolCatalogEntry FindStoredTool(string id)
        {
            return (_toolStore == null
                    ? new List<ToolCatalogEntry>() : _toolStore.Load())
                .FirstOrDefault(tool => tool != null && string.Equals(
                    tool.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        private ToolAuthoringOutcome ValidateAuthoredToolId(string id)
        {
            return _isProtectedToolId != null && _isProtectedToolId(id)
                ? ToolAuthoringOutcome.Error(
                    "Tool id is reserved by a built-in tool: " + id,
                    null, "reserved_tool_id", false)
                : null;
        }

        private static JObject ArgumentPayload(
            IDictionary<string, object> arguments)
        {
            var result = new JObject();
            foreach (var pair in (arguments ??
                new Dictionary<string, object>())
                .OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                result[pair.Key] = pair.Value == null
                    ? JValue.CreateNull()
                    : JToken.FromObject(pair.Value);
            }
            return (JObject)Canonicalize(result);
        }

        private static string StateHash(ToolCatalogEntry tool)
        {
            var state = new JObject { ["exists"] = tool != null };
            if (tool != null)
            {
                var payload = ToolPayload(tool);
                var components = payload["components"] as JArray;
                if (components != null)
                {
                    foreach (var component in components.OfType<JObject>())
                        component.Remove("fileName");
                }
                state["definition"] = payload;
            }
            return Hash(Canonicalize(state));
        }

        private static JToken Canonicalize(JToken token)
        {
            var obj = token as JObject;
            if (obj != null)
            {
                var result = new JObject();
                foreach (var property in obj.Properties()
                    .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    result[property.Name] = Canonicalize(property.Value);
                }
                return result;
            }
            var array = token as JArray;
            if (array != null)
                return new JArray(array.Select(Canonicalize));
            return token == null ? JValue.CreateNull() : token.DeepClone();
        }

        private static string Hash(JToken value)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(
                        Encoding.UTF8.GetBytes((value ?? JValue.CreateNull())
                            .ToString(Formatting.None))))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }
    }
}
