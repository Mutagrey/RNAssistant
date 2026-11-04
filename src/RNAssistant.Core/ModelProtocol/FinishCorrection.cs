using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.ModelProtocol
{
    // Bounded diagnostic state in the existing run projection. Never accepted
    // history, execution authority, or a second copy of the full response body.
    public sealed class FinishCorrection
    {
        public const int MaximumRejections = 3;
        public const int MaximumResponseBytes = 4096;
        public const int MaximumMessageBytes = 2048;
        public const int MaximumClaimBytes = 2048;
        public string Code { get; private set; }
        public string Message { get; private set; }
        public bool MessageTruncated { get; private set; }
        public int? ClaimIndex { get; private set; }
        public string RejectedClaim { get; private set; }
        public bool ClaimTruncated { get; private set; }
        public string RejectedResponse { get; private set; }
        public bool ResponseTruncated { get; private set; }
        public string SourceModelAttemptId { get; private set; }
        public ModelRequestCauseKind Kind { get; private set; }
        public string Fingerprint { get; private set; }
        public int Rejections { get; private set; }

        [JsonConstructor]
        public FinishCorrection(string code, string message, bool messageTruncated, int? claimIndex,
            string rejectedClaim, bool claimTruncated, string rejectedResponse, bool responseTruncated,
            string sourceModelAttemptId, ModelRequestCauseKind kind, string fingerprint, int rejections)
        {
            if (string.IsNullOrWhiteSpace(code) || code.Length > 128 || string.IsNullOrWhiteSpace(message) ||
                string.IsNullOrWhiteSpace(rejectedResponse) || claimIndex < 0 ||
                kind != ModelRequestCauseKind.CompletionRejected && kind != ModelRequestCauseKind.PartialFinishRecheck ||
                string.IsNullOrWhiteSpace(fingerprint) || fingerprint.Length != 64 ||
                rejections < 1 || rejections > MaximumRejections)
                throw new ArgumentException("Invalid finish correction state.");
            RequireBounded(message, MaximumMessageBytes);
            RequireBounded(rejectedResponse, MaximumResponseBytes);
            if (rejectedClaim != null) RequireBounded(rejectedClaim, MaximumClaimBytes);
            Code = code; Message = message; MessageTruncated = messageTruncated;
            ClaimIndex = claimIndex; RejectedClaim = rejectedClaim; ClaimTruncated = claimTruncated;
            RejectedResponse = rejectedResponse; ResponseTruncated = responseTruncated;
            SourceModelAttemptId = sourceModelAttemptId; Kind = kind;
            Fingerprint = fingerprint; Rejections = rejections;
        }

        internal static FinishCorrection Create(AgentResponseDraft proposal, string code, string message,
            int? claimIndex, string sourceModelAttemptId, ModelRequestCauseKind kind, FinishCorrection previous)
        {
            if (proposal == null || proposal.Action != ConversationResponse.FinishAction ||
                claimIndex.HasValue && (claimIndex < 0 || claimIndex >= proposal.Results.Count))
                throw new ArgumentException("A validated rejected finish and matching claim are required.");
            // Narration and the new attempt ID cannot reset an unchanged blocker.
            var fingerprint = ToolPackSnapshot.JsonFingerprint(new JObject {
                ["code"] = code, ["message"] = message, ["claim_index"] = claimIndex
            }.ToString(Formatting.None));
            var count = kind == ModelRequestCauseKind.CompletionRejected && previous?.Kind == kind &&
                previous.Fingerprint == fingerprint ? previous.Rejections + 1 : 1;
            var response = ModelProtocolWire.WriteAcceptedDecision(proposal.Action, proposal.Reason,
                proposal.Message, results: proposal.Results, outcome: proposal.Outcome);
            bool responseTruncated, messageTruncated, claimTruncated = false;
            var quote = ModelProtocolWire.BoundDiagnosticText(response, MaximumResponseBytes, out responseTruncated);
            var reason = ModelProtocolWire.BoundDiagnosticText(message, MaximumMessageBytes, out messageTruncated);
            var claim = claimIndex.HasValue ? ModelProtocolWire.BoundDiagnosticText(
                JObject.Parse(response)["results"][claimIndex.Value].ToString(Formatting.None), MaximumClaimBytes, out claimTruncated) : null;
            return new FinishCorrection(code, reason, messageTruncated, claimIndex, claim, claimTruncated,
                quote, responseTruncated, sourceModelAttemptId, kind, fingerprint, count);
        }

        internal ModelRequestCause RequestCause()
        { return new ModelRequestCause(Kind, SourceModelAttemptId, Code + ": " + Message); }

        private static void RequireBounded(string text, int maximum)
        {
            bool truncated;
            ModelProtocolWire.BoundDiagnosticText(text, maximum, out truncated);
            if (truncated) throw new ArgumentException("Finish correction exceeds its diagnostic bound.");
        }
    }
}
