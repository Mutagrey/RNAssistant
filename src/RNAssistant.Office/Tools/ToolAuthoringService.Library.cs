using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class ToolAuthoringService
    {
        internal ToolManualMutationResult ExecuteManualCoreMutation(
            ToolLibraryCoreMutation mutation)
        {
            if (mutation == null)
                return ManualResult(ToolAuthoringOutcome.Error(
                    "Tool mutation is required.", null,
                    "invalid_tool_mutation", false), false, null,
                    null, null, null);
            var kind = mutation.Kind ?? string.Empty;
            if (!string.Equals(kind, "upsert", StringComparison.Ordinal) &&
                !string.Equals(kind, "delete", StringComparison.Ordinal))
            {
                return ManualResult(ToolAuthoringOutcome.Error(
                    "Unknown Tool Library mutation: " + kind, null,
                    "invalid_tool_mutation", false), false, null,
                    null, kind, null);
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
                return ManualResult(ToolAuthoringOutcome.Error(
                    "Tool id is required.", null,
                    "invalid_tool_definition", false), false, null,
                    selectedId, kind, null);
            }

            var currentId = baseId ?? selectedId;
            var current = FindStoredTool(currentId);
            var currentRevision = StateRevision(current);
            if (!string.Equals(mutation.ExpectedRevision ?? string.Empty,
                currentRevision, StringComparison.Ordinal))
            {
                return ManualResult(StaleLibraryMutation(
                    currentId), false, current, currentId, "stale",
                    mutation.ExpectedRevision);
            }

            if (string.Equals(kind, "delete", StringComparison.Ordinal))
            {
                return ExecutePreparedManualMutation(
                    ToolAuthoringCatalog.DeleteToolId,
                    new Dictionary<string, object> { ["id"] = currentId },
                    currentRevision, currentId, "delete");
            }

            if (intended == null || intended.BuiltIn)
            {
                return ManualResult(ToolAuthoringOutcome.Error(
                    "A writable custom tool package is required.", null,
                    "invalid_tool_definition", false), false, current,
                    selectedId, "upsert", currentRevision);
            }
            if (baseId != null && !string.Equals(baseId, intended.Id,
                StringComparison.Ordinal))
            {
                return ExecuteManualRename(
                    current, intended, currentRevision);
            }

            var arguments = MutationArguments(
                intended, current == null ? "createOnly" : "updateOnly");
            return ExecutePreparedManualMutation(
                ToolAuthoringCatalog.UpsertToolId, arguments,
                currentRevision, intended.Id,
                current == null ? "create" : "update");
        }

        private ToolManualMutationResult ExecutePreparedManualMutation(
            string toolId,
            IDictionary<string, object> arguments,
            string expectedRevision,
            string id,
            string operation)
        {
            var preparation = PrepareMutation(toolId, arguments);
            if (preparation.Outcome.Status != ToolAuthoringOutcomeStatus.Ok)
            {
                return ManualResult(preparation.Outcome, false,
                    FindStoredTool(id), id, operation, expectedRevision);
            }
            var live = FindStoredTool(id);
            if (!string.Equals(StateRevision(live),
                expectedRevision ?? string.Empty, StringComparison.Ordinal))
            {
                return ManualResult(StaleLibraryMutation(id), false,
                    live, id, "stale", expectedRevision);
            }
            var dispatched = false;
            var outcome = ExecuteMutation(toolId, arguments,
                preparation.PreparedStateJson,
                delegate { dispatched = true; });
            return ManualResult(outcome, dispatched,
                FindStoredTool(id), id, operation, expectedRevision);
        }

        private ToolManualMutationResult ExecuteManualRename(
            ToolCatalogEntry current,
            ToolCatalogEntry intended,
            string expectedRevision)
        {
            if (current == null)
            {
                return ManualResult(ToolAuthoringOutcome.Error(
                    "Custom tool not found: " +
                    (intended == null ? string.Empty : intended.Id), null,
                    "tool_not_found", false), false, null,
                    intended == null ? null : intended.Id,
                    "rename", expectedRevision);
            }
            var collision = FindStoredTool(intended.Id);
            if (collision != null && !string.Equals(collision.Id,
                current.Id, StringComparison.Ordinal))
            {
                return ManualResult(ToolAuthoringOutcome.Error(
                    "Custom tool already exists: " + intended.Id, null,
                    "tool_already_exists", false), false, current,
                    current.Id, "rename", expectedRevision);
            }
            var validation = ValidateDefinition(intended);
            if (!validation.Success)
            {
                return ManualResult(validation, false, current,
                    current.Id, "rename", expectedRevision);
            }
            var intendedRevision = StateRevision(intended);
            var live = FindStoredTool(current.Id);
            if (!string.Equals(StateRevision(live), expectedRevision,
                StringComparison.Ordinal))
            {
                return ManualResult(StaleLibraryMutation(current.Id),
                    false, live, current.Id, "stale", expectedRevision);
            }
            var liveTarget = FindStoredTool(intended.Id);
            if (liveTarget != null && !string.Equals(liveTarget.Id,
                current.Id, StringComparison.Ordinal))
            {
                return ManualResult(ToolAuthoringOutcome.Error(
                    "Custom tool already exists: " + intended.Id, null,
                    "tool_already_exists", false), false, live,
                    current.Id, "rename", expectedRevision);
            }

            var dispatched = false;
            string failure = null;
            try
            {
                dispatched = true;
                _toolStore.SaveOne(intended);
                if (!_toolStore.Delete(current.Id))
                    failure = "The previous tool id could not be removed.";
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }
            var verified = FindStoredTool(intended.Id);
            var oldStillPresent = FindStoredTool(current.Id) != null;
            var actualRevision = StateRevision(verified);
            ToolAuthoringOutcome outcome;
            if (!oldStillPresent && string.Equals(actualRevision,
                    intendedRevision, StringComparison.Ordinal) &&
                string.IsNullOrWhiteSpace(failure))
            {
                outcome = ToolAuthoringOutcome.Ok(
                    "Custom tool renamed: " + current.Id + " -> " +
                    intended.Id, null,
                    ToolAuthoringEffect.VerifiedChange);
            }
            else
            {
                outcome = ToolAuthoringOutcome.Unknown(
                    string.IsNullOrWhiteSpace(failure)
                        ? "Custom tool rename did not verify."
                        : failure, null,
                    "tool_authoring_verification_failed");
            }
            return new ToolManualMutationResult(
                outcome, dispatched, verified,
                intended.Id, "rename", expectedRevision, actualRevision);
        }

        private static IDictionary<string, object> MutationArguments(
            ToolCatalogEntry tool, string mode)
        {
            return new Dictionary<string, object>
            {
                ["id"] = tool.Id ?? string.Empty,
                ["mode"] = mode,
                ["host"] = tool.Host ?? "Common",
                ["name"] = tool.Name ?? tool.Id ?? string.Empty,
                ["description"] = tool.Description ?? string.Empty,
                ["parameters"] = tool.ArgumentSchemaJson ?? string.Empty,
                ["executor"] = "vba",
                ["components"] = new JArray((tool.Components ??
                    new List<ToolPackageComponentDefinition>())
                    .Where(component => component != null)
                    .Select(component => new JObject
                    {
                        ["name"] = component.Name,
                        ["type"] = component.Type,
                        ["fileName"] = component.FileName,
                        ["code"] = component.Code
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

        private static string StateRevision(ToolCatalogEntry tool)
        {
            return LibraryRevision(tool);
        }

        internal static string LibraryRevision(ToolCatalogEntry tool)
        {
            return tool == null ? string.Empty : StateHash(tool);
        }

        private static ToolAuthoringOutcome StaleLibraryMutation(string id)
        {
            return ToolAuthoringOutcome.Error(
                "Custom tool changed after the editor loaded it. Refresh the Tool Library before retrying.",
                null, "tool_package_changed", true);
        }

        private static ToolManualMutationResult ManualResult(
            ToolAuthoringOutcome outcome,
            bool dispatched,
            ToolCatalogEntry package,
            string id,
            string operation,
            string previousRevision)
        {
            return new ToolManualMutationResult(
                outcome, dispatched, package, id, operation,
                previousRevision, StateRevision(package));
        }
    }
}
