using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using RNAssistant.Core.Storage;

namespace RNAssistant.Application.Contracts
{
    public sealed class OpenWorkspaceRequest
    {
        [JsonProperty("rootPath", Required = Required.Always)] public string RootPath { get; set; }
        [JsonProperty("readOnly", Required = Required.Always)] public bool ReadOnly { get; set; }
    }

    public class WorkspaceProjectPathRequest
    {
        [JsonProperty("bindingId", Required = Required.Always)] public string BindingId { get; set; }
        [JsonProperty("manifestPath", Required = Required.Always)] public string ManifestPath { get; set; }
    }

    public sealed class CreateTabularProjectRequest : WorkspaceProjectPathRequest
    {
        [JsonProperty("name", Required = Required.Always)] public string Name { get; set; }
    }

    public class WorkspaceSelectedProjectRequest : WorkspaceProjectPathRequest
    {
        [JsonProperty("projectRevision", Required = Required.Always)] public long ProjectRevision { get; set; }
    }

    public sealed class SaveTabularProjectSourcesRequest : WorkspaceSelectedProjectRequest
    {
        [JsonProperty("paths", Required = Required.Always)] public IReadOnlyList<string> Paths { get; set; }
    }

    public sealed class SaveTabularProjectDraftRequest : WorkspaceSelectedProjectRequest
    {
        [JsonProperty("file", Required = Required.Always)] public string File { get; set; }
        [JsonProperty("input", Required = Required.Always)] public string Input { get; set; }
    }

    public sealed class OpenTabularProjectDraftRequest : WorkspaceSelectedProjectRequest
    {
        [JsonProperty("file", Required = Required.Always)] public string File { get; set; }
    }

    public sealed class TabularProjectDraftDto
    {
        [JsonProperty("file")] public string File { get; internal set; }
        [JsonProperty("savedAt")] public long SavedAt { get; internal set; }
        [JsonProperty("label")] public string Label { get; internal set; }
        [JsonProperty("byteLength")] public long ByteLength { get; internal set; }
    }

    public sealed class TabularProjectDraftListDto
    {
        [JsonProperty("state")] public WorkspaceStateDto State { get; internal set; }
        [JsonProperty("items")] public IReadOnlyList<TabularProjectDraftDto> Items { get; internal set; }
    }

    public sealed class TabularProjectDraftSaveDto
    {
        [JsonProperty("state")] public WorkspaceStateDto State { get; internal set; }
        [JsonProperty("draft")] public TabularProjectDraftDto Draft { get; internal set; }
    }

    public sealed class TabularProjectDraftContentDto
    {
        [JsonProperty("state")] public WorkspaceStateDto State { get; internal set; }
        [JsonProperty("draft")] public TabularProjectDraftDto Draft { get; internal set; }
        [JsonProperty("input")] public string Input { get; internal set; }
    }

    public sealed class ListTabularProjectTemplatesRequest : WorkspaceSelectedProjectRequest
    {
        [JsonProperty("offset", Required = Required.Always)] public int Offset { get; set; }
    }

    public sealed class OpenTabularProjectTemplateRequest : WorkspaceSelectedProjectRequest
    {
        [JsonProperty("id", Required = Required.Always)] public string Id { get; set; }
        [JsonProperty("headFile", Required = Required.Always)] public string HeadFile { get; set; }
    }

    public sealed class TabularProjectTemplateHeadDto
    {
        [JsonProperty("id")] public string Id { get; internal set; }
        [JsonProperty("headFile")] public string HeadFile { get; internal set; }
        [JsonProperty("savedAt")] public long SavedAt { get; internal set; }
        [JsonProperty("archived")] public bool Archived { get; internal set; }
        [JsonProperty("versions")] public int Versions { get; internal set; }
    }

    public sealed class TabularProjectTemplateListDto
    {
        [JsonProperty("state")] public WorkspaceStateDto State { get; internal set; }
        [JsonProperty("offset")] public int Offset { get; internal set; }
        [JsonProperty("total")] public int Total { get; internal set; }
        [JsonProperty("items")] public IReadOnlyList<TabularProjectTemplateHeadDto> Items { get; internal set; }
    }

    public sealed class TabularProjectFileDto
    {
        [JsonProperty("file")] public string File { get; internal set; }
        [JsonProperty("format")] public string Format { get; internal set; }
        [JsonProperty("sha256")] public string Sha256 { get; internal set; }
        [JsonProperty("input")] public string Input { get; internal set; }
    }

    public sealed class TabularProjectTemplateContentDto
    {
        [JsonProperty("state")] public WorkspaceStateDto State { get; internal set; }
        [JsonProperty("id")] public string Id { get; internal set; }
        [JsonProperty("headFile")] public string HeadFile { get; internal set; }
        [JsonProperty("template")] public TabularProjectFileDto Template { get; internal set; }
    }

    public sealed class TabularProjectDictionaryContentDto
    {
        [JsonProperty("state")] public WorkspaceStateDto State { get; internal set; }
        [JsonProperty("dictionary")] public TabularProjectFileDto Dictionary { get; internal set; }
    }

    public sealed class TabularProjectSourceDto
    {
        [JsonProperty("sourceId")] public string SourceId { get; internal set; }
        [JsonProperty("path")] public string Path { get; internal set; }
    }

    public sealed class WorkspaceSelectionDto
    {
        [JsonProperty("bindingId")] public string BindingId { get; internal set; }
        [JsonProperty("workspaceId")] public string WorkspaceId { get; internal set; }
        [JsonProperty("name")] public string Name { get; internal set; }
        [JsonProperty("rootPath")] public string RootPath { get; internal set; }
        [JsonProperty("readOnly")] public bool ReadOnly { get; internal set; }
    }

    public sealed class WorkspaceProjectDto
    {
        [JsonProperty("name")] public string Name { get; internal set; }
        [JsonProperty("manifestPath")] public string ManifestPath { get; internal set; }
        [JsonProperty("formatVersion")] public int FormatVersion { get; internal set; }
        [JsonProperty("sources")] public IReadOnlyList<TabularProjectSourceDto> Sources { get; internal set; }
    }

    public sealed class WorkspaceStateDto
    {
        [JsonProperty("type")] public string Type { get { return "rnassistant.workspaceState"; } }
        [JsonProperty("contractVersion")] public int ContractVersion { get { return 1; } }
        [JsonProperty("revision")] public long Revision { get; internal set; }
        [JsonProperty("workspace")] public WorkspaceSelectionDto Workspace { get; internal set; }
        [JsonProperty("project")] public WorkspaceProjectDto Project { get; internal set; }
        [JsonProperty("sourceQueueId")] public string SourceQueueId { get; internal set; }
    }

    public sealed class WorkspaceReconcileResponse
    {
        [JsonProperty("state")] public WorkspaceStateDto State { get; internal set; }
        [JsonProperty("exists")] public bool Exists { get; internal set; }
        [JsonProperty("outcome"), JsonConverter(typeof(StringEnumConverter))]
        public WorkspaceRecoveryOutcome Outcome { get; internal set; }
    }
}
