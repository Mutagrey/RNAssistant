using System;

namespace RNAssistant.Office.Tools
{
    internal enum CapabilityOutcomeStatus
    {
        Ok,
        Error
    }

    internal sealed class CapabilityToolOutcome
    {
        internal CapabilityOutcomeStatus Status { get; private set; }
        internal string Message { get; private set; }
        internal string DataJson { get; private set; }
        internal string ErrorCode { get; private set; }
        internal bool Retryable { get; private set; }
        internal System.Collections.Generic.IReadOnlyList<RNAssistant.Core.Models.ResourceEvidence> Evidence { get; private set; }

        internal static CapabilityToolOutcome Ok(
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

        internal static CapabilityToolOutcome Error(
            string message, string dataJson, string errorCode, bool retryable)
        {
            return new CapabilityToolOutcome
            {
                Status = CapabilityOutcomeStatus.Error,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                    ? "capability_read_failed" : errorCode,
                Retryable = retryable
            };
        }
    }
}
