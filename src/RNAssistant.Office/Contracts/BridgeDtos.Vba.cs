using RNAssistant.Core.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Office.Vba;

namespace RNAssistant.Office.Contracts
{
    public sealed class VbaEditorReadRequest : ChatPayload
    {
        [JsonProperty("moduleName")] public string ModuleName { get; set; }
    }

    public sealed class VbaEditorReadResponse
    {
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("moduleName")] public string ModuleName { get; set; }
        [JsonProperty("componentType")] public string ComponentType { get; set; }
        [JsonProperty("lineCount")] public int LineCount { get; set; }
        [JsonProperty("totalCharacters")] public int TotalCharacters { get; set; }
        [JsonProperty("codeSha256")] public string CodeSha256 { get; set; }
        [JsonProperty("resource")] public ResourceRef Resource { get; set; }
        [JsonProperty("data")] public ResourceDownloadOpenResponse Data { get; set; }
    }

    public sealed class VbaEditorUploadRequest : ChatPayload
    {
        [JsonProperty("byteLength")] public long ByteLength { get; set; }
    }

    public abstract class VbaEditorWriteRequest : ChatPayload
    {
        [JsonProperty("moduleName")]
        public string ModuleName { get; set; }

        [JsonProperty("uploadLeaseId")] public string UploadLeaseId { get; set; }
        [JsonProperty("sourceSha256")] public string SourceSha256 { get; set; }
    }

    public sealed class VbaModulePayload : VbaEditorWriteRequest
    {
        [JsonProperty("expectedCodeSha256")]
        public string ExpectedCodeSha256 { get; set; }
    }

    public sealed class VbaCreateModulePayload : VbaEditorWriteRequest
    {
        [JsonProperty("componentType")]
        public string ComponentType { get; set; }
    }

    public sealed class VbaDeleteModulePayload
    {
        [JsonProperty("moduleName")]
        public string ModuleName { get; set; }
    }

    public sealed class RestoreVbaBackupPayload
    {
        [JsonProperty("backupId")]
        public string BackupId { get; set; }

        [JsonProperty("moduleName")]
        public string ModuleName { get; set; }
    }

    public sealed class VbaMutationQueryPayload
    {
        [JsonProperty("cursor")] public string Cursor { get; set; }
        [JsonProperty("pageSize")] public int? PageSize { get; set; }
        [JsonProperty("search")] public string Search { get; set; }
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("runId")] public string RunId { get; set; }
        [JsonProperty("turnId")] public string TurnId { get; set; }
        [JsonProperty("stepId")] public string StepId { get; set; }
        [JsonProperty("toolCallId")] public string ToolCallId { get; set; }

        public VbaMutationQueryRequest ToQueryRequest()
        {
            return new VbaMutationQueryRequest
            {
                Cursor = Cursor,
                PageSize = PageSize.GetValueOrDefault(100),
                Search = Search,
                Kind = Kind,
                Status = Status,
                RunId = RunId,
                TurnId = TurnId,
                StepId = StepId,
                ToolCallId = ToolCallId
            };
        }
    }

    public sealed class VbaMutationDetailPayload
    {
        [JsonProperty("mutationId")]
        public string MutationId { get; set; }
    }

    public sealed class RunVbaMacroPayload
    {
        [JsonProperty("macroName")]
        public string MacroName { get; set; }
    }

    public sealed class VbaProjectResponse
    {
        [JsonProperty("result")]
        public ToolRunResult Result { get; set; }

        [JsonProperty("backups")]
        public IReadOnlyList<VbaModuleBackup> Backups { get; set; }
    }

    public sealed class VbaMutationQueryResponse
    {
        [JsonProperty("host")] public string Host { get; set; }
        [JsonProperty("documentKey")] public string DocumentKey { get; set; }
        [JsonProperty("documentTitle")] public string DocumentTitle { get; set; }
        [JsonProperty("view")] public string View { get; set; }
        [JsonProperty("totalEvents")] public int TotalEvents { get; set; }
        [JsonProperty("totalRows")] public int TotalRows { get; set; }
        [JsonProperty("totalMatches")] public int TotalMatches { get; set; }
        [JsonProperty("cursor")] public string Cursor { get; set; }
        [JsonProperty("nextCursor")] public string NextCursor { get; set; }
        [JsonProperty("hasMore")] public bool HasMore { get; set; }
        [JsonProperty("rows")] public IReadOnlyList<VbaMutationRowDto> Rows { get; set; }
    }

    public sealed class VbaMutationRowDto
    {
        [JsonProperty("mutationId")] public string MutationId { get; set; }
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("operation")] public string Operation { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("createdUtc")] public DateTime CreatedUtc { get; set; }
        [JsonProperty("completedUtc")] public DateTime? CompletedUtc { get; set; }
        [JsonProperty("firstSequence")] public long FirstSequence { get; set; }
        [JsonProperty("lastSequence")] public long LastSequence { get; set; }
        [JsonProperty("sessionId")] public string SessionId { get; set; }
        [JsonProperty("runId")] public string RunId { get; set; }
        [JsonProperty("turnId")] public string TurnId { get; set; }
        [JsonProperty("stepId")] public string StepId { get; set; }
        [JsonProperty("toolCallId")] public string ToolCallId { get; set; }
        [JsonProperty("moduleName")] public string ModuleName { get; set; }
        [JsonProperty("componentType")] public string ComponentType { get; set; }
        [JsonProperty("backupId")] public string BackupId { get; set; }
        [JsonProperty("packageId")] public string PackageId { get; set; }
        [JsonProperty("packageVersion")] public string PackageVersion { get; set; }
        [JsonProperty("lifecycleId")] public string LifecycleId { get; set; }
        [JsonProperty("sessionOnly")] public bool? SessionOnly { get; set; }
        [JsonProperty("componentCount")] public int ComponentCount { get; set; }
        [JsonProperty("componentNames")] public IReadOnlyList<string> ComponentNames { get; set; }
        [JsonProperty("errorCode")] public string ErrorCode { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
        [JsonProperty("sourceEventSeqs")] public IReadOnlyList<long> SourceEventSeqs { get; set; }
        [JsonProperty("sourceEventIds")] public IReadOnlyList<string> SourceEventIds { get; set; }

        public static VbaMutationRowDto From(VbaMutationQueryRow row)
        {
            if (row == null) return null;
            return new VbaMutationRowDto
            {
                MutationId = row.MutationId,
                Kind = row.Kind,
                Operation = row.Operation,
                Status = row.Status,
                CreatedUtc = row.CreatedUtc,
                CompletedUtc = row.CompletedUtc,
                FirstSequence = row.FirstSequence,
                LastSequence = row.LastSequence,
                SessionId = row.SessionId,
                RunId = row.RunId,
                TurnId = row.TurnId,
                StepId = row.StepId,
                ToolCallId = row.ToolCallId,
                ModuleName = row.ModuleName,
                ComponentType = row.ComponentType,
                BackupId = row.BackupId,
                PackageId = row.PackageId,
                PackageVersion = row.PackageVersion,
                LifecycleId = row.LifecycleId,
                SessionOnly = row.SessionOnly,
                ComponentCount = row.ComponentCount,
                ComponentNames = row.ComponentNames ?? new List<string>(),
                ErrorCode = row.ErrorCode,
                Message = row.Message,
                SourceEventSeqs = row.SourceEventSeqs ?? new List<long>(),
                SourceEventIds = row.SourceEventIds ?? new List<string>()
            };
        }
    }

    public sealed class VbaMutationDetailResponse
    {
        [JsonProperty("mutationId")] public string MutationId { get; set; }
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("operation")] public string Operation { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("createdUtc")] public DateTime CreatedUtc { get; set; }
        [JsonProperty("completedUtc")] public DateTime? CompletedUtc { get; set; }
        [JsonProperty("sessionId")] public string SessionId { get; set; }
        [JsonProperty("runId")] public string RunId { get; set; }
        [JsonProperty("turnId")] public string TurnId { get; set; }
        [JsonProperty("stepId")] public string StepId { get; set; }
        [JsonProperty("toolCallId")] public string ToolCallId { get; set; }
        [JsonProperty("packageId")] public string PackageId { get; set; }
        [JsonProperty("packageVersion")] public string PackageVersion { get; set; }
        [JsonProperty("lifecycleId")] public string LifecycleId { get; set; }
        [JsonProperty("sessionOnly")] public bool? SessionOnly { get; set; }
        [JsonProperty("ownershipMarker")] public string OwnershipMarker { get; set; }
        [JsonProperty("errorCode")] public string ErrorCode { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
        [JsonProperty("sourceEventSeqs")] public IReadOnlyList<long> SourceEventSeqs { get; set; }
        [JsonProperty("sourceEventIds")] public IReadOnlyList<string> SourceEventIds { get; set; }
        [JsonProperty("components")] public IReadOnlyList<VbaMutationComponentDto> Components { get; set; }

        public static VbaMutationDetailResponse From(VbaMutationDetail detail)
        {
            if (detail == null) return null;
            return new VbaMutationDetailResponse
            {
                MutationId = detail.MutationId,
                Kind = detail.Kind,
                Operation = detail.Operation,
                Status = detail.Status,
                CreatedUtc = detail.CreatedUtc,
                CompletedUtc = detail.CompletedUtc,
                SessionId = detail.SessionId,
                RunId = detail.RunId,
                TurnId = detail.TurnId,
                StepId = detail.StepId,
                ToolCallId = detail.ToolCallId,
                PackageId = detail.PackageId,
                PackageVersion = detail.PackageVersion,
                LifecycleId = detail.LifecycleId,
                SessionOnly = detail.SessionOnly,
                OwnershipMarker = detail.OwnershipMarker,
                ErrorCode = detail.ErrorCode,
                Message = detail.Message,
                SourceEventSeqs = detail.SourceEventSeqs ?? new List<long>(),
                SourceEventIds = detail.SourceEventIds ?? new List<string>(),
                Components = (detail.Components ?? new List<VbaMutationComponentDetail>())
                    .Select(VbaMutationComponentDto.From).Where(item => item != null).ToList()
            };
        }
    }

    public sealed class VbaMutationComponentDto
    {
        [JsonProperty("moduleName")] public string ModuleName { get; set; }
        [JsonProperty("beforeExists")] public bool BeforeExists { get; set; }
        [JsonProperty("beforeComponentType")] public string BeforeComponentType { get; set; }
        [JsonProperty("beforeCodeSha256")] public string BeforeCodeSha256 { get; set; }
        [JsonProperty("beforeCode")] public string BeforeCode { get; set; }
        [JsonProperty("intendedAfterExists")] public bool IntendedAfterExists { get; set; }
        [JsonProperty("intendedAfterComponentType")] public string IntendedAfterComponentType { get; set; }
        [JsonProperty("intendedAfterCodeSha256")] public string IntendedAfterCodeSha256 { get; set; }
        [JsonProperty("intendedAfterCode")] public string IntendedAfterCode { get; set; }
        [JsonProperty("backupId")] public string BackupId { get; set; }
        [JsonProperty("canRestore")] public bool CanRestore { get; set; }
        [JsonProperty("actualExists")] public bool? ActualExists { get; set; }
        [JsonProperty("actualComponentType")] public string ActualComponentType { get; set; }
        [JsonProperty("actualCodeSha256")] public string ActualCodeSha256 { get; set; }
        [JsonProperty("matchesBefore")] public bool? MatchesBefore { get; set; }
        [JsonProperty("matchesIntendedAfter")] public bool? MatchesIntendedAfter { get; set; }
        [JsonProperty("errorCode")] public string ErrorCode { get; set; }
        [JsonProperty("message")] public string Message { get; set; }

        public static VbaMutationComponentDto From(VbaMutationComponentDetail component)
        {
            if (component == null) return null;
            return new VbaMutationComponentDto
            {
                ModuleName = component.ModuleName,
                BeforeExists = component.BeforeExists,
                BeforeComponentType = component.BeforeComponentType,
                BeforeCodeSha256 = component.BeforeCodeSha256,
                BeforeCode = component.BeforeCode,
                IntendedAfterExists = component.IntendedAfterExists,
                IntendedAfterComponentType = component.IntendedAfterComponentType,
                IntendedAfterCodeSha256 = component.IntendedAfterCodeSha256,
                IntendedAfterCode = component.IntendedAfterCode,
                BackupId = component.BackupId,
                CanRestore = component.CanRestore,
                ActualExists = component.ActualExists,
                ActualComponentType = component.ActualComponentType,
                ActualCodeSha256 = component.ActualCodeSha256,
                MatchesBefore = component.MatchesBefore,
                MatchesIntendedAfter = component.MatchesIntendedAfter,
                ErrorCode = component.ErrorCode,
                Message = component.Message
            };
        }
    }

    public sealed class VbaPackageResultDto
    {
        [JsonProperty("contractVersion")]
        public int ContractVersion { get; set; }

        [JsonProperty("sourceRevision")]
        public string SourceRevision { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("code", NullValueHandling = NullValueHandling.Ignore)]
        public string Code { get; set; }

        [JsonProperty("retryable", NullValueHandling = NullValueHandling.Ignore)]
        public bool? Retryable { get; set; }

        [JsonProperty("mayHaveDispatched")]
        public bool MayHaveDispatched { get; set; }

        [JsonProperty("effect")]
        public string Effect { get; set; }

        [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)]
        public JObject Data { get; set; }

        internal static VbaPackageResultDto From(VbaPackageResult result)
        {
            if (result == null) return null;
            return new VbaPackageResultDto
            {
                ContractVersion = result.ContractVersion,
                SourceRevision = result.SourceRevision,
                Status = VbaPackageResult.StatusText(result.Status),
                Success = result.Status == VbaMutationOutcomeStatus.Ok,
                Message = result.Message,
                Code = result.ErrorCode,
                Retryable = result.Retryable,
                MayHaveDispatched = result.MayHaveDispatched,
                Effect = VbaPackageResult.EffectText(result.Effect),
                Data = result.Data
            };
        }
    }

    public sealed class VbaToolPackageResponse
    {
        [JsonProperty("result")]
        public VbaPackageResultDto Result { get; set; }

        [JsonProperty("tools")]
        public ToolLibraryResponse Tools { get; set; }
    }
}
