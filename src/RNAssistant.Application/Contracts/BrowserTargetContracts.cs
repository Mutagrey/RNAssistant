using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RNAssistant.Application.Contracts
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum BrowserTargetKind { HtmlArtifact, HtmlResource, HtmlWorkspace }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum BrowserTargetState { Ready, Empty, Unavailable }

    public sealed class BrowserTargetCapabilities
    {
        [JsonProperty("preview")] public bool Preview { get; set; }
        [JsonProperty("source")] public bool Source { get; set; }
        [JsonProperty("edit")] public bool Edit { get; set; }
        [JsonProperty("versions")] public bool Versions { get; set; }
        [JsonProperty("restore")] public bool Restore { get; set; }
        [JsonProperty("exportProject")] public bool ExportProject { get; set; }
        [JsonProperty("createProject")] public bool CreateProject { get; set; }
        [JsonProperty("annotations")] public bool Annotations { get; set; }
    }

    public sealed class BrowserTargetDescriptor
    {
        [JsonProperty("kind")] public BrowserTargetKind Kind { get; set; }
        [JsonProperty("state")] public BrowserTargetState State { get; set; }
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("documentId")] public string DocumentId { get; set; }
        [JsonProperty("resourceUri")] public string ResourceUri { get; set; }
        [JsonProperty("artifactId")] public string ArtifactId { get; set; }
        [JsonProperty("revision")] public int Revision { get; set; }
        [JsonProperty("capabilities")] public BrowserTargetCapabilities Capabilities { get; set; }
    }
}
