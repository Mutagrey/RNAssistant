using System;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Agent;

namespace RNAssistant.Core.Tools
{
    // Legacy callers retain captured safety; registered handlers also capture the
    // complete typed policy. A typed contract cannot match an untyped snapshot.
    public sealed class ToolPolicySnapshot
    {
        public string ToolId { get; private set; }
        public string Revision { get; private set; }
        public bool MayHaveSideEffects { get; private set; }
        public bool RequiresConfirmation { get; private set; }
        public bool IndependentLocalRead { get; private set; }
        public ToolPolicy Policy { get; private set; }

        [JsonConstructor]
        public ToolPolicySnapshot(string toolId, string revision, bool mayHaveSideEffects,
            bool requiresConfirmation = false, bool independentLocalRead = false, ToolPolicy policy = null)
        {
            if (string.IsNullOrWhiteSpace(toolId)) throw new ArgumentException("Tool id is required.", nameof(toolId));
            if (string.IsNullOrWhiteSpace(revision)) throw new ArgumentException("Policy revision is required.", nameof(revision));
            if (independentLocalRead && (mayHaveSideEffects || requiresConfirmation))
                throw new ArgumentException("An independent local read cannot have effects or require confirmation.");
            if (policy != null && (policy.MayHaveSideEffects != mayHaveSideEffects ||
                policy.RequiresConfirmation != requiresConfirmation || policy.IndependentLocalRead != independentLocalRead))
                throw new ArgumentException("Typed and captured policy metadata disagree.", nameof(policy));
            ToolId = toolId;
            Revision = revision;
            MayHaveSideEffects = mayHaveSideEffects;
            RequiresConfirmation = requiresConfirmation;
            IndependentLocalRead = independentLocalRead;
            Policy = policy;
        }

        public ToolPolicySnapshot(string toolId, string revision, ToolPolicy policy)
            : this(toolId, revision, (policy ?? throw new ArgumentNullException(nameof(policy))).MayHaveSideEffects,
                policy.RequiresConfirmation, policy.IndependentLocalRead, policy)
        {
        }

