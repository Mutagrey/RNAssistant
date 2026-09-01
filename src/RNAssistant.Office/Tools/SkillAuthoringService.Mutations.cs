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
    internal sealed partial class SkillAuthoringService
    {
        private const int PreparedContractVersion = 1;

        internal SkillAuthoringPreparation PrepareMutation(
            string toolId, IDictionary<string, object> arguments)
        {
            if (_skillStore == null)
            {
                return new SkillAuthoringPreparation(
                    SkillAuthoringOutcome.Error(
                        "Skill authoring store is not available.", null,
                        "skill_store_unavailable", false));
            }
            SkillDefinition current;
            SkillDefinition intended;
            string operation;
            string referencePath;
            var error = Resolve(toolId, arguments, out current,
                out intended, out operation, out referencePath);
            if (error != null) return new SkillAuthoringPreparation(error);

            var id = ToolArgumentReader.String(arguments, "id", string.Empty);
            var beforeRevision = StateRevision(current);
            var intendedRevision = StateRevision(intended);
            var prepared = new JObject
            {
                ["version"] = PreparedContractVersion,
                ["toolId"] = toolId,
                ["id"] = id,
                ["operation"] = operation,
                ["referencePath"] = referencePath == null
                    ? JValue.CreateNull() : new JValue(referencePath),
                ["argumentsSha256"] = Hash(ArgumentPayload(arguments)),
                ["beforeRevision"] = beforeRevision,
                ["intendedRevision"] = intendedRevision
            }.ToString(Formatting.None);
            var preview = ResultData(id, operation, referencePath,
                beforeRevision, intendedRevision,
                !string.Equals(beforeRevision, intendedRevision,
                    StringComparison.Ordinal));
            preview["type"] = "rnassistant.skillAuthoringPreview";
            return new SkillAuthoringPreparation(
                SkillAuthoringOutcome.Ok(
                    "Confirmation required to " + OperationLabel(operation) +
                    " skill " + id + ".",
                    preview.ToString(Formatting.None),
                    SkillAuthoringEffect.None),
                prepared);
        }

        internal SkillAuthoringOutcome ExecuteMutation(
            string toolId, IDictionary<string, object> arguments,
            string preparedStateJson, Action markDispatchPossible)
        {
            if (_skillStore == null)
            {
                return SkillAuthoringOutcome.Error(
                    "Skill authoring store is not available.", null,
                    "skill_store_unavailable", false);
            }
            JObject prepared;
            try
            {
                prepared = JObject.Parse(preparedStateJson ?? string.Empty);
            }
            catch (JsonException)
            {
                return SkillAuthoringOutcome.Error(
                    "Skill authoring preparation is invalid.", null,
                    "skill_preparation_invalid", false);
            }

            var id = ToolArgumentReader.String(arguments, "id", string.Empty);
            if (prepared.Value<int?>("version") != PreparedContractVersion ||
                !string.Equals((string)prepared["toolId"], toolId,
                    StringComparison.Ordinal) ||
                !string.Equals((string)prepared["id"], id,
                    StringComparison.Ordinal) ||
                !string.Equals((string)prepared["argumentsSha256"],
                    Hash(ArgumentPayload(arguments)), StringComparison.Ordinal))
            {
                return SkillAuthoringOutcome.Error(
                    "Skill authoring preparation does not match the accepted call.",
                    null, "skill_preparation_mismatch", false);
            }

            var liveBefore = FindStoredSkill(id);
            var beforeRevision = StateRevision(liveBefore);
            if (!string.Equals((string)prepared["beforeRevision"],
                beforeRevision, StringComparison.Ordinal))
            {
                return SkillAuthoringOutcome.Error(
                    "Custom skill changed after confirmation was requested. Read it again before retrying.",
                    ResultData(id, (string)prepared["operation"],
                        (string)prepared["referencePath"],
                        (string)prepared["beforeRevision"],
                        beforeRevision, false).ToString(Formatting.None),
                    "skill_package_changed", true);
            }

            SkillDefinition current;
            SkillDefinition intended;
            string operation;
            string referencePath;
            var error = Resolve(toolId, arguments, out current,
                out intended, out operation, out referencePath);
            if (error != null) return error;
            var intendedRevision = StateRevision(intended);
            if (!string.Equals((string)prepared["operation"], operation,
                    StringComparison.Ordinal) ||
                !string.Equals((string)prepared["referencePath"],
                    referencePath, StringComparison.Ordinal) ||
                !string.Equals((string)prepared["intendedRevision"],
                    intendedRevision, StringComparison.Ordinal))
            {
                return SkillAuthoringOutcome.Error(
                    "Custom skill changed after confirmation was requested. Read it again before retrying.",
                    null, "skill_package_changed", true);
            }

            if (string.Equals(beforeRevision, intendedRevision,
                StringComparison.Ordinal))
            {
                return SkillAuthoringOutcome.Ok(
                    "Custom skill is already up to date: " + id,
                    ResultData(id, operation, referencePath,
                        beforeRevision, intendedRevision, false)
                        .ToString(Formatting.None),
                    SkillAuthoringEffect.VerifiedNoChange);
            }

            if (markDispatchPossible != null) markDispatchPossible();
            string mutationError = null;
            try
            {
                mutationError = ApplyMutation(operation, arguments,
                    current, intended, referencePath);
            }
            catch (Exception ex)
            {
                mutationError = ex.Message;
            }

            var verified = FindStoredSkill(id);
            var actualRevision = StateRevision(verified);
            var data = ResultData(id, operation, referencePath,
                beforeRevision, actualRevision,
                !string.Equals(beforeRevision, actualRevision,
                    StringComparison.Ordinal));
            data["expectedRevision"] = intendedRevision;
            if (string.Equals(actualRevision, intendedRevision,
                StringComparison.Ordinal))
            {
                return SkillAuthoringOutcome.Ok(
                    SuccessMessage(id, operation),
                    data.ToString(Formatting.None),
                    SkillAuthoringEffect.VerifiedChange);
            }
            if (string.Equals(actualRevision, beforeRevision,
                StringComparison.Ordinal))
            {
                return SkillAuthoringOutcome.Error(
                    string.IsNullOrWhiteSpace(mutationError)
                        ? "Custom skill mutation was not applied: " + id
                        : mutationError,
                    data.ToString(Formatting.None),
                    "skill_authoring_not_applied", false,
                    SkillAuthoringEffect.VerifiedNoChange);
            }
            return SkillAuthoringOutcome.Unknown(
                "Custom skill did not verify after " +
                OperationLabel(operation) +
                ". Inspect the Skill Library before retrying." +
                (string.IsNullOrWhiteSpace(mutationError)
                    ? string.Empty : " " + mutationError),
                data.ToString(Formatting.None),
                "skill_authoring_verification_failed");
        }

        private SkillAuthoringOutcome Resolve(
            string toolId, IDictionary<string, object> arguments,
            out SkillDefinition current,
            out SkillDefinition intended,
            out string operation,
            out string referencePath)
        {
            if (string.Equals(toolId, SkillAuthoringCatalog.UpsertToolId,
                StringComparison.Ordinal))
            {
                return ResolveUpsert(arguments, out current, out intended,
                    out operation, out referencePath);
            }
            if (string.Equals(toolId, SkillAuthoringCatalog.DeleteToolId,
                StringComparison.Ordinal))
            {
                return ResolveDelete(arguments, out current, out intended,
                    out operation, out referencePath);
            }
            current = null;
            intended = null;
            operation = string.Empty;
            referencePath = null;
            return SkillAuthoringOutcome.Error(
                "Unknown skill authoring mutation: " + toolId,
                null, "unknown_tool", false);
        }

        private string ApplyMutation(
            string operation,
            IDictionary<string, object> arguments,
            SkillDefinition current,
            SkillDefinition intended,
            string referencePath)
        {
            if (string.Equals(operation, "delete", StringComparison.Ordinal))
            {
                return _skillStore.Delete(current.Id)
                    ? null : "Custom skill was not found during deletion.";
            }
            if (string.Equals(operation, "create_reference",
                    StringComparison.Ordinal) ||
                string.Equals(operation, "update_reference",
                    StringComparison.Ordinal))
            {
                SkillReferenceMetadata saved;
                string error;
                return _skillStore.TrySaveReference(
                    current, referencePath,
                    ToolArgumentReader.String(arguments,
                        "referenceMarkdown", string.Empty),
                    out saved, out error)
                        ? null : error;
            }
            if (string.Equals(operation, "delete_reference",
                StringComparison.Ordinal))
            {
                string error;
                return _skillStore.TryDeleteReference(
                    current, referencePath, out error)
                        ? null : error;
            }
            _skillStore.SaveOne(intended);
            return null;
        }

        private static string StateRevision(SkillDefinition skill)
        {
            var source = RNAssistant.Core.Tools.SkillPackageSource.Capture(skill);
            return source == null ? string.Empty : source.Revision;
        }

        private static JObject ResultData(
            string id, string operation, string referencePath,
            string previousRevision, string revision, bool changed)
        {
            return new JObject
            {
                ["type"] = "rnassistant.skillAuthoringResult",
                ["contractVersion"] =
                    SkillAuthoringOutcome.CurrentContractVersion,
                ["id"] = id ?? string.Empty,
                ["operation"] = operation ?? string.Empty,
                ["referencePath"] = referencePath == null
                    ? JValue.CreateNull() : new JValue(referencePath),
                ["previousRevision"] = previousRevision ?? string.Empty,
                ["revision"] = revision ?? string.Empty,
                ["changed"] = changed
            };
        }

        private static string SuccessMessage(string id, string operation)
        {
            if (string.Equals(operation, "delete", StringComparison.Ordinal))
                return "Skill deleted: " + id;
            if (string.Equals(operation, "create", StringComparison.Ordinal))
                return "Skill created: " + id;
            if (string.Equals(operation, "update", StringComparison.Ordinal))
                return "Skill updated: " + id;
            return "Skill " + OperationLabel(operation) + ": " + id;
        }

        private static string OperationLabel(string operation)
        {
            return (operation ?? string.Empty).Replace('_', ' ');
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
                    ? JValue.CreateNull() : JToken.FromObject(pair.Value);
            }
            return (JObject)Canonicalize(result);
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
