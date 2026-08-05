using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal static class AgentText
    {
        public static bool ContainsAny(string value, params string[] terms)
        {
            foreach (var term in terms ?? new string[0])
            {
                if (!string.IsNullOrWhiteSpace(term) && value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        public static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values ?? new string[0])
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
            return string.Empty;
        }

        public static string Truncate(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            {
                return value ?? string.Empty;
            }
            return value.Substring(0, Math.Max(0, maxChars)) + "\n[truncated]";
        }
    }

    internal sealed class OfficeSnapshot
    {
        public string Host { get; set; }
        public string DocumentTitle { get; set; }
        public string ContainerName { get; set; }
        public string SelectionAddress { get; set; }
        public string SelectionText { get; set; }
        public string SnapshotText { get; set; }
    }

    internal sealed class RoutedTask
    {
        public string App { get; set; }
        public string Mode { get; set; }
        public string TaskType { get; set; }
        public string Phase { get; set; }
        public int RiskAllowed { get; set; }
        public bool RequiresTool { get; set; }
        public bool RequiresInspection { get; set; }
        public string DecisionReason { get; set; }
    }

    internal sealed class AgentObservation
    {
        public string Id { get; set; }
        public string ToolId { get; set; }
        public string Status { get; set; }
        public string Summary { get; set; }
        public string FactsJson { get; set; }
        public bool Mutation { get; set; }
        public bool LocalMutation { get; set; }
        public bool RequiresVerification { get; set; }
        public string Purpose { get; set; }
    }

    internal static class AgentObservationPurposes
    {
        public const string Inspection = "inspection";
        public const string Mutation = "mutation";
        public const string Verification = "verification";
    }

    internal sealed class ToolCatalogSlice
    {
        public List<ToolDefinition> Tools { get; set; }
        public List<ToolExclusion> Excluded { get; set; }

        public ToolCatalogSlice()
        {
            Tools = new List<ToolDefinition>();
            Excluded = new List<ToolExclusion>();
        }

        public ToolDefinition Find(string id)
        {
            return Tools.FirstOrDefault(t => t != null && string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    internal sealed class ToolExclusion
    {
        public string ToolId { get; set; }
        public string Reason { get; set; }
        public string Detail { get; set; }
    }

    internal sealed class PlannerValidationResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public ToolCommand Command { get; set; }
        public ToolDefinition Tool { get; set; }

        public static PlannerValidationResult Ok(ToolCommand command, ToolDefinition tool)
        {
            return new PlannerValidationResult { Success = true, Command = command, Tool = tool };
        }

        public static PlannerValidationResult Fail(string message)
        {
            return new PlannerValidationResult { Success = false, Message = message };
        }
    }

    internal sealed class AgentRunState
    {
        public int TotalToolSteps { get; set; }
        public bool FormatRepairUsed { get; set; }
        public bool ToolCorrectionUsed { get; set; }
        public bool PendingVerification { get; set; }
        public bool PlanDeclared { get; set; }
        public string ResponseMode { get; set; }
        public string WorkingGoal { get; set; }
        public List<AgentPlanStep> Plan { get; set; }

        public AgentRunState()
        {
            Plan = new List<AgentPlanStep>();
        }
    }
}
