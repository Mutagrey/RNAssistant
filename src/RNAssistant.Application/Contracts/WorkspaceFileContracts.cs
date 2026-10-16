using System.Collections.Generic;
using Newtonsoft.Json;

namespace RNAssistant.Application.Contracts
{
    public sealed class BrowseWorkspaceFilesRequest
    {
        [JsonProperty("bindingId", Required = Required.Always)] public string BindingId { get; set; }
        [JsonProperty("directory")] public string Directory { get; set; } = "";
        [JsonProperty("query")] public string Query { get; set; }
        [JsonProperty("offset")] public int Offset { get; set; }
    }

    public sealed class WorkspaceFileEntryDto
    {
        [JsonProperty("name")] public string Name { get; internal set; }
        [JsonProperty("path")] public string Path { get; internal set; }
        [JsonProperty("isDirectory")] public bool IsDirectory { get; internal set; }
        [JsonProperty("byteLength")] public long? ByteLength { get; internal set; }
    }

    public sealed class WorkspaceFilesPage
    {
        [JsonProperty("type")] public string Type { get { return "rnassistant.workspaceFiles"; } }
        [JsonProperty("contractVersion")] public int ContractVersion { get { return 1; } }
        [JsonProperty("bindingId")] public string BindingId { get; internal set; }
        [JsonProperty("directory")] public string Directory { get; internal set; }
        [JsonProperty("offset")] public int Offset { get; internal set; }
        [JsonProperty("matched")] public int Matched { get; internal set; }
        [JsonProperty("scanLimitReached")] public bool ScanLimitReached { get; internal set; }
        [JsonProperty("items")] public IReadOnlyList<WorkspaceFileEntryDto> Items { get; internal set; }
    }
}
