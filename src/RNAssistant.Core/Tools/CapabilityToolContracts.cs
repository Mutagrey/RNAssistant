using System;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Tools
{
    public enum CapabilityOutcomeStatus
    {
        Ok,
        Error
    }

    public sealed class CapabilityToolOutcome
    {
        public CapabilityOutcomeStatus Status { get; private set; }
        public string Message { get; private set; }
        public string DataJson { get; private set; }
        public string ErrorCode { get; private set; }
        public bool Retryable { get; private set; }
        public ToolRecoveryContract Recovery { get; private set; }
        public System.Collections.Generic.IReadOnlyList<RNAssistant.Core.Models.ResourceEvidence> Evidence { get; private set; }

        public static CapabilityToolOutcome Ok(
            string message, string dataJson, System.Collections.Generic.IReadOnlyList<RNAssistant.Core.Models.ResourceEvidence> evidence = null)
        {
            return new CapabilityToolOutcome
            {
                Status = CapabilityOutcomeStatus.Ok,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                Evidence = evidence
            };
        }

        public static CapabilityToolOutcome Error(
            string message, string dataJson, string errorCode, bool retryable,
            ToolRecoveryContract recovery = null)
        {
            return new CapabilityToolOutcome
            {
                Status = CapabilityOutcomeStatus.Error,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                    ? "capability_read_failed" : errorCode,
                Retryable = retryable,
                Recovery = recovery ?? new ToolRecoveryContract(
                    ToolFailureKind.RejectedNoEffect,
                    ToolRetryPolicy.Replan)
            };
        }
    }
}
