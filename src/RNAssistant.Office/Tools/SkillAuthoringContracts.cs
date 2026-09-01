using System;

namespace RNAssistant.Office.Tools
{
    internal enum SkillAuthoringOutcomeStatus
    {
        Ok,
        Error,
        Unknown
    }

    internal enum SkillAuthoringEffect
    {
        None,
        VerifiedNoChange,
        VerifiedChange,
        Unknown
    }

    internal sealed class SkillAuthoringOutcome
    {
        internal const int CurrentContractVersion = 1;

        internal SkillAuthoringOutcomeStatus Status { get; private set; }
        internal SkillAuthoringEffect Effect { get; private set; }
        internal string Message { get; private set; }
        internal string DataJson { get; private set; }
        internal string ErrorCode { get; private set; }
        internal bool Retryable { get; private set; }

        internal static SkillAuthoringOutcome Ok(
            string message, string dataJson,
            SkillAuthoringEffect effect)
        {
            return new SkillAuthoringOutcome
            {
                Status = SkillAuthoringOutcomeStatus.Ok,
                Effect = effect,
                Message = message ?? string.Empty,
                DataJson = dataJson
            };
        }

        internal static SkillAuthoringOutcome Error(
            string message, string dataJson = null,
            string errorCode = null, bool retryable = false,
            SkillAuthoringEffect effect = SkillAuthoringEffect.None)
        {
            return new SkillAuthoringOutcome
            {
                Status = SkillAuthoringOutcomeStatus.Error,
                Effect = effect,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                    ? "skill_authoring_failed" : errorCode,
                Retryable = retryable
            };
        }

        internal static SkillAuthoringOutcome Unknown(
            string message, string dataJson = null,
            string errorCode = null)
        {
            return new SkillAuthoringOutcome
            {
                Status = SkillAuthoringOutcomeStatus.Unknown,
                Effect = SkillAuthoringEffect.Unknown,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                    ? "skill_authoring_effect_unknown" : errorCode,
                Retryable = false
            };
        }
    }

    internal sealed class SkillAuthoringPreparation
    {
        internal SkillAuthoringOutcome Outcome { get; private set; }
        internal string PreparedStateJson { get; private set; }

        internal SkillAuthoringPreparation(
            SkillAuthoringOutcome outcome, string preparedStateJson = null)
        {
            Outcome = outcome ?? throw new ArgumentNullException(nameof(outcome));
            PreparedStateJson = preparedStateJson;
        }
    }
}
