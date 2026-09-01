using RNAssistant.Core.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
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
            ToolCatalogEntry tool,
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

        public static bool EffectiveMutatesDocument(ToolCatalogEntry tool, IEnumerable<ToolCatalogEntry> knownTools)
        {
            return Resolve(tool, knownTools).MutatesDocument;
        }

        public static bool EffectiveMutatesLocalState(ToolCatalogEntry tool, IEnumerable<ToolCatalogEntry> knownTools)
        {
            return Resolve(tool, knownTools).MutatesLocalState;
        }

        public static int EffectiveRiskLevel(ToolCatalogEntry tool, IEnumerable<ToolCatalogEntry> knownTools)
        {
            return Resolve(tool, knownTools).RiskLevel;
        }

        public static ToolSafetyProfile Resolve(ToolCatalogEntry tool, IEnumerable<ToolCatalogEntry> knownTools)
        {
            if (tool == null) return Invalid("Tool definition is missing.");
            if (string.Equals(tool.Executor, "pipeline", StringComparison.OrdinalIgnoreCase))
                return Invalid("Pipelines are disabled during stabilization.");

            var isVba = string.Equals(tool.Executor, "vba", StringComparison.OrdinalIgnoreCase);
            var profile = new ToolSafetyProfile
            {
                Valid = true,
                MutatesDocument = tool.MutatesDocument || isVba,
                MutatesLocalState = tool.MutatesLocalState,
                RequiresConfirmation = tool.RequiresConfirmation || tool.Policy != null && tool.Policy.RequiresConfirmation,
                AgentCanRun = tool.AgentCanRun,
                RiskLevel = tool.RiskLevel
            };
            if (profile.MutatesDocument && profile.RiskLevel <= 0) profile.RiskLevel = 2;
            if (isVba) profile.RiskLevel = Math.Max(3, profile.RiskLevel);
            ApplyImplicitConfirmation(tool, profile);
            return profile;
        }

        public static IDictionary<string, ToolSafetyProfile> ResolveAll(IEnumerable<ToolCatalogEntry> tools)
        {
            return (tools ?? new ToolCatalogEntry[0])
                .Where(tool => tool != null && !string.IsNullOrWhiteSpace(tool.Id))
                .GroupBy(tool => tool.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => Resolve(group.First(), null), StringComparer.OrdinalIgnoreCase);
        }

        private static bool CanAgentRunMutation(ToolCatalogEntry tool, ToolSafetyProfile profile)
        {
            return tool != null &&
                tool.BuiltIn &&
                profile != null &&
                profile.AgentCanRun;
        }

        private static void ApplyImplicitConfirmation(ToolCatalogEntry tool, ToolSafetyProfile profile)
        {
            if (profile != null && profile.MutatesDocument && !CanAgentRunMutation(tool, profile))
            {
                profile.RequiresConfirmation = true;
            }
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
