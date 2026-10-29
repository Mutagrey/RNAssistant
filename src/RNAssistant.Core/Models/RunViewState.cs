using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.ModelProtocol;

namespace RNAssistant.Core.Models
{
    public static class RunViewLifecycles
    {
        public const string Running = "running";
        public const string Completed = "completed";
        public const string AwaitingConfirmation = "awaiting_confirmation";
        public const string AwaitingUser = "awaiting_user";
        public const string Cancelled = "cancelled";
        public const string Failed = "failed";

        public static bool IsKnown(string value)
        {
            return value == Running || value == Completed || value == AwaitingConfirmation ||
                value == AwaitingUser || value == Cancelled || value == Failed;
        }
    }

    public static class RunViewHealth
    {
        public const string Clean = "clean";
        public const string Errors = "errors";
        public const string Unknown = "unknown";

        public static bool IsKnown(string value)
        {
            return value == Clean || value == Errors || value == Unknown;
        }
    }

    public static class RunGoalVerification
    {
        // Execution read-back is evidence of an effect, not proof of the user's goal.
        public const string NotAssessed = "not_assessed";
    }

    public sealed class PendingConfirmationViewState
    {
        public string PendingId { get; private set; }
        public string ToolCallId { get; private set; }
        public string ToolName { get; private set; }
        public RNAssistant.Core.Tools.PatchBatchResult PatchPreview { get; private set; }

        [JsonConstructor]
        public PendingConfirmationViewState(string pendingId, string toolCallId, string toolName, RNAssistant.Core.Tools.PatchBatchResult patchPreview = null)
        {
            if (string.IsNullOrWhiteSpace(pendingId) || string.IsNullOrWhiteSpace(toolCallId) ||
                string.IsNullOrWhiteSpace(toolName))
                throw new ArgumentException("Complete pending confirmation view state is required.");
            PendingId = pendingId;
            ToolCallId = toolCallId;
            ToolName = toolName;
            PatchPreview = patchPreview;
        }
    }

    // A finish result is the model's stated assessment. Its evidence IDs link
    // to runtime outcomes; this DTO never upgrades GoalVerification.
    public sealed class ModelResultViewState
    {
        public string Summary { get; private set; }
        public string Assessment { get; private set; }
        public IReadOnlyList<string> Evidence { get; private set; }

        [JsonConstructor]
        public ModelResultViewState(string summary, string assessment, IEnumerable<string> evidence)
        {
            if (string.IsNullOrWhiteSpace(summary) ||
                assessment != "satisfied" && assessment != "partially_satisfied" &&
                assessment != "unverified" && assessment != "not_satisfied")
                throw new ArgumentException("A model result requires a summary and v8 assessment.");
            var refs = (evidence ?? throw new ArgumentNullException(nameof(evidence))).ToArray();
            if (refs.Any(string.IsNullOrWhiteSpace) || refs.Distinct(StringComparer.Ordinal).Count() != refs.Length)
                throw new ArgumentException("Model result evidence IDs must be nonblank and unique.");
            Summary = summary;
            Assessment = assessment;
            Evidence = Array.AsReadOnly(refs);
        }
    }

    // Immutable runtime-to-UI projection. Narrative is carried as data; lifecycle
    // and effect health are never inferred from its text or model-authored status.
    public sealed class RunViewState
    {
        public string RunId { get; private set; }
        public string TurnId { get; private set; }
        public string Narrative { get; private set; }
        public string Lifecycle { get; private set; }
        public string ExecutionHealth { get; private set; }
        public string GoalVerification { get; private set; }
        public string ModelOutcome { get; private set; }
        public IReadOnlyList<ModelResultViewState> ModelResults { get; private set; }
        public int SuccessfulReads { get; private set; }
        public int VerifiedWrites { get; private set; }
        public int NoChangeWrites { get; private set; }
        public int UnverifiedWrites { get; private set; }
        public int FailedCalls { get; private set; }
        public int UnknownEffects { get; private set; }
        public PendingConfirmationViewState PendingConfirmation { get; private set; }
        public string Reason { get; private set; }
        public string CurrentAction { get; private set; }
        public DateTime StartedUtc { get; private set; }

