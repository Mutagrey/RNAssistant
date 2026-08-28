using System;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Agent
{
    public enum RunLifecycle { Running, Completed, AwaitingConfirmation, Cancelled, Failed }
    public enum ExecutionHealth { Clean, Errors, Unknown }

    public sealed class ToolCounts
    {
        public int ReadOk { get; private set; }
        public int ReadError { get; private set; }
        public int WriteOk { get; private set; }
        public int WriteError { get; private set; }
        public int WriteUnknown { get; private set; }

        public ToolCounts(int readOk = 0, int readError = 0, int writeOk = 0, int writeError = 0, int writeUnknown = 0)
        {
            if (readOk < 0 || readError < 0 || writeOk < 0 || writeError < 0 || writeUnknown < 0)
                throw new ArgumentOutOfRangeException(nameof(readOk), "Counts cannot be negative.");
            ReadOk = readOk;
            ReadError = readError;
            WriteOk = writeOk;
            WriteError = writeError;
            WriteUnknown = writeUnknown;
        }

        internal ToolCounts Add(ToolExecutionRecord record)
        {
            if (record.Outcome == ToolExecutionOutcome.AwaitingConfirmation ||
                record.Outcome == ToolExecutionOutcome.NotDispatched) return this;
            var write = record.Context.Policy.MayHaveSideEffects;
            var ok = record.Outcome == ToolExecutionOutcome.Ok;
            var unknown = write && record.Outcome == ToolExecutionOutcome.Unknown;
            checked
            {
                return new ToolCounts(ReadOk + (!write && ok ? 1 : 0),
                    ReadError + (!write && !ok ? 1 : 0),
                    WriteOk + (write && ok ? 1 : 0),
                    WriteError + (write && !ok && !unknown ? 1 : 0),
                    WriteUnknown + (unknown ? 1 : 0));
            }
        }
    }

    public sealed class PendingConfirmation
    {
        public string PendingId { get; private set; }
        public ToolCall Call { get; private set; }
        public ToolPolicySnapshot Policy { get; private set; }
        public string StepId { get; private set; }
        public int ChargedToolSteps { get; private set; }

        internal PendingConfirmation(ToolExecutionRecord record)
        {
            PendingId = record.PendingId;
            Call = record.Context.Call;
            Policy = record.Context.Policy;
            StepId = record.Context.StepId;
            ChargedToolSteps = record.ToolStepsConsumed;
        }
    }

    // Runtime authority: narrative cannot set health. DTOs are immutable; storage
    // adapters must append these facts, never introduce a mutable snapshot store.
    public sealed class RunSummary
    {
        public string RunId { get; private set; }
        public string TurnId { get; private set; }
        public RunLifecycle Lifecycle { get; private set; }
        public ExecutionHealth ExecutionHealth
        {
            get
            {
                return ToolCounts.WriteUnknown > 0 ? ExecutionHealth.Unknown
                    : ToolCounts.ReadError > 0 || ToolCounts.WriteError > 0 ? ExecutionHealth.Errors
                    : ExecutionHealth.Clean;
            }
        }
        public ToolCounts ToolCounts { get; private set; }
        public int IterationsUsed { get; private set; }
        public int ToolStepsUsed { get; private set; }
        public string AssistantMessage { get; private set; }
        public string Reason { get; private set; }
        public PendingConfirmation PendingConfirmation { get; private set; }

        internal RunSummary(string runId, string turnId, RunLifecycle lifecycle, ToolCounts counts,
            int iterations, int toolSteps, string message, string reason, PendingConfirmation pending)
        {
            RunId = runId;
            TurnId = turnId;
            Lifecycle = lifecycle;
            ToolCounts = counts;
            IterationsUsed = iterations;
            ToolStepsUsed = toolSteps;
            AssistantMessage = message ?? string.Empty;
            Reason = reason;
            PendingConfirmation = pending;
        }
    }
}
