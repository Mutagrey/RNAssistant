using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Tools
{
    internal static class ToolSafetyPolicy
    {
        public static bool RequiresConfirmation(ToolDefinition tool, AppSettings settings, bool dryRun, bool manualRun)
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

            if (tool.RequiresConfirmation)
            {
                return true;
            }

            if (!tool.MutatesDocument)
            {
                return false;
            }

            return !CanAgentRunMutation(tool, effectiveSettings);
        }

        public static bool EffectiveMutatesDocument(ToolDefinition tool, IEnumerable<ToolDefinition> knownTools)
        {
            return EffectiveMutatesDocument(tool, knownTools, 0);
        }

        public static bool CanAgentRunMutation(ToolDefinition tool, AppSettings settings)
        {
            return settings != null &&
                settings.AgentModeEnabled != false &&
                tool != null &&
                tool.BuiltIn &&
                tool.AgentCanRun;
        }

        private static bool EffectiveMutatesDocument(ToolDefinition tool, IEnumerable<ToolDefinition> knownTools, int depth)
        {
            if (tool == null)
            {
                return false;
            }

            if (tool.MutatesDocument)
            {
                return true;
            }

            if (depth > 8 ||
                !string.Equals(tool.Executor, "pipeline", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(tool.PipelineJson))
            {
                return false;
            }

            JObject pipeline;
            try
            {
                pipeline = JObject.Parse(tool.PipelineJson);
            }
            catch (JsonException)
            {
                return false;
            }

            var steps = pipeline["steps"] as JArray;
            if (steps == null)
            {
                return false;
            }

            foreach (var step in steps.OfType<JObject>())
            {
                var nestedId = (string)step["toolId"];
                if (string.IsNullOrWhiteSpace(nestedId))
                {
                    continue;
                }

                var nested = (knownTools ?? new ToolDefinition[0]).FirstOrDefault(t =>
                    t != null && string.Equals(t.Id, nestedId, StringComparison.OrdinalIgnoreCase));
                if (EffectiveMutatesDocument(nested, knownTools, depth + 1))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
