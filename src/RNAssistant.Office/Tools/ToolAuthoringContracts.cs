using System;

namespace RNAssistant.Office.Tools
{
    internal enum ToolAuthoringOutcomeStatus
    {
        Ok,
        Error,
        Unknown
    }

    internal enum ToolAuthoringEffect
    {
        None,
        VerifiedNoChange,
        VerifiedChange,
        Unknown
    }

    internal sealed class ToolAuthoringOutcome
    {
        internal ToolAuthoringOutcomeStatus Status { get; private set; }
        internal ToolAuthoringEffect Effect { get; private set; }
        internal string Message { get; private set; }
        internal string DataJson { get; private set; }
        internal string ErrorCode { get; private set; }
        internal bool Retryable { get; private set; }
        internal bool Success { get { return Status == ToolAuthoringOutcomeStatus.Ok; } }

        internal static ToolAuthoringOutcome Ok(
            string message, string dataJson = null,
            ToolAuthoringEffect effect = ToolAuthoringEffect.None)
        {
            return new ToolAuthoringOutcome
            {
                Status = ToolAuthoringOutcomeStatus.Ok,
                Effect = effect,
                Message = message ?? string.Empty,
                DataJson = dataJson
            };
        }

        internal static ToolAuthoringOutcome Error(
            string message, string dataJson = null,
            string errorCode = null, bool retryable = false)
        {
            return new ToolAuthoringOutcome
            {
                Status = ToolAuthoringOutcomeStatus.Error,
                Effect = ToolAuthoringEffect.None,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                    ? "tool_authoring_failed" : errorCode,
                Retryable = retryable
            };
        }

        internal static ToolAuthoringOutcome Unknown(
            string message, string dataJson = null,
            string errorCode = null)
        {
            return new ToolAuthoringOutcome
            {
                Status = ToolAuthoringOutcomeStatus.Unknown,
                Effect = ToolAuthoringEffect.Unknown,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                    ? "tool_authoring_effect_unknown" : errorCode,
                Retryable = false
            };
        }
    }

    internal sealed class ToolAuthoringPreparation
    {
        internal ToolAuthoringOutcome Outcome { get; private set; }
        internal string PreparedStateJson { get; private set; }

        internal ToolAuthoringPreparation(
            ToolAuthoringOutcome outcome, string preparedStateJson = null)
        {
            Outcome = outcome ?? throw new ArgumentNullException(nameof(outcome));
            PreparedStateJson = preparedStateJson;
        }
    }
}
