using System;
using Newtonsoft.Json;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Persistence
{
    // A projection carried only by existing run.updated operations. No separate
    // checkpoint file, accepted-ID index or duplicate message history is stored.
    public sealed class AgentRunState
    {
        public RunSummary Summary { get; private set; }
        public AgentRunLimits Limits { get; private set; }
        public ToolExecutionContext InFlightTool { get; private set; }

        [JsonConstructor]
        public AgentRunState(RunSummary summary, AgentRunLimits limits, ToolExecutionContext inFlightTool = null)
        {
            Summary = summary ?? throw new ArgumentNullException(nameof(summary));
            Limits = limits ?? throw new ArgumentNullException(nameof(limits));
            if (inFlightTool != null && (!string.Equals(inFlightTool.RunId, summary.RunId, StringComparison.Ordinal) ||
                !string.Equals(inFlightTool.TurnId, summary.TurnId, StringComparison.Ordinal)))
                throw new ArgumentException("In-flight execution belongs to another run.");
            InFlightTool = inFlightTool;
        }

        public static AgentRunState Apply(AgentRunState previous, AgentRunEvent fact)
        {
            var limits = fact.Limits ?? (previous == null ? null : previous.Limits);
            var active = fact.Kind == AgentRunEventKind.ToolStarted ? fact.ToolContext
                : fact.Kind == AgentRunEventKind.ToolCompleted || fact.Kind == AgentRunEventKind.SummaryChanged
                    ? null : previous == null ? null : previous.InFlightTool;
            return new AgentRunState(fact.Summary, limits, active);
        }

        public AgentRunState Interrupt(bool cancelled, string message, string interruptedRunId = null)
        {
            var counts = Summary.ToolCounts;
            if (InFlightTool != null)
            {
                var completed = DateTime.UtcNow;
                if (completed < InFlightTool.StartedUtc) completed = InFlightTool.StartedUtc;
                counts = counts.Add(new ToolExecutionRecord(InFlightTool,
                    InFlightTool.Policy.MayHaveSideEffects ? ToolExecutionOutcome.Unknown : ToolExecutionOutcome.Error,
                    completed, message));
            }
            return new AgentRunState(new RunSummary(interruptedRunId ?? Summary.RunId, Summary.TurnId,
                cancelled ? RunLifecycle.Cancelled : RunLifecycle.Failed, counts, Summary.IterationsUsed,
                Summary.ToolStepsUsed, message, "runtime_interrupted", null), Limits);
        }
    }
}
