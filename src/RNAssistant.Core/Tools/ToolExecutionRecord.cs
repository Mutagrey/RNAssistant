using System;
using RNAssistant.Core.Agent;

namespace RNAssistant.Core.Tools
{
    // A captured execution policy, not the descriptor/policy/binding migration of Phase 4.
    public sealed class ToolPolicySnapshot
    {
        public string ToolId { get; private set; }
        public string Revision { get; private set; }
        public bool MayHaveSideEffects { get; private set; }
        public bool RequiresConfirmation { get; private set; }
        public bool IndependentLocalRead { get; private set; }

        public ToolPolicySnapshot(string toolId, string revision, bool mayHaveSideEffects,
            bool requiresConfirmation = false, bool independentLocalRead = false)
        {
            if (string.IsNullOrWhiteSpace(toolId)) throw new ArgumentException("Tool id is required.", nameof(toolId));
            if (string.IsNullOrWhiteSpace(revision)) throw new ArgumentException("Policy revision is required.", nameof(revision));
            if (independentLocalRead && (mayHaveSideEffects || requiresConfirmation))
                throw new ArgumentException("An independent local read cannot have effects or require confirmation.");
            ToolId = toolId;
            Revision = revision;
            MayHaveSideEffects = mayHaveSideEffects;
            RequiresConfirmation = requiresConfirmation;
            IndependentLocalRead = independentLocalRead;
        }

        public bool Matches(ToolPolicySnapshot other)
        {
            return other != null && string.Equals(ToolId, other.ToolId, StringComparison.Ordinal) &&
                string.Equals(Revision, other.Revision, StringComparison.Ordinal) &&
                MayHaveSideEffects == other.MayHaveSideEffects &&
                RequiresConfirmation == other.RequiresConfirmation &&
                IndependentLocalRead == other.IndependentLocalRead;
        }
    }

    public enum ToolExecutionOutcome { Ok, Error, Unknown, AwaitingConfirmation, NotDispatched }

    public sealed class ToolExecutionContext
    {
        public ToolCall Call { get; private set; }
        public ToolPolicySnapshot Policy { get; private set; }
        public string RunId { get; private set; }
        public string TurnId { get; private set; }
        public string StepId { get; private set; }
        public DateTime StartedUtc { get; private set; }
        public bool IsConfirmed { get; private set; }
        public int RemainingToolSteps { get; private set; }

        internal ToolExecutionContext(ToolCall call, ToolPolicySnapshot policy, string runId, string turnId,
            string stepId, DateTime startedUtc, bool confirmed, int remaining)
        {
            Call = call;
            Policy = policy;
            RunId = runId;
            TurnId = turnId;
            StepId = stepId;
            StartedUtc = startedUtc;
            IsConfirmed = confirmed;
            RemainingToolSteps = remaining;
        }
    }

    // Runtime-only evidence. ModelResultJson is already prepared by the external
    // adapter and stays opaque here; it is not a new model-facing Tool Result format.
    public sealed class ToolExecutionRecord
    {
        public ToolExecutionContext Context { get; private set; }
        public ToolExecutionOutcome Outcome { get; private set; }
        public DateTime CompletedUtc { get; private set; }
        public string DocumentRuntimeId { get; private set; }
        public string Message { get; private set; }
        public string ModelResultJson { get; private set; }
        // True includes an ambiguous runtime entry; it does not assert that a
        // domain mutation actually happened. False certifies no dispatch.
        public bool MayHaveDispatched { get; private set; }
        public string PendingId { get; private set; }
        public bool AwaitingUser { get; private set; }
        public int ToolStepsConsumed { get; private set; }

        public ToolExecutionRecord(ToolExecutionContext context, ToolExecutionOutcome outcome, DateTime completedUtc,
            string message = null, string modelResultJson = null, bool mayHaveDispatched = true,
            string pendingId = null, bool awaitingUser = false, int toolStepsConsumed = 1, string documentRuntimeId = null)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
            if (!Enum.IsDefined(typeof(ToolExecutionOutcome), outcome)) throw new ArgumentOutOfRangeException(nameof(outcome));
            if (completedUtc < context.StartedUtc) throw new ArgumentException("Completion precedes execution start.", nameof(completedUtc));
            if (toolStepsConsumed < 0) throw new ArgumentOutOfRangeException(nameof(toolStepsConsumed));
            if (outcome == ToolExecutionOutcome.AwaitingConfirmation &&
                (mayHaveDispatched || context.IsConfirmed || string.IsNullOrWhiteSpace(pendingId)))
                throw new ArgumentException("Pending confirmation requires an unexecuted call and a pending id.");
            if (outcome != ToolExecutionOutcome.AwaitingConfirmation && pendingId != null)
                throw new ArgumentException("Only a pending call has a pending id.", nameof(pendingId));
            if (outcome == ToolExecutionOutcome.NotDispatched && mayHaveDispatched)
                throw new ArgumentException("A non-dispatched call cannot have been dispatched.", nameof(mayHaveDispatched));
            if (awaitingUser && outcome != ToolExecutionOutcome.Ok)
                throw new ArgumentException("Only a successful local interaction can await user input.", nameof(awaitingUser));
            Outcome = outcome;
            CompletedUtc = completedUtc;
            Message = message ?? string.Empty;
            ModelResultJson = modelResultJson;
            MayHaveDispatched = mayHaveDispatched;
            PendingId = pendingId;
            AwaitingUser = awaitingUser;
            ToolStepsConsumed = outcome == ToolExecutionOutcome.NotDispatched ? 0 : Math.Max(1, toolStepsConsumed);
            DocumentRuntimeId = documentRuntimeId;
        }
    }
}
