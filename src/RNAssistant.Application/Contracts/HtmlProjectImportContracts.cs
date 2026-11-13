using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RNAssistant.Application.Contracts
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum HtmlProjectBacking { Managed }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum HtmlProjectImportStatus { Created, Rejected, Unknown }

    public sealed class HtmlProjectImportResult
    {
        [JsonProperty("status")] public HtmlProjectImportStatus Status { get; set; }
        [JsonProperty("backing")] public HtmlProjectBacking Backing { get; set; }
        [JsonProperty("workspaceId")] public string WorkspaceId { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("rootPath")] public string RootPath { get; set; }
        [JsonProperty("entryPath")] public string EntryPath { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
        [JsonProperty("reason")] public BrowserRefusalCode Reason { get; set; }
    }
}
