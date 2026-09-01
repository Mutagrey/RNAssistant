using System;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

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
        internal SkillAuthoringResultData Data { get; private set; }
        internal string DataJson
        {
            get
            {
                return Data == null ? null :
                    JsonConvert.SerializeObject(Data, Formatting.None);
            }
        }
        internal string ErrorCode { get; private set; }
        internal bool Retryable { get; private set; }

        internal static SkillAuthoringOutcome Ok(
            string message, SkillAuthoringResultData data,
            SkillAuthoringEffect effect)
        {
            return new SkillAuthoringOutcome
            {
                Status = SkillAuthoringOutcomeStatus.Ok,
                Effect = effect,
                Message = message ?? string.Empty,
                Data = data
            };
        }

        internal static SkillAuthoringOutcome Error(
            string message, SkillAuthoringResultData data = null,
            string errorCode = null, bool retryable = false,
            SkillAuthoringEffect effect = SkillAuthoringEffect.None)
        {
            return new SkillAuthoringOutcome
            {
                Status = SkillAuthoringOutcomeStatus.Error,
                Effect = effect,
                Message = message ?? string.Empty,
                Data = data,
                ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                    ? "skill_authoring_failed" : errorCode,
                Retryable = retryable
            };
        }

        internal static SkillAuthoringOutcome Unknown(
            string message, SkillAuthoringResultData data = null,
            string errorCode = null)
        {
            return new SkillAuthoringOutcome
            {
                Status = SkillAuthoringOutcomeStatus.Unknown,
                Effect = SkillAuthoringEffect.Unknown,
                Message = message ?? string.Empty,
                Data = data,
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
        internal string BeforeRevision { get; private set; }

        internal SkillAuthoringPreparation(
            SkillAuthoringOutcome outcome, string preparedStateJson = null,
            string beforeRevision = null)
        {
            Outcome = outcome ?? throw new ArgumentNullException(nameof(outcome));
            PreparedStateJson = preparedStateJson;
            BeforeRevision = beforeRevision;
        }
    }

    internal sealed class SkillAuthoringResultData
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("contractVersion")]
        public int ContractVersion { get; set; }

        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("operation")]
        public string Operation { get; set; }

        [JsonProperty("referencePath")]
        public string ReferencePath { get; set; }

        [JsonProperty("previousRevision")]
        public string PreviousRevision { get; set; }

        [JsonProperty("revision")]
        public string Revision { get; set; }

        [JsonProperty("expectedRevision", NullValueHandling = NullValueHandling.Ignore)]
        public string ExpectedRevision { get; set; }

        [JsonProperty("changed")]
        public bool Changed { get; set; }
    }

    internal sealed class SkillLibraryCoreMutation
    {
        internal string Kind { get; set; }
        internal string BaseId { get; set; }
        internal string ExpectedRevision { get; set; }
        internal SkillDefinition Intended { get; set; }
    }

    internal sealed class SkillManualMutationResult
    {
        internal SkillAuthoringOutcome Outcome { get; private set; }
        internal bool DispatchPossible { get; private set; }
        internal SkillPackageSource Package { get; private set; }

        internal SkillManualMutationResult(
            SkillAuthoringOutcome outcome,
            bool dispatchPossible,
            SkillPackageSource package)
        {
            Outcome = outcome ?? throw new ArgumentNullException(nameof(outcome));
            DispatchPossible = dispatchPossible;
            Package = package;
        }
    }

    internal sealed class SkillReferenceReadResult
    {
        internal SkillPackageSource Package { get; private set; }
        internal SkillPackageReferenceSource Reference { get; private set; }
        internal string Content { get; private set; }

        internal SkillReferenceReadResult(
            SkillPackageSource package,
            SkillPackageReferenceSource reference,
            string content)
        {
            Package = package ?? throw new ArgumentNullException(nameof(package));
            Reference = reference ?? throw new ArgumentNullException(nameof(reference));
            Content = content ?? string.Empty;
        }
    }
}
