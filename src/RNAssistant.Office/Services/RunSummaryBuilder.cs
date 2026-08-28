using System;
using System.Collections.Generic;
using RNAssistant.Core.Models;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    // Phase 1C adapter from legacy ToolResult to runtime evidence. ToolRuntime replaces
    // this mapping in Phase 4; neither model text nor model status is an input.
    public sealed class RunSummaryBuilder
    {
        private enum Outcome { ReadOk, WriteOk, ReadError, WriteError, WriteUnknown }

        private IDictionary<string, ToolSafetyProfile> _policies;
        // ToolCommand has reference identity: repeated model ids are separate invocations.
        // Re-observing the same invocation cannot double count or erase uncertainty.
        private readonly Dictionary<ToolCommand, Outcome> _outcomes = new Dictionary<ToolCommand, Outcome>();
        private readonly RunExecutionSummary _previous;

        public RunSummaryBuilder(IEnumerable<ToolDefinition> catalog, RunExecutionSummary previous = null)
        {
            _policies = ToolSafetyPolicy.ResolveAll(catalog);
            _previous = previous == null ? new RunExecutionSummary() : previous.Clone();
        }

        public static RunExecutionSummary ContinuationSeed(ChatSession session)
        {
            // Old pending runs have no evidence summary. Never invent a clean history.
            return session != null && session.LastRun != null && session.LastRun.ExecutionSummary != null
                ? session.LastRun.ExecutionSummary.Clone()
                : new RunExecutionSummary { ExecutionHealth = "unknown" };
        }

        internal void UseCatalog(IEnumerable<ToolDefinition> catalog)
        {
            _policies = ToolSafetyPolicy.ResolveAll(catalog);
        }

        public void Observe(ToolCommand command, ToolResult result)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (result != null && string.Equals(result.Status, "waiting_confirmation", StringComparison.OrdinalIgnoreCase)) return;

            ToolSafetyProfile policy;
            var knownPolicy = _policies.TryGetValue(command.ToolId ?? string.Empty, out policy) && policy.Valid;
            var write = !knownPolicy || policy.MutatesDocument || policy.MutatesLocalState;
            var uncertain = result == null ||
                EqualsCode(result.Status, "unknown") || EqualsCode(result.Status, "interrupted_unknown") ||
                EqualsCode(result.Status, "partial_failure") || EqualsCode(result.ErrorCode, "tool_effect_uncertain") ||
                EqualsCode(result.ErrorCode, "missing_result");
            var outcome = !knownPolicy || (write && uncertain) ? Outcome.WriteUnknown
                : result != null && result.Success ? (write ? Outcome.WriteOk : Outcome.ReadOk)
                : write ? Outcome.WriteError : Outcome.ReadError;
            Outcome previous;
            if (!_outcomes.TryGetValue(command, out previous) || outcome > previous)
                _outcomes[command] = outcome;
        }

        public RunExecutionSummary Snapshot()
        {
            var summary = _previous.Clone();
            foreach (var outcome in _outcomes.Values)
            {
                switch (outcome)
                {
                    case Outcome.ReadOk: summary.ReadOk++; break;
                    case Outcome.ReadError: summary.ReadError++; break;
                    case Outcome.WriteOk: summary.WriteOk++; break;
                    case Outcome.WriteError: summary.WriteError++; break;
                    case Outcome.WriteUnknown: summary.WriteUnknown++; break;
                }
            }
            summary.ExecutionHealth = summary.WriteUnknown > 0 || EqualsCode(_previous.ExecutionHealth, "unknown") ? "unknown"
                : summary.ReadError + summary.WriteError > 0 || EqualsCode(_previous.ExecutionHealth, "errors") ? "errors"
                : "clean";
            return summary;
        }

        public RunExecutionSummary Publish(ChatSession session, ChatMessage message = null)
        {
            var summary = Snapshot();
            if (session != null && session.LastRun != null) session.LastRun.ExecutionSummary = summary.Clone();
            if (message != null) message.ExecutionSummary = summary.Clone();
            return summary;
        }

        private static bool EqualsCode(string value, string code)
        {
            return string.Equals(value, code, StringComparison.OrdinalIgnoreCase);
        }
    }
}
