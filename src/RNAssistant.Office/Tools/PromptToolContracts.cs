using System;

namespace RNAssistant.Office.Tools
{
    internal enum PromptOutcomeStatus
    {
        Ok,
        Error,
        Unknown
    }

    internal enum PromptToolEffect
    {
        None,
        VerifiedNoChange,
        VerifiedChange,
        Unknown
    }

    internal sealed class PromptToolOutcome
    {
        internal PromptOutcomeStatus Status { get; private set; }
        internal PromptToolEffect Effect { get; private set; }
        internal string Message { get; private set; }
        internal string DataJson { get; private set; }
        internal string ErrorCode { get; private set; }
        internal bool Retryable { get; private set; }

        internal static PromptToolOutcome Ok(
            string message, string dataJson, PromptToolEffect effect)
        {
            return new PromptToolOutcome
            {
                Status = PromptOutcomeStatus.Ok,
                Effect = effect,
                Message = message ?? string.Empty,
                DataJson = dataJson
            };
        }

        internal static PromptToolOutcome Error(
            string message, string dataJson, string errorCode, bool retryable)
        {
            return new PromptToolOutcome
            {
                Status = PromptOutcomeStatus.Error,
                Effect = PromptToolEffect.None,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                    ? "prompt_tool_failed" : errorCode,
                Retryable = retryable
            };
        }

        internal static PromptToolOutcome Unknown(
            string message, string dataJson, string errorCode)
        {
            return new PromptToolOutcome
            {
                Status = PromptOutcomeStatus.Unknown,
                Effect = PromptToolEffect.Unknown,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                    ? "prompt_settings_effect_unknown" : errorCode,
                Retryable = false
            };
        }
    }

    internal sealed class PromptSavePreparation
    {
        internal PromptToolOutcome Outcome { get; private set; }
        internal string PreparedStateJson { get; private set; }

        internal PromptSavePreparation(
            PromptToolOutcome outcome, string preparedStateJson = null)
        {
            Outcome = outcome ?? throw new ArgumentNullException(nameof(outcome));
            PreparedStateJson = preparedStateJson;
        }
    }
}
