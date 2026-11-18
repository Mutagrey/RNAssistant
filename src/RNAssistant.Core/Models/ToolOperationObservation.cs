using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Models
{
    // Runtime-owned correlation for work that can outlive the caller's wait.
    // Never accepted from model arguments, result JSON or an arbitrary source body.
    public sealed class ToolOperationReference
    {
        public string SessionId { get; private set; }
        public string Generation { get; private set; }
        public string RunId { get; private set; }
        public string CallId { get; private set; }
        public string CallSignature { get; private set; }
        public ResourceIdentity StatusResource { get; private set; }
        public string StatusView { get; private set; }

        [JsonConstructor]
        public ToolOperationReference(string sessionId, string generation, string runId,
            string callId, string callSignature, ResourceIdentity statusResource, string statusView)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(generation) ||
                string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(callId) ||
                string.IsNullOrWhiteSpace(callSignature) || statusResource == null || string.IsNullOrWhiteSpace(statusView))
                throw new ArgumentException("An operation requires exact session, generation, call and status scope.");
            SessionId = sessionId; Generation = generation; RunId = runId; CallId = callId;
            CallSignature = callSignature; StatusResource = statusResource; StatusView = statusView;
        }

        public bool Matches(ToolOperationReference other)
        {
            return other != null && SessionId == other.SessionId && Generation == other.Generation &&
                RunId == other.RunId && CallId == other.CallId && CallSignature == other.CallSignature &&
                StatusResource.Equals(other.StatusResource) && StatusView == other.StatusView;
        }

        public static ToolOperationReference Capture(ToolExecutionContext context, string sessionId,
            string generation, ResourceIdentity statusResource, string statusView)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            return new ToolOperationReference(sessionId, generation, context.RunId, context.Call.Id,
                RNAssistant.Core.Agent.AgentProgressTracker.Signature(context.Call), statusResource, statusView);
        }
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum ToolOperationState
    {
        Pending, Running, Succeeded, Failed, Cancelled, Unresolved,
        // Native work is positively known to have ended, while its business
        // result remains unverified. This is not success or a no-effect claim.
        StoppedUnverified
    }

    public sealed class ToolOperationObservation
    {
        public ToolOperationReference Operation { get; private set; }
        public ToolOperationState State { get; private set; }
        public DateTime NativeObservedAtUtc { get; private set; }
        public bool ExecutionStopped { get; private set; }
        public bool SafeToInvokeAgain { get; private set; }

        [JsonConstructor]
        public ToolOperationObservation(ToolOperationReference operation, ToolOperationState state,
            DateTime nativeObservedAtUtc, bool executionStopped, bool safeToInvokeAgain)
        {
            if (operation == null || !Enum.IsDefined(typeof(ToolOperationState), state))
                throw new ArgumentException("An exact operation and supported state are required.");
            var terminal = state == ToolOperationState.Succeeded || state == ToolOperationState.Failed ||
                state == ToolOperationState.Cancelled || state == ToolOperationState.StoppedUnverified;
            if (executionStopped && !terminal || safeToInvokeAgain && !executionStopped ||
                state == ToolOperationState.StoppedUnverified && !executionStopped)
                throw new ArgumentException("Only confirmed terminal work can release a replay guard.");
            Operation = operation; State = state; NativeObservedAtUtc = nativeObservedAtUtc.ToUniversalTime();
            ExecutionStopped = executionStopped; SafeToInvokeAgain = safeToInvokeAgain;
        }

        public bool Resolves(ToolExecutionProgress pending, ResourceEvidence evidence)
        {
            return pending != null && pending.Outcome == ToolExecutionOutcome.Unknown &&
                pending.Operation != null && Operation.Matches(pending.Operation) &&
                ExecutionStopped && SafeToInvokeAgain && evidence != null && !evidence.Immutable &&
                evidence.Complete && evidence.Coverage.Kind == ResourceCoverageKinds.Whole &&
                evidence.Resource.Identity.Equals(Operation.StatusResource) && evidence.View == Operation.StatusView &&
                pending.CompletedUtc.HasValue && evidence.ObservedAt > pending.CompletedUtc.Value &&
                evidence.ObservedAt >= NativeObservedAtUtc;
        }
    }
}
