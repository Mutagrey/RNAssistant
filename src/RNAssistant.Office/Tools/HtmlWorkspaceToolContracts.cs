using System;

namespace RNAssistant.Office.Tools
{
    internal enum HtmlWorkspaceOutcomeStatus
    {
        Ok,
        Error,
        Unknown
    }

    internal enum HtmlWorkspaceEffect
    {
        None,
        VerifiedNoChange,
        VerifiedChange,
        Unknown
    }

    internal sealed class HtmlWorkspaceToolOutcome
    {
        internal HtmlWorkspaceOutcomeStatus Status { get; private set; }
        internal HtmlWorkspaceEffect Effect { get; private set; }
        internal string Message { get; private set; }
        internal string DataJson { get; private set; }
        internal string ErrorCode { get; private set; }
        internal bool Retryable { get; private set; }

        internal static HtmlWorkspaceToolOutcome Ok(
            string message, string dataJson, HtmlWorkspaceEffect effect)
        {
            return new HtmlWorkspaceToolOutcome
            {
                Status = HtmlWorkspaceOutcomeStatus.Ok,
                Effect = effect,
                Message = message ?? string.Empty,
                DataJson = dataJson
            };
        }

        internal static HtmlWorkspaceToolOutcome Error(
            string message, string dataJson, string errorCode, bool retryable)
        {
            return new HtmlWorkspaceToolOutcome
            {
                Status = HtmlWorkspaceOutcomeStatus.Error,
                Effect = HtmlWorkspaceEffect.None,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                    ? "html_workspace_failed" : errorCode,
                Retryable = retryable
            };
        }

        internal static HtmlWorkspaceToolOutcome Unknown(
            string message, string dataJson, string errorCode)
        {
            return new HtmlWorkspaceToolOutcome
            {
                Status = HtmlWorkspaceOutcomeStatus.Unknown,
                Effect = HtmlWorkspaceEffect.Unknown,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                    ? "html_workspace_effect_unknown" : errorCode,
                Retryable = false
            };
        }
    }


}
