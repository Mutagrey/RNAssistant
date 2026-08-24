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
            var catalog = BuildCatalog(knownTools);
            if (tool != null && !string.IsNullOrWhiteSpace(tool.Id))
            {
                catalog[tool.Id] = tool;
            }
            return Resolve(tool, catalog, new Dictionary<string, ToolSafetyProfile>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
        }

        public static IDictionary<string, ToolSafetyProfile> ResolveAll(IEnumerable<ToolDefinition> tools)
        {
            var catalog = BuildCatalog(tools);
            var profiles = new Dictionary<string, ToolSafetyProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in catalog)
            {
                Resolve(pair.Value, catalog, profiles, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
            }
            return profiles;
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
            IDictionary<string, ToolDefinition> knownTools,
            IDictionary<string, ToolSafetyProfile> cache,
            ISet<string> path,
            int depth)
        {
            if (tool == null)
            {
                return Invalid("Tool definition is missing.");
            }

            var id = tool.Id ?? string.Empty;
            ToolSafetyProfile cached;
            if (cache.TryGetValue(id, out cached))
            {
                return cached;
            }
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
                ApplyImplicitConfirmation(tool, profile);
                return Complete(id, profile, cache, path);
            }

            PipelineDefinition pipeline;
            string parseError;
            if (!PipelineDefinitionParser.TryParse(id, tool.PipelineJson, out pipeline, out parseError))
            {
                return Complete(id, Invalid(parseError), cache, path);
            }

            foreach (var step in pipeline.Steps)
            {
                ToolDefinition nested;
                if (!knownTools.TryGetValue(step.ToolId, out nested))
                {
                    return Complete(id, Invalid("Pipeline references unknown tool: " + step.ToolId), cache, path);
                }

                var nestedProfile = Resolve(nested, knownTools, cache, path, depth + 1);
                if (!nestedProfile.Valid)
                {
                    return Complete(id, nestedProfile, cache, path);
                }

                profile.MutatesDocument |= nestedProfile.MutatesDocument;
                profile.MutatesLocalState |= nestedProfile.MutatesLocalState;
                profile.RequiresConfirmation |= nestedProfile.RequiresConfirmation;
                profile.AgentCanRun &= nestedProfile.AgentCanRun;
                profile.RiskLevel = Math.Max(profile.RiskLevel, nestedProfile.RiskLevel);
            }

            ApplyImplicitConfirmation(tool, profile);
            return Complete(id, profile, cache, path);
        }

        private static void ApplyImplicitConfirmation(ToolDefinition tool, ToolSafetyProfile profile)
        {
            if (profile != null && profile.MutatesDocument && !CanAgentRunMutation(tool, profile))
            {
                profile.RequiresConfirmation = true;
            }
        }

        private static Dictionary<string, ToolDefinition> BuildCatalog(IEnumerable<ToolDefinition> tools)
        {
            var catalog = new Dictionary<string, ToolDefinition>(StringComparer.OrdinalIgnoreCase);
            var toolList = (tools ?? new ToolDefinition[0])
                .Where(tool => tool != null && !string.IsNullOrWhiteSpace(tool.Id))
                .ToList();
            foreach (var tool in toolList)
            {
                if (!catalog.ContainsKey(tool.Id))
                {
                    catalog.Add(tool.Id, tool);
                }
            }
            foreach (var alias in VbaPublicToolIds.LegacyAliases())
            {
                ToolDefinition canonical;
                if (!catalog.ContainsKey(alias.Key) && catalog.TryGetValue(alias.Value, out canonical))
                {
                    catalog.Add(alias.Key, canonical);
                }
            }
            foreach (var tool in toolList.Where(item => item.Id.StartsWith("common.vba_", StringComparison.OrdinalIgnoreCase)))
            {
                var suffix = tool.Id.Substring("common.".Length);
                foreach (var host in new[] { "excel", "word", "powerpoint" })
                {
                    var legacyId = host + "." + suffix;
                    if (toolList.Any(item => item.Id.StartsWith(host + ".", StringComparison.OrdinalIgnoreCase)) &&
                        !catalog.ContainsKey(legacyId))
                    {
                        catalog.Add(legacyId, tool);
                    }
                }
            }
            foreach (var alias in VbaPublicToolIds.LegacyAliases())
            {
                var aliasSuffix = alias.Key.Substring("common.".Length);
                var canonicalSuffix = alias.Value.Substring("common.".Length);
                foreach (var host in new[] { "excel", "word", "powerpoint" })
                {
                    ToolDefinition canonical;
                    var legacyId = host + "." + aliasSuffix;
                    if (!catalog.ContainsKey(legacyId) && catalog.TryGetValue(host + "." + canonicalSuffix, out canonical))
                    {
                        catalog.Add(legacyId, canonical);
                    }
                }
            }
            foreach (var alias in BuiltInToolAliases.Aliases())
            {
                ToolDefinition canonical;
                if (!catalog.ContainsKey(alias.Key) && catalog.TryGetValue(alias.Value, out canonical))
                {
                    catalog.Add(alias.Key, canonical);
                }
            }
            return catalog;
        }

        private static ToolSafetyProfile Complete(string id, ToolSafetyProfile profile, IDictionary<string, ToolSafetyProfile> cache, ISet<string> path)
        {
            path.Remove(id);
            cache[id] = profile;
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