        [JsonConstructor]
        public RunViewState(string runId, string turnId, string narrative, string lifecycle,
            string executionHealth, int successfulReads, int verifiedWrites, int noChangeWrites,
            int unverifiedWrites, int failedCalls, int unknownEffects,
            PendingConfirmationViewState pendingConfirmation, string reason, string currentAction,
            DateTime startedUtc, string goalVerification = RunGoalVerification.NotAssessed,
            IEnumerable<ModelResultViewState> modelResults = null, string modelOutcome = null)
        {
            // Json.NET passes null for this field in durable run views written before
            // goal verification was added; optional parameter defaults do not apply.
            if (goalVerification == null) goalVerification = RunGoalVerification.NotAssessed;
            if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(turnId))
                throw new ArgumentException("Run and turn ids are required.");
            if (!RunViewLifecycles.IsKnown(lifecycle))
                throw new ArgumentOutOfRangeException(nameof(lifecycle));
            if (!RunViewHealth.IsKnown(executionHealth))
                throw new ArgumentOutOfRangeException(nameof(executionHealth));
            if (goalVerification != RunGoalVerification.NotAssessed)
                throw new ArgumentOutOfRangeException(nameof(goalVerification));
            if (successfulReads < 0 || verifiedWrites < 0 || noChangeWrites < 0 ||
                unverifiedWrites < 0 || failedCalls < 0 || unknownEffects < 0)
                throw new ArgumentOutOfRangeException("Run view counts cannot be negative.");
            var expectedHealth = unknownEffects > 0 ? RunViewHealth.Unknown
                : failedCalls > 0 ? RunViewHealth.Errors : RunViewHealth.Clean;
            if (executionHealth != expectedHealth || unverifiedWrites > unknownEffects)
                throw new ArgumentException("Run view health must match its effect evidence.");
            if ((lifecycle == RunViewLifecycles.AwaitingConfirmation) != (pendingConfirmation != null))
                throw new ArgumentException("Pending confirmation must match the run lifecycle.");
            var claims = (modelResults ?? new ModelResultViewState[0]).ToArray();
            if (claims.Any(claim => claim == null) ||
                claims.Length > 0 && lifecycle != RunViewLifecycles.Completed)
                throw new ArgumentException("Model finish results require a completed run.");
            if (modelOutcome != null)
            {
                ConversationOutcome outcome;
                if (lifecycle != RunViewLifecycles.Completed || reason != "model_finish" ||
                    !ConversationFinishContract.TryRead(modelOutcome, out outcome))
                    throw new ArgumentException("A model outcome requires an accepted finish.", nameof(modelOutcome));
                var assessments = claims.Select(claim => {
                    ConversationAssessment assessment;
                    ConversationResultClaim.TryReadAssessment(claim.Assessment, out assessment);
                    return assessment;
                });
                if (ConversationFinishContract.Contradiction(outcome, assessments) != null)
                    throw new ArgumentException("Model outcome contradicts result assessments.", nameof(modelOutcome));
            }

            RunId = runId;
            TurnId = turnId;
            Narrative = narrative ?? string.Empty;
            Lifecycle = lifecycle;
            ExecutionHealth = executionHealth;
            GoalVerification = goalVerification;
            ModelOutcome = modelOutcome;
            ModelResults = Array.AsReadOnly(claims);
            SuccessfulReads = successfulReads;
            VerifiedWrites = verifiedWrites;
            NoChangeWrites = noChangeWrites;
            UnverifiedWrites = unverifiedWrites;
            FailedCalls = failedCalls;
            UnknownEffects = unknownEffects;
            PendingConfirmation = pendingConfirmation;
            Reason = reason;
            CurrentAction = currentAction ?? string.Empty;
            StartedUtc = startedUtc;
        }
    }
}
