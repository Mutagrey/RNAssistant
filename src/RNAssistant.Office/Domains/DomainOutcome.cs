using System;

namespace RNAssistant.Office.Domains
{
    public enum DomainOutcomeStatus { Ok, Error, Unknown }
    public enum DomainEffect { None, VerifiedNoChange, VerifiedChange, Unknown }

    // Immutable facts only. Domain services still own guards, dispatch and read-back.
    public sealed class DomainOutcome
    {
        public DomainOutcomeStatus Status { get; private set; }
        public DomainEffect Effect { get; private set; }
        public string Message { get; private set; }
        public string DataJson { get; private set; }
        public string ErrorCode { get; private set; }
        public bool Retryable { get; private set; }

        private DomainOutcome(DomainOutcomeStatus status, DomainEffect effect, string message,
            string dataJson, string errorCode = null, bool retryable = false)
        {
            if (status != DomainOutcomeStatus.Ok && string.IsNullOrWhiteSpace(errorCode))
                throw new ArgumentException("A domain failure requires its error code.", nameof(errorCode));
            Status = status;
            Effect = effect;
            Message = message ?? string.Empty;
            DataJson = dataJson;
            ErrorCode = errorCode;
            Retryable = retryable;
        }

        public static DomainOutcome Read(string message, string dataJson)
        { return new DomainOutcome(DomainOutcomeStatus.Ok, DomainEffect.None, message, dataJson); }

        public static DomainOutcome Verified(string message, string dataJson, DomainEffect effect)
        {
            if (effect != DomainEffect.VerifiedNoChange && effect != DomainEffect.VerifiedChange)
                throw new ArgumentException("A verified write effect is required.", nameof(effect));
            return new DomainOutcome(DomainOutcomeStatus.Ok, effect, message, dataJson);
        }

        public static DomainOutcome Error(string message, string dataJson, string errorCode, bool retryable)
        { return new DomainOutcome(DomainOutcomeStatus.Error, DomainEffect.None, message, dataJson, errorCode, retryable); }

        public static DomainOutcome Unknown(string message, string dataJson, string errorCode)
        { return new DomainOutcome(DomainOutcomeStatus.Unknown, DomainEffect.Unknown, message, dataJson, errorCode); }
    }
}
