using System;
using Newtonsoft.Json;

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

    public sealed class PendingConfirmationViewState
    {
        public string PendingId { get; private set; }
        public string ToolCallId { get; private set; }
        public string ToolName { get; private set; }

        [JsonConstructor]
        public PendingConfirmationViewState(string pendingId, string toolCallId, string toolName)
        {
            if (string.IsNullOrWhiteSpace(pendingId) || string.IsNullOrWhiteSpace(toolCallId) ||
                string.IsNullOrWhiteSpace(toolName))
                throw new ArgumentException("Complete pending confirmation view state is required.");
            PendingId = pendingId;
            ToolCallId = toolCallId;
            ToolName = toolName;
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
            DateTime startedUtc)
        {
            if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(turnId))
                throw new ArgumentException("Run and turn ids are required.");
            if (!RunViewLifecycles.IsKnown(lifecycle))
                throw new ArgumentOutOfRangeException(nameof(lifecycle));
            if (!RunViewHealth.IsKnown(executionHealth))
                throw new ArgumentOutOfRangeException(nameof(executionHealth));
            if (successfulReads < 0 || verifiedWrites < 0 || noChangeWrites < 0 ||
                unverifiedWrites < 0 || failedCalls < 0 || unknownEffects < 0)
                throw new ArgumentOutOfRangeException("Run view counts cannot be negative.");
            var expectedHealth = unknownEffects > 0 ? RunViewHealth.Unknown
                : failedCalls > 0 ? RunViewHealth.Errors : RunViewHealth.Clean;
            if (executionHealth != expectedHealth || unverifiedWrites > unknownEffects)
                throw new ArgumentException("Run view health must match its effect evidence.");
            if ((lifecycle == RunViewLifecycles.AwaitingConfirmation) != (pendingConfirmation != null))
                throw new ArgumentException("Pending confirmation must match the run lifecycle.");

            RunId = runId;
            TurnId = turnId;
            Narrative = narrative ?? string.Empty;
            Lifecycle = lifecycle;
            ExecutionHealth = executionHealth;
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
