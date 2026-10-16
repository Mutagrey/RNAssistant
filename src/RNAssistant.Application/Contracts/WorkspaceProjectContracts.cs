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

    public sealed class SaveTabularProjectSourcesRequest : WorkspaceProjectPathRequest
    {
        [JsonProperty("projectRevision", Required = Required.Always)] public long ProjectRevision { get; set; }
        [JsonProperty("paths", Required = Required.Always)] public IReadOnlyList<string> Paths { get; set; }
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
