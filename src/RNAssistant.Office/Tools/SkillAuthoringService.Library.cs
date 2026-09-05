using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class SkillAuthoringService
    {
        internal SkillManualMutationResult ExecuteManualCoreMutation(
            SkillLibraryCoreMutation mutation)
        {
            if (mutation == null)
                return ManualResult(SkillAuthoringOutcome.Error(
                    "Skill mutation is required.", null,
                    "invalid_skill_mutation", false), false, null);
            var kind = mutation.Kind ?? string.Empty;
            if (!string.Equals(kind, "upsert", StringComparison.Ordinal) &&
                !string.Equals(kind, "delete", StringComparison.Ordinal))
            {
                return ManualResult(SkillAuthoringOutcome.Error(
                    "Unknown Skill Library mutation: " + kind, null,
                    "invalid_skill_mutation", false), false, null);
            }

            var intended = mutation.Intended;
            var baseId = string.IsNullOrWhiteSpace(mutation.BaseId)
                ? null : mutation.BaseId;
            var selectedId = string.Equals(kind, "delete",
                    StringComparison.Ordinal)
                ? baseId
                : intended == null ? null : intended.Id;
            if (string.IsNullOrWhiteSpace(selectedId))
            {
                return ManualResult(SkillAuthoringOutcome.Error(
                    "Skill id is required.", null,
                    "invalid_skill_definition", false), false, null);
            }

            var currentId = baseId ?? selectedId;
            var current = FindStoredSkill(currentId);
            var expectedRevision = mutation.ExpectedRevision ?? string.Empty;
            var currentRevision = StateRevision(current);
            if (!string.Equals(expectedRevision, currentRevision,
                StringComparison.Ordinal))
            {
                return ManualResult(StaleLibraryMutation(
                    currentId, expectedRevision, currentRevision),
                    false, current);
            }

            if (string.Equals(kind, "delete", StringComparison.Ordinal))
            {
                return ExecutePreparedManualMutation(
                    SkillAuthoringCatalog.DeleteToolId,
                    new Dictionary<string, object>
                    {
                        ["id"] = currentId
                    }, expectedRevision, currentId);
            }

            var validation = SkillStore.ValidateDefinition(intended);
            if (!string.IsNullOrWhiteSpace(validation))
            {
                return ManualResult(SkillAuthoringOutcome.Error(
                    validation, null, "invalid_skill_definition", false),
                    false, current);
            }
            var reserved = ValidateAuthoredSkillId(intended.Id);
            if (reserved != null)
                return ManualResult(reserved, false, current);

            if (baseId != null && !string.Equals(baseId, intended.Id,
                StringComparison.Ordinal))
            {
                return ExecuteManualRename(
                    current, intended, expectedRevision);
            }

            var arguments = new Dictionary<string, object>
            {
                ["id"] = intended.Id,
                ["mode"] = current == null ? "createOnly" : "updateOnly",
                ["host"] = intended.Host,
                ["name"] = intended.Name,
                ["description"] = intended.Description,
                ["version"] = intended.Version,
                ["bodyMarkdown"] = intended.BodyMarkdown,
                ["enabled"] = intended.Enabled
            };
            return ExecutePreparedManualMutation(
                SkillAuthoringCatalog.UpsertToolId,
                arguments, expectedRevision, intended.Id);
        }

        internal SkillManualMutationResult ExecuteManualReferenceMutation(
            string kind, string skillId, string path, string content,
            string expectedRevision)
        {
            var current = FindStoredSkill(skillId);
            var currentRevision = StateRevision(current);
            if (!string.Equals(expectedRevision ?? string.Empty,
                currentRevision, StringComparison.Ordinal))
            {
                return ManualResult(StaleLibraryMutation(
                    skillId, expectedRevision, currentRevision),
                    false, current);
            }
            var arguments = new Dictionary<string, object>
            {
                ["id"] = skillId,
                ["referencePath"] = path
            };
            string toolId;
            if (string.Equals(kind, "upsert", StringComparison.Ordinal))
            {
                toolId = SkillAuthoringCatalog.ReferenceUpsertToolId;
                arguments["referenceMarkdown"] = content ?? string.Empty;
                arguments["mode"] = "upsert";
            }
            else if (string.Equals(kind, "delete", StringComparison.Ordinal))
            {
                toolId = SkillAuthoringCatalog.ReferenceDeleteToolId;
            }
            else
            {
                return ManualResult(SkillAuthoringOutcome.Error(
                    "Unknown skill reference mutation: " +
                    (kind ?? string.Empty), null,
                    "invalid_skill_mutation", false), false, current);
            }
            return ExecutePreparedManualMutation(
                toolId, arguments, currentRevision, skillId);
        }

        private SkillManualMutationResult ExecutePreparedManualMutation(
            string toolId,
            IDictionary<string, object> arguments,
            string expectedRevision,
            string resultId)
        {
            var preparation = PrepareMutation(toolId, arguments);
            if (preparation.Outcome.Status != SkillAuthoringOutcomeStatus.Ok)
            {
                return ManualResult(preparation.Outcome, false,
                    FindStoredSkill(resultId));
            }
            if (!string.Equals(preparation.BeforeRevision,
                expectedRevision ?? string.Empty, StringComparison.Ordinal))
            {
                return ManualResult(StaleLibraryMutation(
                    resultId, expectedRevision,
                    preparation.BeforeRevision), false,
                    FindStoredSkill(resultId));
            }
            var dispatched = false;
            var outcome = ExecuteMutation(
                toolId, arguments, preparation.PreparedStateJson,
                delegate { dispatched = true; });
            return ManualResult(outcome, dispatched,
                FindStoredSkill(resultId));
        }

        private SkillManualMutationResult ExecuteManualRename(
            SkillDefinition current,
            SkillDefinition supplied,
            string expectedRevision)
        {
            if (current == null)
            {
                return ManualResult(SkillAuthoringOutcome.Error(
                    "Custom skill not found: " +
                    (supplied == null ? string.Empty : supplied.Id),
                    null, "skill_not_found", false), false, null);
            }
            var collision = FindStoredSkill(supplied.Id);
            if (collision != null && !string.Equals(
                collision.Id, current.Id, StringComparison.Ordinal))
            {
                return ManualResult(SkillAuthoringOutcome.Error(
                    "Skill already exists: " + supplied.Id, null,
                    "skill_already_exists", false), false, current);
            }

            var intended = Clone(current);
            intended.Id = supplied.Id;
            intended.Host = supplied.Host;
            intended.Name = supplied.Name;
            intended.Description = supplied.Description;
            intended.Version = supplied.Version;
            intended.BodyMarkdown = supplied.BodyMarkdown;
            intended.Enabled = supplied.Enabled;
            intended.StoragePath = current.StoragePath;
            var validation = SkillStore.ValidateDefinition(intended);
            if (!string.IsNullOrWhiteSpace(validation))
            {
                return ManualResult(SkillAuthoringOutcome.Error(
                    validation, null, "invalid_skill_definition", false),
                    false, current);
            }
            var intendedRevision = StateRevision(intended);
            var beforeRevision = StateRevision(current);
            var live = FindStoredSkill(current.Id);
            if (!string.Equals(expectedRevision ?? string.Empty,
                    beforeRevision, StringComparison.Ordinal) ||
                !string.Equals(StateRevision(live), beforeRevision,
                    StringComparison.Ordinal))
            {
                return ManualResult(StaleLibraryMutation(
                    current.Id, expectedRevision, StateRevision(live)),
                    false, live);
            }
            var liveTarget = FindStoredSkill(intended.Id);
            if (liveTarget != null && !string.Equals(
                liveTarget.Id, current.Id, StringComparison.Ordinal))
            {
                return ManualResult(SkillAuthoringOutcome.Error(
                    "Skill already exists: " + intended.Id, null,
                    "skill_already_exists", false), false, live);
            }

            var dispatched = false;
            string mutationError = null;
            try
            {
                dispatched = true;
                _skillStore.SaveOne(intended);
            }
            catch (Exception ex)
            {
                mutationError = ex.Message;
            }
            var verified = FindStoredSkill(intended.Id);
            var actualRevision = StateRevision(verified);
            var oldStillPresent = !string.Equals(
                    current.Id, intended.Id,
                    StringComparison.OrdinalIgnoreCase) &&
                FindStoredSkill(current.Id) != null;
            var data = ResultData(intended.Id, "rename", null,
                beforeRevision, actualRevision,
                !string.Equals(beforeRevision, actualRevision,
                    StringComparison.Ordinal));
            data.ExpectedRevision = intendedRevision;
            SkillAuthoringOutcome outcome;
            if (!oldStillPresent && string.Equals(
                actualRevision, intendedRevision, StringComparison.Ordinal))
            {
                outcome = SkillAuthoringOutcome.Ok(
                    "Skill renamed: " + current.Id + " -> " + intended.Id,
                    data, SkillAuthoringEffect.VerifiedChange);
            }
            else if (!oldStillPresent && string.Equals(
                actualRevision, beforeRevision, StringComparison.Ordinal))
            {
                outcome = SkillAuthoringOutcome.Error(
                    string.IsNullOrWhiteSpace(mutationError)
                        ? "Skill rename was not applied: " + current.Id
                        : mutationError,
                    data, "skill_authoring_not_applied", false,
                    SkillAuthoringEffect.VerifiedNoChange);
            }
            else
            {
                outcome = SkillAuthoringOutcome.Unknown(
                    "Skill rename did not verify. Inspect the Skill Library before retrying." +
                    (string.IsNullOrWhiteSpace(mutationError)
                        ? string.Empty : " " + mutationError),
                    data, "skill_authoring_verification_failed");
            }
            return ManualResult(outcome, dispatched, verified);
        }

        private static SkillAuthoringOutcome StaleLibraryMutation(
            string id, string expectedRevision, string actualRevision)
        {
            return SkillAuthoringOutcome.Error(
                "Custom skill changed after the editor loaded it. Refresh the Skill Library before retrying.",
                ResultData(id, "stale", null,
                    expectedRevision, actualRevision, false),
                "skill_package_changed", true);
        }

        private static SkillManualMutationResult ManualResult(
            SkillAuthoringOutcome outcome,
            bool dispatched,
            SkillDefinition package)
        {
            return new SkillManualMutationResult(
                outcome, dispatched, SkillPackageSource.Capture(package));
        }

    }
}
