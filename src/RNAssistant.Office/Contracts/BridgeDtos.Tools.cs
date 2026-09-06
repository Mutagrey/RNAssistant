using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Contracts
{
    public sealed class ToolLibraryDocumentationRequest
    {
        public const string ContractType =
            "rnassistant.toolLibraryDocumentationRequest";

        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("contractVersion")] public int ContractVersion { get; set; }
        [JsonProperty("toolId")] public string ToolId { get; set; }
        [JsonProperty("expectedRevision")] public string ExpectedRevision { get; set; }
    }

    public sealed class ToolLibraryDocumentationResponse
    {
        public const string ContractType =
            "rnassistant.toolLibraryDocumentation";

        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("contractVersion")] public int ContractVersion { get; set; }
        [JsonProperty("toolId")] public string ToolId { get; set; }
        [JsonProperty("revision")] public string Revision { get; set; }
        [JsonProperty("markdown")] public string Markdown { get; set; }
    }

    // Typed bulk upload body, not a bridge control payload.
    public sealed class ToolLibraryMutationBatch
    {
        public const string ContractType =
            "rnassistant.toolLibraryMutationRequest";

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("contractVersion")]
        public int ContractVersion { get; set; }

        [JsonProperty("mutations")]
        public List<ToolCoreMutationPayload> Mutations { get; set; }
    }

    public sealed class ToolMutationUploadRequest
    {
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("byteLength")] public long ByteLength { get; set; }
    }

    public sealed class ToolMutationWriteRequest
    {
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("uploadLeaseId")] public string UploadLeaseId { get; set; }
        [JsonProperty("sha256")] public string Sha256 { get; set; }
    }

    public sealed class ToolCoreMutationPayload
    {
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("baseId")] public string BaseId { get; set; }
        [JsonProperty("expectedRevision")] public string ExpectedRevision { get; set; }
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("host")] public string Host { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("description")] public string Description { get; set; }
        [JsonProperty("argumentSchemaJson")] public string ArgumentSchemaJson { get; set; }
        [JsonProperty("executor")] public string Executor { get; set; }
        [JsonProperty("requiresConfirmation")] public bool RequiresConfirmation { get; set; }
        [JsonProperty("mutatesDocument")] public bool MutatesDocument { get; set; }
        [JsonProperty("mutatesLocalState")] public bool MutatesLocalState { get; set; }
        [JsonProperty("agentCanRun")] public bool AgentCanRun { get; set; }
        [JsonProperty("code")] public string Code { get; set; }
        [JsonProperty("readme")] public string Readme { get; set; }
        [JsonProperty("enabled")] public bool Enabled { get; set; }
        [JsonProperty("riskLevel")] public int RiskLevel { get; set; }
        [JsonProperty("useWhen")] public string UseWhen { get; set; }
        [JsonProperty("doNotUseWhen")] public string DoNotUseWhen { get; set; }
        [JsonProperty("capabilityStatus")] public string CapabilityStatus { get; set; }
        [JsonProperty("limitations")] public string Limitations { get; set; }
        [JsonProperty("packageVersion")] public string PackageVersion { get; set; }
        [JsonProperty("entryPoint")] public string EntryPoint { get; set; }
        [JsonProperty("argumentOrder")] public List<string> ArgumentOrder { get; set; }
        [JsonProperty("components")] public List<ToolPackageComponentDto> Components { get; set; }

        internal ToolCatalogEntry ToCatalogEntry()
        {
            return new ToolCatalogEntry
            {
                Id = Id,
                Host = Host,
                Name = Name,
                Description = Description,
                ArgumentSchemaJson = ArgumentSchemaJson,
                Executor = string.IsNullOrWhiteSpace(Executor)
                    ? "vba" : Executor,
                RequiresConfirmation = RequiresConfirmation,
                MutatesDocument = MutatesDocument,
                MutatesLocalState = MutatesLocalState,
                AgentCanRun = AgentCanRun,
                Code = Code,
                Readme = Readme,
                Enabled = Enabled,
                BuiltIn = false,
                RiskLevel = RiskLevel,
                UseWhen = UseWhen,
                DoNotUseWhen = DoNotUseWhen,
                CapabilityStatus = CapabilityStatus,
                Limitations = Limitations,
                PackageVersion = PackageVersion,
                EntryPoint = EntryPoint,
                ArgumentOrder = ArgumentOrder ?? new List<string>(),
                Components = (Components ??
                    new List<ToolPackageComponentDto>())
                    .Where(component => component != null)
                    .Select(component => component.ToDefinition())
                    .ToList(),
                Scope = "global"
            };
        }
    }

    public sealed class ToolLibraryResponse
    {
        public const int CurrentContractVersion = 1;
        public const string ContractType = "rnassistant.toolLibrary";

        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("contractVersion")] public int ContractVersion { get; set; }
        [JsonProperty("tools")] public List<ToolLibraryItemDto> Tools { get; set; }

        internal static ToolLibraryResponse From(
            IEnumerable<ToolCatalogEntry> tools)
        {
            return new ToolLibraryResponse
            {
                Type = ContractType,
                ContractVersion = CurrentContractVersion,
                Tools = (tools ?? new ToolCatalogEntry[0])
                    .Where(tool => tool != null)
                    .Select(ToolLibraryItemDto.From)
                    .ToList()
            };
        }
    }

    public sealed class ToolLibraryItemDto
    {
        [JsonProperty("revision")] public string Revision { get; set; }
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("host")] public string Host { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("description")] public string Description { get; set; }
        [JsonProperty("argumentSchemaJson")] public string ArgumentSchemaJson { get; set; }
        [JsonProperty("executor")] public string Executor { get; set; }
        [JsonProperty("requiresConfirmation")] public bool RequiresConfirmation { get; set; }
        [JsonProperty("mutatesDocument")] public bool MutatesDocument { get; set; }
        [JsonProperty("mutatesLocalState")] public bool MutatesLocalState { get; set; }
        [JsonProperty("canSourceHtmlData")] public bool CanSourceHtmlData { get; set; }
        [JsonProperty("agentCanRun")] public bool AgentCanRun { get; set; }
        [JsonProperty("code")] public string Code { get; set; }
        [JsonProperty("readme")] public string Readme { get; set; }
        [JsonProperty("enabled")] public bool Enabled { get; set; }
        [JsonProperty("builtIn")] public bool BuiltIn { get; set; }
        [JsonProperty("riskLevel")] public int RiskLevel { get; set; }
        [JsonProperty("useWhen")] public string UseWhen { get; set; }
        [JsonProperty("doNotUseWhen")] public string DoNotUseWhen { get; set; }
        [JsonProperty("capabilityStatus")] public string CapabilityStatus { get; set; }
        [JsonProperty("limitations")] public string Limitations { get; set; }
        [JsonProperty("packageVersion")] public string PackageVersion { get; set; }
        [JsonProperty("entryPoint")] public string EntryPoint { get; set; }
        [JsonProperty("argumentOrder")] public List<string> ArgumentOrder { get; set; }
        [JsonProperty("components")] public List<ToolPackageComponentDto> Components { get; set; }
        [JsonProperty("scope")] public string Scope { get; set; }
        [JsonProperty("installationStatus")] public string InstallationStatus { get; set; }

        internal static ToolLibraryItemDto From(ToolCatalogEntry tool)
        {
            if (tool == null) return null;
            return new ToolLibraryItemDto
            {
                Revision = ToolAuthoringService.LibraryRevision(tool),
                Id = tool.Id ?? string.Empty,
                Host = tool.Host ?? string.Empty,
                Name = tool.Name ?? string.Empty,
                Description = tool.Description ?? string.Empty,
                ArgumentSchemaJson = tool.ArgumentSchemaJson ?? string.Empty,
                Executor = tool.Executor ?? string.Empty,
                RequiresConfirmation = tool.RequiresConfirmation,
                MutatesDocument = tool.MutatesDocument,
                MutatesLocalState = tool.MutatesLocalState,
                CanSourceHtmlData = tool.CanSourceHtmlData,
                AgentCanRun = tool.AgentCanRun,
                Code = tool.Code ?? string.Empty,
                Readme = tool.BuiltIn ? string.Empty : tool.Readme ?? string.Empty,
                Enabled = tool.Enabled,
                BuiltIn = tool.BuiltIn,
                RiskLevel = tool.RiskLevel,
                UseWhen = tool.UseWhen ?? string.Empty,
                DoNotUseWhen = tool.DoNotUseWhen ?? string.Empty,
                CapabilityStatus = tool.CapabilityStatus ?? string.Empty,
                Limitations = tool.Limitations ?? string.Empty,
                PackageVersion = tool.PackageVersion ?? string.Empty,
                EntryPoint = tool.EntryPoint ?? string.Empty,
                ArgumentOrder = new List<string>(tool.ArgumentOrder ??
                    new List<string>()),
                Components = (tool.Components ??
                    new List<ToolPackageComponentDefinition>())
                    .Where(component => component != null)
                    .Select(ToolPackageComponentDto.From).ToList(),
                Scope = tool.Scope ?? string.Empty,
                InstallationStatus = tool.InstallationStatus ?? string.Empty
            };
        }
    }

    public sealed class ToolPackageComponentDto
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("fileName")] public string FileName { get; set; }
        [JsonProperty("code")] public string Code { get; set; }
        [JsonProperty("codeSha256")] public string CodeSha256 { get; set; }

        internal static ToolPackageComponentDto From(
            ToolPackageComponentDefinition component)
        {
            return component == null ? null : new ToolPackageComponentDto
            {
                Name = component.Name ?? string.Empty,
                Type = component.Type ?? string.Empty,
                FileName = component.FileName ?? string.Empty,
                Code = component.Code ?? string.Empty,
                CodeSha256 = component.CodeSha256 ?? string.Empty
            };
        }

        internal ToolPackageComponentDefinition ToDefinition()
        {
            return new ToolPackageComponentDefinition
            {
                Name = Name,
                Type = Type,
                FileName = FileName,
                Code = Code,
                CodeSha256 = CodeSha256
            };
        }
    }

    public sealed class ToolMutationResultDto
    {
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("contractVersion")] public int ContractVersion { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
        [JsonProperty("code")] public string Code { get; set; }
        [JsonProperty("retryable")] public bool Retryable { get; set; }
        [JsonProperty("dispatch")] public string Dispatch { get; set; }
        [JsonProperty("effect")] public string Effect { get; set; }
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("operation")] public string Operation { get; set; }
        [JsonProperty("previousRevision")] public string PreviousRevision { get; set; }
        [JsonProperty("revision")] public string Revision { get; set; }

        internal static ToolMutationResultDto From(
            ToolManualMutationResult result)
        {
            if (result == null || result.Outcome == null) return null;
            var outcome = result.Outcome;
            return new ToolMutationResultDto
            {
                Type = "rnassistant.toolMutationResult",
                ContractVersion = ToolLibraryResponse.CurrentContractVersion,
                Status = outcome.Status == ToolAuthoringOutcomeStatus.Ok
                    ? "ok" : outcome.Status == ToolAuthoringOutcomeStatus.Unknown
                        ? "unknown" : "error",
                Message = outcome.Message ?? string.Empty,
                Code = outcome.Status == ToolAuthoringOutcomeStatus.Ok
                    ? null : outcome.ErrorCode,
                Retryable = outcome.Retryable,
                Dispatch = result.DispatchPossible
                    ? "may_have_dispatched" : "not_dispatched",
                Effect = EffectText(outcome.Effect),
                Id = result.Id,
                Operation = result.Operation,
                PreviousRevision = result.PreviousRevision,
                Revision = result.Revision
            };
        }

        private static string EffectText(ToolAuthoringEffect effect)
        {
            switch (effect)
            {
                case ToolAuthoringEffect.VerifiedNoChange:
                    return "verified_no_change";
                case ToolAuthoringEffect.VerifiedChange:
                    return "verified_change";
                case ToolAuthoringEffect.Unknown:
                    return "unknown";
                default:
                    return "none";
            }
        }
    }

    public sealed class ToolLibraryMutationResponse
    {
        public const string ContractType =
            "rnassistant.toolLibraryMutationResult";

        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("contractVersion")] public int ContractVersion { get; set; }
        [JsonProperty("results")] public List<ToolMutationResultDto> Results { get; set; }
        [JsonProperty("library")] public ToolLibraryResponse Library { get; set; }
    }
}
