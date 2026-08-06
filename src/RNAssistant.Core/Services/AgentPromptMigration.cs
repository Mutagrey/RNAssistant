using System;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public static class AgentPromptMigration
    {
        private const string LegacyToolEnvelopeRule =
            "For json_schema or json_object, select an action with kind=tool and one tool object.";

        private const string CurrentToolEnvelopeRule =
            "For json_schema or json_object, emit kind=tool whose tool contains exactly toolId copied from AVAILABLE_TOOLS and arguments as a JSON object matching that tool schema, shaped as {\"toolId\":\"<copy exact id>\",\"arguments\":{}}. Never use id, name, args, action, actions, toolCalls, steps, or function_call.";

        private const string LegacySinglePlanRule =
            "Use plan only once when a complex task benefits from visible steps; plan never executes tools. Make plan steps concise, ordered, and observable: include expected inspection, mutation, and verification actions so the runtime can advance one visible step for each executed tool. Use stable short step ids. Use clarify only when required user input cannot be obtained through a read tool. Use final when the request is complete. Use cannot_complete when a required capability is unavailable. Select at most one external tool per model turn.";

        private const string CurrentPlanRule =
            "For a complex task, use kind=plan with a concise goal and ordered steps containing only id and title. A plan never executes tools. If observations materially change the remaining work, kind=plan may be returned again with the full revised remaining plan and stable ids; runtime preserves completed steps and replaces unfinished ones. Use clarify only when required input cannot be obtained through a read tool. Use final when complete and cannot_complete only when a required capability is unavailable. Select at most one external tool per model turn.";

        private const string LegacyPlanContinuationPrompt =
            "Continue the declared plan with the next single AgentDecision. Follow the visible steps in order, use one external tool per step, and do not repeat the plan.";

        private static readonly string[] LegacySystemPrompts =
        {
            "You are RNAssistant Office Action Planner. Follow the planner protocol exactly and never expose internal reasoning.",
            "You are RNAssistant Office Action Planner.",
            "You are an Office AI assistant. Always return the RNAssistant strict JSON planner envelope.",
            "You are an Office AI assistant. Use local tools only through rnassistant-agent JSON blocks when Office actions are required.",
            "You are an Office AI assistant. Use provided tools only through rnassistant-agent JSON blocks when document actions are required.",
            "You are an Office AI assistant. Use provided tools only through rnassistant-skill JSON blocks when document actions are required.",
            "You are an Office AI assistant. Use provided skills only through rnassistant-skill JSON blocks when document actions are required."
        };

        private static readonly string[] LegacyRepairPrompts =
        {
            "The previous response was not a valid AgentDecision v1 decision for the active transport. Return exactly one corrected decision and no surrounding text.",
            "Your previous AgentDecision or native tool selection was semantically invalid. Return one corrected decision matching the active transport and supplied response format."
        };

        private static readonly string[] LegacyForceToolPrompts =
        {
            "The current route requires a local Office tool before completion. Select exactly one available tool using the active transport, or return cannot_complete and name the missing capability.",
            "This task requires Office tool use before a final answer. Select one available read/context tool using the active transport, or return cannot_complete if no available tool can satisfy it.",
            "This task requires Office tool use before a final answer. Return kind=tool_plan with an available read/context tool, or kind=cannot_do if no available tool can satisfy it.",
            "You are in RNAssistant Agent mode. The user asked for an Office action, so a prose-only answer is not acceptable. Return only one ```rnassistant-agent fenced JSON block with executable steps. Copy toolId values exactly from Available tools. If a tool is missing, say that plainly instead of inventing one.",
            "You are in RNAssistant Agent mode. The user asked for an Office action, so a prose-only answer is not acceptable. Return only one ```rnassistant-agent fenced JSON block with executable steps. Copy toolId values exactly from the Available tools list. If a tool is missing, say that plainly instead of inventing one."
        };

        public static void Apply(AppSettings settings, AppSettings defaults)
        {
            if (settings == null)
            {
                return;
            }

            defaults = defaults ?? new AppSettings();
            var rawSystemPrompt = settings.SystemPrompt ?? string.Empty;
            var systemPrompt = rawSystemPrompt.Trim();
            if (Matches(systemPrompt, LegacySystemPrompts) || ContainsLegacyProtocol(systemPrompt))
            {
                settings.SystemPrompt = defaults.SystemPrompt;
            }
            else if (rawSystemPrompt.IndexOf(LegacyToolEnvelopeRule, StringComparison.Ordinal) >= 0)
            {
                settings.SystemPrompt = rawSystemPrompt.Replace(LegacyToolEnvelopeRule, CurrentToolEnvelopeRule);
            }
            if ((settings.SystemPrompt ?? string.Empty).IndexOf(LegacySinglePlanRule, StringComparison.Ordinal) >= 0)
            {
                settings.SystemPrompt = settings.SystemPrompt.Replace(LegacySinglePlanRule, CurrentPlanRule);
            }

            if (settings.AgentPrompts == null)
            {
                return;
            }

            var repairPrompt = (settings.AgentPrompts.RepairDecisionPrompt ?? string.Empty).Trim();
            if (Matches(repairPrompt, LegacyRepairPrompts) || ContainsLegacyProtocol(repairPrompt))
            {
                settings.AgentPrompts.RepairDecisionPrompt = defaults.AgentPrompts.RepairDecisionPrompt;
            }
            var forceToolPrompt = (settings.AgentPrompts.ForceToolUsePrompt ?? string.Empty).Trim();
            if (Matches(forceToolPrompt, LegacyForceToolPrompts) || ContainsLegacyProtocol(forceToolPrompt))
            {
                settings.AgentPrompts.ForceToolUsePrompt = defaults.AgentPrompts.ForceToolUsePrompt;
            }
            if (string.Equals(
                (settings.AgentPrompts.PlanContinuationPrompt ?? string.Empty).Trim(),
                LegacyPlanContinuationPrompt,
                StringComparison.Ordinal))
            {
                settings.AgentPrompts.PlanContinuationPrompt = defaults.AgentPrompts.PlanContinuationPrompt;
            }
        }

        private static bool ContainsLegacyProtocol(string value)
        {
            value = value ?? string.Empty;
            return value.IndexOf("rnassistant-agent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("rnassistant-skill", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("tool_plan", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("cannot_do", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool Matches(string value, string[] candidates)
        {
            foreach (var candidate in candidates ?? new string[0])
            {
                if (string.Equals(value, candidate, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
