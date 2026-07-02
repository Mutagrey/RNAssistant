using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Tools
{
    internal sealed class ToolSafetyProfile
    {
        public bool Valid { get; set; }
        public string Error { get; set; }
        public bool MutatesDocument { get; set; }
        public bool MutatesLocalState { get; set; }
        public bool RequiresConfirmation { get; set; }
        public bool AgentCanRun { get; set; }
        public int RiskLevel { get; set; }
    }

    internal static class ToolSafetyPolicy
    {
        public static bool RequiresConfirmation(
            ToolDefinition tool,
            ToolSafetyProfile profile,
            AppSettings settings,
            bool dryRun,
            bool manualRun)
        {
            if (dryRun || manualRun || tool == null)
            {
                return false;
            }

            var effectiveSettings = settings ?? new AppSettings();
            if (effectiveSettings.AutoConfirmToolActions)
            {
                return false;
            }

            profile = profile ?? new ToolSafetyProfile();
            if (profile.RequiresConfirmation)
            {
                return true;
            }

            if (!profile.MutatesDocument)
            {
                return false;
            }

            return !CanAgentRunMutation(tool, profile);
        }

        public static bool EffectiveMutatesDocument(ToolDefinition tool, IEnumerable<ToolDefinition> knownTools)
        {
            return Resolve(tool, knownTools).MutatesDocument;
        }

        public static bool EffectiveMutatesLocalState(ToolDefinition tool, IEnumerable<ToolDefinition> knownTools)
        {
            return Resolve(tool, knownTools).MutatesLocalState;
        }

        public static int EffectiveRiskLevel(ToolDefinition tool, IEnumerable<ToolDefinition> knownTools)
        {
            return Resolve(tool, knownTools).RiskLevel;
        }

        public static ToolSafetyProfile Resolve(ToolDefinition tool, IEnumerable<ToolDefinition> knownTools)
        {
            return Resolve(
                tool,
                (knownTools ?? new ToolDefinition[0]).Where(t => t != null).ToList(),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                0);
        }

        private static bool CanAgentRunMutation(ToolDefinition tool, ToolSafetyProfile profile)
        {
            return tool != null &&
                tool.BuiltIn &&
                profile != null &&
                profile.AgentCanRun;
        }

        private static ToolSafetyProfile Resolve(
            ToolDefinition tool,
            IReadOnlyList<ToolDefinition> knownTools,
            ISet<string> path,
            int depth)
        {
            if (tool == null)
            {
                return Invalid("Tool definition is missing.");
            }

            var id = tool.Id ?? string.Empty;
            if (depth > 8)
            {
                return Invalid("Pipeline nesting limit exceeded: " + id);
            }

            if (!path.Add(id))
            {
                return Invalid("Pipeline cycle detected: " + id);
            }

            var isVba = string.Equals(tool.Executor, "vba", StringComparison.OrdinalIgnoreCase);
            var profile = new ToolSafetyProfile
            {
                Valid = true,
                MutatesDocument = tool.MutatesDocument || isVba,
                MutatesLocalState = tool.MutatesLocalState,
                RequiresConfirmation = tool.RequiresConfirmation,
                AgentCanRun = tool.AgentCanRun,
                RiskLevel = tool.RiskLevel
            };
            if (profile.MutatesDocument && profile.RiskLevel <= 0)
            {
                profile.RiskLevel = 2;
            }
            if (isVba)
            {
                profile.RiskLevel = Math.Max(3, profile.RiskLevel);
            }

            if (!string.Equals(tool.Executor, "pipeline", StringComparison.OrdinalIgnoreCase))
            {
                path.Remove(id);
                return profile;
            }

            JObject pipeline;
            try
            {
                pipeline = JObject.Parse(tool.PipelineJson ?? string.Empty);
            }
            catch (JsonException ex)
            {
                path.Remove(id);
                return Invalid("Invalid pipeline JSON for " + id + ": " + ex.Message);
            }

            var steps = pipeline["steps"] as JArray;
            if (steps == null || steps.Count == 0)
            {
                path.Remove(id);
                return Invalid("Pipeline has no steps: " + id);
            }

            foreach (var step in steps)
            {
                var stepObject = step as JObject;
                var nestedId = stepObject == null ? null : (string)stepObject["toolId"];
                if (string.IsNullOrWhiteSpace(nestedId))
                {
                    path.Remove(id);
                    return Invalid("Pipeline step has no toolId: " + id);
                }

                var nested = knownTools.FirstOrDefault(t =>
                    string.Equals(t.Id, nestedId, StringComparison.OrdinalIgnoreCase));
                if (nested == null)
                {
                    path.Remove(id);
                    return Invalid("Pipeline references unknown tool: " + nestedId);
                }

                var nestedProfile = Resolve(nested, knownTools, path, depth + 1);
                if (!nestedProfile.Valid)
                {
                    path.Remove(id);
                    return nestedProfile;
                }

                profile.MutatesDocument |= nestedProfile.MutatesDocument;
                profile.MutatesLocalState |= nestedProfile.MutatesLocalState;
                profile.RequiresConfirmation |= nestedProfile.RequiresConfirmation;
                profile.AgentCanRun &= nestedProfile.AgentCanRun;
                profile.RiskLevel = Math.Max(profile.RiskLevel, nestedProfile.RiskLevel);
            }

            path.Remove(id);
            return profile;
        }

        private static ToolSafetyProfile Invalid(string error)
        {
            return new ToolSafetyProfile
            {
                Valid = false,
                Error = error ?? "Invalid tool safety metadata.",
                AgentCanRun = false
            };
        }
    }
}
