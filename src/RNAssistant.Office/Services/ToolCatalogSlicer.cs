using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal sealed class ToolCatalogSlicer
    {
        public ToolCatalogSlice Slice(
            RoutedTask route,
            IEnumerable<ToolDefinition> tools,
            int maxTools = 24,
            AppSettings settings = null)
        {
            var slice = new ToolCatalogSlice();
            var host = route == null ? string.Empty : route.App ?? string.Empty;
            foreach (var tool in tools ?? new ToolDefinition[0])
            {
                var exclusion = CandidateExclusion(tool, host);
                if (exclusion == null && !AllowedForPhase(tool, route))
                {
                    exclusion = Exclude(tool, "wrong_phase", "Only read-only tools are available during verification.");
                }
                if (exclusion == null && !AllowedForExplicitMode(tool, route))
                {
                    exclusion = Exclude(tool, "explicit_mode", "Tool is outside the active explicit workspace mode.");
                }
                if (exclusion != null)
                {
                    slice.Excluded.Add(exclusion);
                    continue;
                }
                slice.Tools.Add(tool);
            }

            var ordered = slice.Tools
                .GroupBy(tool => tool.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(tool => ToolPriority(tool, host))
                .ToList();
            slice.Tools = SelectBalancedTools(ordered, host, route, Math.Max(1, maxTools));
            FitRequestBudget(slice, settings);

            var selectedIds = new HashSet<string>(slice.Tools.Select(tool => tool.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var omitted in ordered.Where(tool =>
                !selectedIds.Contains(tool.Id) &&
                !slice.Excluded.Any(item => string.Equals(item.ToolId, tool.Id, StringComparison.OrdinalIgnoreCase))))
            {
                slice.Excluded.Add(Exclude(omitted, "selection_limit", "The balanced tool set filled the request budget."));
            }
            return slice;
        }

        private static ToolExclusion CandidateExclusion(ToolDefinition tool, string host)
        {
            if (tool == null) return new ToolExclusion { ToolId = string.Empty, Reason = "invalid_definition", Detail = "Tool definition is null." };
            if (string.IsNullOrWhiteSpace(tool.Id)) return Exclude(tool, "missing_id", "Tool id is empty.");
            if (!tool.Enabled) return Exclude(tool, "disabled", "Tool is disabled.");
            if (!string.Equals(tool.CapabilityStatus ?? "available", "available", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(tool.CapabilityStatus ?? "available", "partial", StringComparison.OrdinalIgnoreCase))
            {
                return Exclude(tool, "capability_unavailable", "Capability status is " + tool.CapabilityStatus + ".");
            }
            if (!string.Equals(tool.Host, host, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(tool.Host, "Common", StringComparison.OrdinalIgnoreCase))
            {
                return Exclude(tool, "wrong_host", "Tool host " + tool.Host + " does not match " + host + ".");
            }
            return null;
        }

        private static bool AllowedForPhase(ToolDefinition tool, RoutedTask route)
        {
            return route == null ||
                !string.Equals(route.Phase, AgentPhases.Verification, StringComparison.OrdinalIgnoreCase) ||
                tool != null && !tool.MutatesDocument && !tool.MutatesLocalState;
        }

        private static bool AllowedForExplicitMode(ToolDefinition tool, RoutedTask route)
        {
            if (route == null || !string.Equals(route.TaskType, "html", StringComparison.OrdinalIgnoreCase)) return true;
            return IsControlTool(tool) ||
                tool != null && (tool.Id ?? string.Empty).StartsWith("common.html_workspace_", StringComparison.OrdinalIgnoreCase);
        }

        private static List<ToolDefinition> SelectBalancedTools(
            IReadOnlyList<ToolDefinition> ordered,
            string host,
            RoutedTask route,
            int limit)
        {
            var selected = new List<ToolDefinition>();
            Action<IEnumerable<ToolDefinition>, int> add = (source, count) =>
            {
                foreach (var tool in source)
                {
                    if (selected.Count >= limit || count <= 0) break;
                    if (selected.Any(item => string.Equals(item.Id, tool.Id, StringComparison.OrdinalIgnoreCase))) continue;
                    selected.Add(tool);
                    count -= 1;
                }
            };

            add(ordered.Where(IsControlTool), 1);
            if (route != null && string.Equals(route.Phase, AgentPhases.Verification, StringComparison.OrdinalIgnoreCase))
            {
                add(ordered.Where(IsReadOnly), limit - selected.Count);
                return selected;
            }

            add(ordered.Where(tool => IsHostTool(tool, host) && (tool.MutatesDocument || tool.MutatesLocalState)), Math.Max(1, limit / 2));
            add(ordered.Where(tool => IsHostTool(tool, host) && IsReadOnly(tool)), Math.Max(2, limit / 3));
            add(ordered.Where(tool => !IsHostTool(tool, host)), Math.Max(2, limit / 4));
            add(ordered, limit - selected.Count);
            return selected;
        }

        private static bool IsHostTool(ToolDefinition tool, string host)
        {
            return tool != null && string.Equals(tool.Host, host, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsReadOnly(ToolDefinition tool)
        {
            return tool != null && !tool.MutatesDocument && !tool.MutatesLocalState;
        }

        private static int ToolPriority(ToolDefinition tool, string host)
        {
            if (IsControlTool(tool)) return -10;
            return IsHostTool(tool, host) ? 0 : 10;
        }

        private static bool IsControlTool(ToolDefinition tool)
        {
            return tool != null && string.Equals(tool.Id, "common.skills_load", StringComparison.OrdinalIgnoreCase);
        }

        private static ToolExclusion Exclude(ToolDefinition tool, string reason, string detail)
        {
            return new ToolExclusion
            {
                ToolId = tool == null ? string.Empty : tool.Id,
                Reason = reason,
                Detail = detail
            };
        }

        private static void FitRequestBudget(ToolCatalogSlice slice, AppSettings settings)
        {
            if (slice == null || settings == null || slice.Tools.Count <= 1) return;
            var limit = Math.Max(512, ModelContextBudget.InputBudgetTokens(settings) / 2);
            if (EstimateRequestTokens(slice.Tools, settings) <= limit) return;

            var selectedCount = 1;
            var low = 1;
            var high = slice.Tools.Count - 1;
            while (low <= high)
            {
                var middle = low + (high - low) / 2;
                if (EstimateRequestTokens(slice.Tools.Take(middle).ToList(), settings) <= limit)
                {
                    selectedCount = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            for (var index = slice.Tools.Count - 1; index >= selectedCount; index--)
            {
                slice.Excluded.Add(Exclude(slice.Tools[index], "request_token_limit", "Tool schema was omitted to keep the request inside the model context budget."));
            }
            slice.Tools.RemoveRange(selectedCount, slice.Tools.Count - selectedCount);
        }

        private static int EstimateRequestTokens(IReadOnlyList<ToolDefinition> tools, AppSettings settings)
        {
            var options = AgentPlannerCompletionRunner.BuildOptions(settings.AgentResponseMode, tools);
            var structured = options.NativeTools ||
                string.Equals(options.ResponseFormat, LlmResponseFormats.JsonSchema, StringComparison.OrdinalIgnoreCase);
            return ModelContextBudget.EstimateRequestOptionsTokens(options) + (tools ?? new ToolDefinition[0]).Sum(tool => tool == null
                ? 0
                : 16 + ModelContextBudget.EstimateTextTokens(
                    (tool.Id ?? string.Empty) + "\n" +
                    (tool.Description ?? string.Empty) + "\n" +
                    (tool.ArgumentSchemaJson ?? string.Empty) + "\n" +
                    (tool.UseWhen ?? string.Empty) + "\n" +
                    (tool.DoNotUseWhen ?? string.Empty) + "\n" +
                    (structured ? string.Empty : tool.ExamplesJson ?? string.Empty)));
        }
    }
}
