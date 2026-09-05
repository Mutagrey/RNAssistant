using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Contracts
{
    // Bulk data-plane body, never an inline bridge payload.
    public sealed class SkillLibraryMutationBatch
    {
        public const string ContractType =
            "rnassistant.skillLibraryMutationRequest";

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("contractVersion")]
        public int ContractVersion { get; set; }

        [JsonProperty("mutations")]
        public List<SkillCoreMutationPayload> Mutations { get; set; }
    }

    public sealed class SkillCoreMutationPayload
    {
        [JsonProperty("kind")]
        public string Kind { get; set; }

        [JsonProperty("baseId")]
        public string BaseId { get; set; }

        [JsonProperty("expectedRevision")]
        public string ExpectedRevision { get; set; }

        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("host")]
        public string Host { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("bodyMarkdown")]
        public string BodyMarkdown { get; set; }

        [JsonProperty("preserveBody")]
        public bool PreserveBody { get; set; }

        [JsonProperty("enabled")]
        public bool Enabled { get; set; }
    }

    public class SkillReferencePayload
    {
        public const string ContractType =
            "rnassistant.skillReferenceRequest";

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("contractVersion")]
        public int ContractVersion { get; set; }

        [JsonProperty("skillId")]
        public string SkillId { get; set; }

        [JsonProperty("path")]
        public string Path { get; set; }

        [JsonProperty("expectedPackageRevision")]
        public string ExpectedPackageRevision { get; set; }
    }

    public sealed class SkillReferenceMutationBody : SkillReferencePayload
    {
        [JsonProperty("content")]
        public string Content { get; set; }
    }

    public sealed class SkillMutationUploadRequest
    {
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("byteLength")] public long ByteLength { get; set; }
    }

    public sealed class SkillMutationWriteRequest
    {
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("uploadLeaseId")] public string UploadLeaseId { get; set; }
        [JsonProperty("sha256")] public string Sha256 { get; set; }
    }

    public sealed class SkillSourceReadRequest
    {
        public const string ContractType = "rnassistant.skillSourceRequest";
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("contractVersion")] public int ContractVersion { get; set; }
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("skillId")] public string SkillId { get; set; }
        [JsonProperty("expectedPackageRevision")] public string ExpectedPackageRevision { get; set; }
        [JsonProperty("path")] public string Path { get; set; }
    }

    public sealed class SkillSourceReadResponse
    {
        public const string ContractType = "rnassistant.skillSourceRead";
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("contractVersion")] public int ContractVersion { get; set; }
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("skillId")] public string SkillId { get; set; }
        [JsonProperty("packageRevision")] public string PackageRevision { get; set; }
        [JsonProperty("path")] public string Path { get; set; }
        [JsonProperty("reference")] public SkillReferenceDto Reference { get; set; }
        [JsonProperty("resource")] public ResourceRef Resource { get; set; }
        [JsonProperty("totalCharacters")] public int TotalCharacters { get; set; }
        [JsonProperty("data")] public ResourceDownloadOpenResponse Data { get; set; }
    }

    public sealed class SkillReferenceResponse
    {
        public const string ContractType =
            "rnassistant.skillReferenceResult";

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("contractVersion")]
        public int ContractVersion { get; set; }

        [JsonProperty("result")]
        public SkillMutationResultDto Result { get; set; }

        [JsonProperty("skill")]
        public SkillPackageDto Skill { get; set; }

        [JsonProperty("path")]
        public string Path { get; set; }

        [JsonProperty("deleted")]
        public bool Deleted { get; set; }

        [JsonProperty("reference")]
        public SkillReferenceDto Reference { get; set; }
    }

    public sealed class SkillLibraryResponse
    {
        public const int CurrentContractVersion = 1;
        public const string ContractType = "rnassistant.skillLibrary";

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("contractVersion")]
        public int ContractVersion { get; set; }

        [JsonProperty("skills")]
        public List<SkillPackageDto> Skills { get; set; }

        internal static SkillLibraryResponse From(
            IEnumerable<SkillDefinition> skills)
        {
            return new SkillLibraryResponse
            {
                Type = ContractType,
                ContractVersion = CurrentContractVersion,
                Skills = (skills ?? new SkillDefinition[0])
                    .Where(skill => skill != null)
                    .Select(SkillPackageDto.From)
                    .ToList()
            };
        }
    }

    public sealed class SkillPackageDto
    {
        [JsonProperty("revision")]
        public string Revision { get; set; }

        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("host")]
        public string Host { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("body")]
        public SkillBodyMetadataDto Body { get; set; }

        [JsonProperty("enabled")]
        public bool Enabled { get; set; }

        [JsonProperty("builtIn")]
        public bool BuiltIn { get; set; }

        [JsonProperty("references")]
        public List<SkillReferenceDto> References { get; set; }

        internal static SkillPackageDto From(SkillDefinition skill)
        {
            return From(SkillPackageSource.Capture(skill),
                skill != null && skill.BuiltIn);
        }

        internal static SkillPackageDto From(
            SkillPackageSource source, bool builtIn = false)
        {
            if (source == null) return null;
            return new SkillPackageDto
            {
                Revision = source.Revision,
                Id = source.Id,
                Host = source.Host,
                Name = source.Name,
                Description = source.Description,
                Version = source.Version,
                Body = SkillBodyMetadataDto.From(source.BodyMarkdown),
                Enabled = source.Enabled,
                BuiltIn = builtIn,
                References = source.References
                    .Select(SkillReferenceDto.From).ToList()
            };
        }
    }

    public sealed class SkillBodyMetadataDto
    {
        [JsonProperty("sha256")] public string Sha256 { get; set; }
        [JsonProperty("byteLength")] public int ByteLength { get; set; }
        [JsonProperty("characters")] public int Characters { get; set; }

        internal static SkillBodyMetadataDto From(string text)
        {
            text = text ?? string.Empty;
            return new SkillBodyMetadataDto { Sha256 = TextPatternEngine.Sha256(text),
                ByteLength = Encoding.UTF8.GetByteCount(text), Characters = text.Length };
        }
    }

    public sealed class SkillReferenceDto
    {
        [JsonProperty("path")]
        public string Path { get; set; }

        [JsonProperty("byteLength")]
        public long ByteLength { get; set; }

        [JsonProperty("revision")]
        public string Revision { get; set; }

        internal static SkillReferenceDto From(
            SkillPackageReferenceSource source)
        {
            return source == null ? null : new SkillReferenceDto
            {
                Path = source.Path,
                ByteLength = source.ByteLength,
                Revision = source.Revision
            };
        }
    }

    public sealed class SkillMutationResultDto
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("contractVersion")]
        public int ContractVersion { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("retryable")]
        public bool Retryable { get; set; }

        [JsonProperty("dispatch")]
        public string Dispatch { get; set; }

        [JsonProperty("effect")]
        public string Effect { get; set; }

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

        [JsonProperty("changed")]
        public bool Changed { get; set; }

        internal static SkillMutationResultDto From(
            SkillManualMutationResult result)
        {
            if (result == null || result.Outcome == null) return null;
            var outcome = result.Outcome;
            var data = outcome.Data;
            return new SkillMutationResultDto
            {
                Type = "rnassistant.skillMutationResult",
                ContractVersion = SkillAuthoringOutcome.CurrentContractVersion,
                Status = outcome.Status == SkillAuthoringOutcomeStatus.Ok
                    ? "ok" : outcome.Status == SkillAuthoringOutcomeStatus.Unknown
                        ? "unknown" : "error",
                Message = outcome.Message,
                Code = outcome.Status == SkillAuthoringOutcomeStatus.Ok
                    ? null : outcome.ErrorCode,
                Retryable = outcome.Retryable,
                Dispatch = result.DispatchPossible
                    ? "may_have_dispatched" : "not_dispatched",
                Effect = ToEffectContract(outcome.Effect),
                Id = data == null ? string.Empty : data.Id,
                Operation = data == null ? string.Empty : data.Operation,
                ReferencePath = data == null ? null : data.ReferencePath,
                PreviousRevision = data == null
                    ? string.Empty : data.PreviousRevision,
                Revision = data == null ? string.Empty : data.Revision,
                Changed = data != null && data.Changed
            };
        }

        private static string ToEffectContract(SkillAuthoringEffect effect)
        {
            switch (effect)
            {
                case SkillAuthoringEffect.VerifiedNoChange:
                    return "verified_no_change";
                case SkillAuthoringEffect.VerifiedChange:
                    return "verified_change";
                case SkillAuthoringEffect.Unknown:
                    return "unknown";
                default:
                    return "none";
            }
        }
    }

    public sealed class SkillLibraryMutationResponse
    {
        public const string ContractType =
            "rnassistant.skillLibraryMutationResult";

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("contractVersion")]
        public int ContractVersion { get; set; }

        [JsonProperty("results")]
        public List<SkillMutationResultDto> Results { get; set; }

        [JsonProperty("library")]
        public SkillLibraryResponse Library { get; set; }
    }
}
