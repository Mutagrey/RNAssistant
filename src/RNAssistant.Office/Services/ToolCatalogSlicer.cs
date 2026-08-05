using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal sealed class ToolCatalogSlicer
    {
        public ToolCatalogSlice Slice(
            RoutedTask route,
            IEnumerable<ToolDefinition> tools,
            IReadOnlyList<AgentObservation> observations,
            int maxTools = 24,
            bool allowAgentToolAuthoring = false)
        {
            var slice = new ToolCatalogSlice();
            if (route != null && !route.RequiresTool)
            {
                return slice;
            }
            var host = route == null ? string.Empty : route.App ?? string.Empty;
            foreach (var tool in tools ?? new ToolDefinition[0])
            {
                var exclusion = CandidateExclusion(tool, host);
                if (exclusion != null)
                {
                    slice.Excluded.Add(exclusion);
                    continue;
                }
                if (!AllowedForPhase(tool, route))
                {
                    slice.Excluded.Add(Exclude(tool, "wrong_phase", "Tool risk or mutation mode is not allowed in phase " + (route == null ? string.Empty : route.Phase) + "."));
                    continue;
                }
                if (!Relevant(tool, route) && !OptionalToolAuthoring(tool, route, allowAgentToolAuthoring))
                {
                    slice.Excluded.Add(Exclude(tool, "not_relevant", "Tool does not match task type " + (route == null ? string.Empty : route.TaskType) + "."));
                    continue;
                }
                slice.Tools.Add(tool);
            }

            var ordered = slice.Tools
                .GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(t => ToolPriority(t, route))
                .ThenBy(t => t.RiskLevel)
                .ThenBy(t => t.Id)
                .ToList();
            slice.Tools = SelectBalancedTools(ordered, route, Math.Max(8, Math.Min(64, maxTools)));
            var selectedIds = new HashSet<string>(slice.Tools.Select(tool => tool.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var omitted in ordered.Where(tool => !selectedIds.Contains(tool.Id)))
            {
                slice.Excluded.Add(Exclude(omitted, "selection_limit", "A higher-priority balanced set filled the prompt tool budget."));
            }
            return slice;
        }

        private static ToolExclusion CandidateExclusion(ToolDefinition tool, string host)
        {
            if (tool == null)
            {
                return new ToolExclusion { ToolId = string.Empty, Reason = "invalid_definition", Detail = "Tool definition is null." };
            }
            if (string.IsNullOrWhiteSpace(tool.Id))
            {
                return Exclude(tool, "missing_id", "Tool id is empty.");
            }
            if (!tool.Enabled)
            {
                return Exclude(tool, "disabled", "Tool is disabled.");
            }
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

        private static List<ToolDefinition> SelectBalancedTools(
            IReadOnlyList<ToolDefinition> ordered,
            RoutedTask route,
            int limit)
        {
            var selected = new List<ToolDefinition>();
            Action<IEnumerable<ToolDefinition>, int> add = (source, count) =>
            {
                foreach (var tool in source)
                {
                    if (selected.Count >= limit || count <= 0)
                    {
                        break;
                    }
                    if (selected.Any(existing => string.Equals(existing.Id, tool.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                    selected.Add(tool);
                    count -= 1;
                }
            };

            var mutationPhase = route != null &&
                string.Equals(route.Phase, AgentPhases.Mutation, StringComparison.OrdinalIgnoreCase);
            if (mutationPhase)
            {
                add(ordered.Where(tool => tool.MutatesDocument), Math.Max(1, (int)Math.Ceiling(limit * 0.6)));
                add(ordered.Where(tool => !tool.MutatesDocument && LooksLikeInspectionTool(tool)), Math.Max(2, limit / 4));
            }
            else
            {
                add(ordered.Where(tool => !tool.MutatesDocument && LooksLikeInspectionTool(tool)), Math.Max(4, limit / 3));
            }
            add(ordered, limit - selected.Count);
            return selected;
        }

        private static bool LooksLikeInspectionTool(ToolDefinition tool)
        {
            return tool != null && AgentText.ContainsAny(
                (tool.Id ?? string.Empty) + " " + (tool.UseWhen ?? string.Empty),
                "context", "selection", "summary", "read", "profile", "list", "search", "inspect", "get_");
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

        private static bool AllowedForPhase(ToolDefinition tool, RoutedTask route)
        {
            if (route == null)
            {
                return true;
            }
            if (string.Equals(route.Phase, AgentPhases.ReadOnly, StringComparison.OrdinalIgnoreCase))
            {
                return !tool.MutatesDocument && tool.RiskLevel <= 0;
            }
            if (string.Equals(route.Phase, AgentPhases.Verification, StringComparison.OrdinalIgnoreCase))
            {
                return !tool.MutatesDocument;
            }
            return tool.RiskLevel <= route.RiskAllowed;
        }

        private static bool Relevant(ToolDefinition tool, RoutedTask route)
        {
            if (route == null || string.IsNullOrWhiteSpace(route.TaskType))
            {
                return true;
            }

            var id = tool.Id ?? string.Empty;
            if (id.StartsWith("common.", StringComparison.OrdinalIgnoreCase) && route.TaskType != "html" && route.TaskType != "tool_authoring")
            {
                return false;
            }
            if (route.TaskType == "content")
            {
                return true;
            }
            if (route.TaskType == "formatting")
            {
                return AgentText.ContainsAny(id, "context", "selection", "summary", "read", "profile", "format", "autofit");
            }
            if (route.TaskType == "html")
            {
                return AgentText.ContainsAny(id, "html_workspace", "prompts_read", "skills_read", "tools_read");
            }
            if (route.TaskType == "tool_authoring")
            {
                return AgentText.ContainsAny(id, "tools_", "skills_", "prompts_");
            }
            if (route.TaskType == "chart")
            {
                return AgentText.ContainsAny(id, "context", "selection", "summary", "read", "profile", "chart");
            }
            if (route.TaskType == "mail_search")
            {
                return AgentText.ContainsAny(id, "context", "read", "search", "mail", "attachment", "collect");
            }
            if (route.TaskType == "vba")
            {
                return AgentText.ContainsAny(id, "vba", "macro", "context");
            }
            if (route.TaskType == "macro_execution")
            {
                return AgentText.ContainsAny(id, "vba", "macro", "context");
            }
            if (route.TaskType == "destructive")
            {
                return true;
            }
            return !tool.MutatesDocument || AgentText.ContainsAny(id, "read", "list", "search", "context", "summary");
        }

        private static bool OptionalToolAuthoring(ToolDefinition tool, RoutedTask route, bool enabled)
        {
            if (!enabled || tool == null || route == null ||
                !string.Equals(route.Phase, AgentPhases.Mutation, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return string.Equals(tool.Id, "common.tools_validate", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tool.Id, "common.tools_save", StringComparison.OrdinalIgnoreCase);
        }

        private static int ToolPriority(ToolDefinition tool, RoutedTask route)
        {
            if (tool == null || route == null)
            {
                return 50;
            }
            var id = tool.Id ?? string.Empty;
            if (route.TaskType == "html" && AgentText.ContainsAny(id, "html_workspace"))
            {
                return 0;
            }
            if (route.TaskType == "formatting" && AgentText.ContainsAny(id, "format", "autofit"))
            {
                return string.Equals(route.Phase, AgentPhases.Mutation, StringComparison.OrdinalIgnoreCase) ? 0 : 20;
            }
            if (route.TaskType == "chart" && AgentText.ContainsAny(id, "chart"))
            {
                return string.Equals(route.Phase, AgentPhases.Mutation, StringComparison.OrdinalIgnoreCase) ? 0 : 20;
            }
            if (AgentText.ContainsAny(id, "common.tools_validate", "common.tools_save"))
            {
                return 5;
            }
            if (route.TaskType == "content" && tool.MutatesDocument)
            {
                return AgentText.ContainsAny(id, "add_sheet", "add_slide", "write_table", "write_range", "set_formula", "add_chart", "add_table", "insert", "replace", "comment")
                    ? 0
                    : 20;
            }
            if (!tool.MutatesDocument && AgentText.ContainsAny(id, "context", "selection", "summary", "read", "profile", "list", "search"))
            {
                return 10;
            }
            return 30;
        }


    }
}
