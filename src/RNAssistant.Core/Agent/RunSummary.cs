using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Agent
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum RunLifecycle { Running, Completed, AwaitingConfirmation, Cancelled, Failed }
    [JsonConverter(typeof(StringEnumConverter))]
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
            : this(record.PendingId, record.Context.Call, record.Context.Policy, record.Context.StepId, record.ToolStepsConsumed)
        {
        }

        [JsonConstructor]
        internal PendingConfirmation(string pendingId, ToolCall call, ToolPolicySnapshot policy, string stepId, int chargedToolSteps)
        {
            if (string.IsNullOrWhiteSpace(pendingId) || string.IsNullOrWhiteSpace(stepId) || call == null || policy == null ||
                !string.Equals(call.Name, policy.ToolId, StringComparison.Ordinal) || chargedToolSteps < 1)
                throw new ArgumentException("Incomplete pending execution evidence.");
            PendingId = pendingId;
            Call = call;
            Policy = policy;
            StepId = stepId;
            ChargedToolSteps = chargedToolSteps;
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

        [JsonConstructor]
        internal RunSummary(string runId, string turnId, RunLifecycle lifecycle, ToolCounts toolCounts,
            int iterationsUsed, int toolStepsUsed, string assistantMessage, string reason, PendingConfirmation pendingConfirmation)
        {
            if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(turnId) || toolCounts == null ||
                !Enum.IsDefined(typeof(RunLifecycle), lifecycle) || iterationsUsed < 0 || toolStepsUsed < 0 ||
                (lifecycle == RunLifecycle.AwaitingConfirmation && pendingConfirmation == null))
                throw new ArgumentException("Incomplete runtime summary.");
            RunId = runId;
            TurnId = turnId;
            Lifecycle = lifecycle;
            ToolCounts = toolCounts;
            IterationsUsed = iterationsUsed;
            ToolStepsUsed = toolStepsUsed;
            AssistantMessage = assistantMessage ?? string.Empty;
            Reason = reason;
            PendingConfirmation = pendingConfirmation;
        }
    }
}
