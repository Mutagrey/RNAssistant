using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed class AgentActionValidator
    {
        public PlannerValidationResult Validate(
            AgentPlannerStep step,
            ToolCatalogSlice slice,
            RoutedTask route,
            IReadOnlyList<AgentObservation> observations,
            IReadOnlyList<ToolDefinition> allTools)
        {
            if (step == null || string.IsNullOrWhiteSpace(step.ToolId))
            {
                return PlannerValidationResult.Fail("Planner step has no toolId.");
            }
            var tool = slice == null ? null : slice.Find(step.ToolId);
            if (tool == null)
            {
                var known = AgentToolCatalogResolver.Find(allTools, step.ToolId);
                if (known == null)
                {
                    var suggestions = ToolIdSuggester.Suggest(step.ToolId, allTools, 3);
                    return PlannerValidationResult.Fail(
                        "Unknown tool id: " + step.ToolId + ". Use only exact ids from AVAILABLE_TOOLS." +
                        (suggestions.Count == 0 ? string.Empty : " Did you mean: " + string.Join(", ", suggestions.ToArray()) + "?"));
                }

                var exclusion = slice == null
                    ? null
                    : slice.Excluded.FirstOrDefault(item =>
                        item != null && string.Equals(item.ToolId, step.ToolId, StringComparison.OrdinalIgnoreCase));
                return PlannerValidationResult.Fail(
                    "Tool is excluded from the current route: " + step.ToolId + "." +
                    (exclusion == null
                        ? string.Empty
                        : " Reason: " + exclusion.Reason + ". " + exclusion.Detail));
            }
            if (route != null &&
                route.RequiresInspection &&
                (tool.MutatesDocument || tool.MutatesLocalState) &&
                !string.Equals(tool.Id, "common.skills_load", StringComparison.OrdinalIgnoreCase) &&
                !HasInspectionObservation(observations))
            {
                return PlannerValidationResult.Fail("Target must be inspected before mutation. Use a read/context tool first.");
            }

            var command = new ToolCommand { ToolId = step.ToolId, Description = step.Reason, ToolCallId = step.ToolCallId };
            foreach (var pair in step.Arguments ?? new Dictionary<string, object>())
            {
                command.Arguments[pair.Key] = pair.Value;
            }
            return PlannerValidationResult.Ok(command, tool);
        }

        private static bool HasInspectionObservation(IEnumerable<AgentObservation> observations)
        {
            foreach (var observation in observations ?? new AgentObservation[0])
            {
                if (observation != null &&
                    string.Equals(observation.Status, "success", StringComparison.OrdinalIgnoreCase) &&
                    !observation.Mutation &&
                    !observation.LocalMutation &&
                    string.Equals(observation.Purpose, AgentObservationPurposes.Inspection, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