        public bool Matches(ToolPolicySnapshot other)
        {
            return other != null && string.Equals(ToolId, other.ToolId, StringComparison.Ordinal) &&
                string.Equals(Revision, other.Revision, StringComparison.Ordinal) &&
                MayHaveSideEffects == other.MayHaveSideEffects &&
                RequiresConfirmation == other.RequiresConfirmation &&
                IndependentLocalRead == other.IndependentLocalRead &&
                (Policy == null ? other.Policy == null : Policy.Matches(other.Policy));
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
        public string PreparedStateJson { get; private set; }
        public string ExpectedContentSha256 { get; private set; }

        [JsonConstructor]
        public ToolExecutionContext(ToolCall call, ToolPolicySnapshot policy, string runId, string turnId,
            string stepId, DateTime startedUtc, bool isConfirmed, int remainingToolSteps,
            string preparedStateJson = null, string expectedContentSha256 = null)
        {
            if (call == null || policy == null || !string.Equals(call.Name, policy.ToolId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(turnId) || string.IsNullOrWhiteSpace(stepId) || remainingToolSteps < 0)
                throw new ArgumentException("Incomplete tool execution context.");
            if (preparedStateJson != null &&
                preparedStateJson.Length > ToolPreparationResult.MaxPreparedStateChars)
                throw new ArgumentException("Prepared state exceeds the runtime bound.", nameof(preparedStateJson));
            Call = call;
            Policy = policy;
            RunId = runId;
            TurnId = turnId;
            StepId = stepId;
            StartedUtc = startedUtc;
            IsConfirmed = isConfirmed;
            RemainingToolSteps = remainingToolSteps;
            PreparedStateJson = preparedStateJson;
            ExpectedContentSha256 = expectedContentSha256;
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
        public string PreparedStateJson { get; private set; }
        public string ConfirmationDataJson { get; private set; }
        public bool AwaitingUser { get; private set; }
        public int ToolStepsConsumed { get; private set; }
        public ToolExecutionEvidence Evidence { get; private set; }
        public System.Collections.Generic.IReadOnlyList<RNAssistant.Core.Models.ResourceEvidence> ResourceEvidence { get; private set; }
        public RNAssistant.Core.Models.ResourceEffect ResourceEffect { get; private set; }
        public System.Collections.Generic.IReadOnlyList<RNAssistant.Core.Models.ResourceMutationReadBack> ResourceReadBack { get; private set; }
        public string AuthorityCommitId { get; private set; }
        [JsonIgnore]
        public Contracts.ToolResult Result { get; private set; }

        public ToolExecutionRecord(ToolExecutionContext context, ToolExecutionOutcome outcome, DateTime completedUtc,
            string message = null, string modelResultJson = null, bool mayHaveDispatched = true,
            string pendingId = null, bool awaitingUser = false, int toolStepsConsumed = 1, string documentRuntimeId = null,
            ToolExecutionEvidence evidence = null, Contracts.ToolResult result = null,
            string preparedStateJson = null, string confirmationDataJson = null,
            System.Collections.Generic.IReadOnlyList<RNAssistant.Core.Models.ResourceEvidence> resourceEvidence = null,
            RNAssistant.Core.Models.ResourceEffect resourceEffect = null, string authorityCommitId = null,
            System.Collections.Generic.IReadOnlyList<RNAssistant.Core.Models.ResourceMutationReadBack> resourceReadBack = null)
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
            if (outcome != ToolExecutionOutcome.AwaitingConfirmation &&
                (preparedStateJson != null || confirmationDataJson != null))
                throw new ArgumentException("Only a pending call can carry confirmation preparation.");
            if (preparedStateJson != null && preparedStateJson.Length > ToolPreparationResult.MaxPreparedStateChars)
                throw new ArgumentException("Prepared state exceeds the runtime bound.", nameof(preparedStateJson));
            if (confirmationDataJson != null &&
                confirmationDataJson.Length > ToolPreparationResult.MaxConfirmationDataChars)
                throw new ArgumentException("Confirmation data exceeds the runtime bound.", nameof(confirmationDataJson));
            if (outcome == ToolExecutionOutcome.NotDispatched && mayHaveDispatched)
                throw new ArgumentException("A non-dispatched call cannot have been dispatched.", nameof(mayHaveDispatched));
            if (awaitingUser && outcome != ToolExecutionOutcome.Ok)
                throw new ArgumentException("Only a successful local interaction can await user input.", nameof(awaitingUser));
            var dispatch = mayHaveDispatched ? ToolDispatchEvidence.MayHaveDispatched : ToolDispatchEvidence.NotDispatched;
            if (evidence != null && evidence.Dispatch != dispatch)
                throw new ArgumentException("Dispatch evidence disagrees with the execution record.", nameof(evidence));
            Outcome = outcome;
            CompletedUtc = completedUtc;
            Message = message ?? string.Empty;
            ModelResultJson = modelResultJson;
            MayHaveDispatched = mayHaveDispatched;
            PendingId = pendingId;
            PreparedStateJson = preparedStateJson;
            ConfirmationDataJson = confirmationDataJson;
            AwaitingUser = awaitingUser;
            ToolStepsConsumed = outcome == ToolExecutionOutcome.NotDispatched ? 0 : Math.Max(1, toolStepsConsumed);
            DocumentRuntimeId = documentRuntimeId;
            Evidence = evidence ?? new ToolExecutionEvidence(dispatch, ToolEffectEvidence.Unreported);
            Result = result;
            ResourceEvidence = resourceEvidence ?? new RNAssistant.Core.Models.ResourceEvidence[0];
            ResourceEffect = resourceEffect;
            AuthorityCommitId = authorityCommitId;
            ResourceReadBack = resourceReadBack ?? new RNAssistant.Core.Models.ResourceMutationReadBack[0];
        }

        public ToolExecutionRecord WithAuthorityCommit(RNAssistant.Core.Models.ResourceAuthorityCommit commit)
        {
            if (commit == null) return this;
            var committedResult = Result == null ? null : new RNAssistant.Core.Tools.Contracts.ToolResult(
                Result.Status, Result.Message, Result.DataJson, Result.Resources.Concat(commit.HeadChanges
                    .Where(change => change.After.Knowledge == RNAssistant.Core.Models.HeadKnowledge.Known)
                    .Select(change => change.After.Revision)).GroupBy(reference => reference.Uri + "@" + reference.Revision)
                    .Select(group => group.First()));
            return new ToolExecutionRecord(Context, Outcome, CompletedUtc, Message, ModelResultJson,
                MayHaveDispatched, PendingId, AwaitingUser, ToolStepsConsumed, DocumentRuntimeId, Evidence,
                committedResult, PreparedStateJson, ConfirmationDataJson, ResourceEvidence, commit.Effect, commit.CommitId, ResourceReadBack);
        }
    }
}
