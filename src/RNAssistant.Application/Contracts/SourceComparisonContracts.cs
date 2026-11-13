using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RNAssistant.Application.Contracts
{
    public abstract class SourceComparisonRequest
    {
        [JsonProperty("beforeId", Required = Required.Always)] public string BeforeId { get; set; }
        [JsonProperty("afterId", Required = Required.Always)] public string AfterId { get; set; }
        [JsonProperty("path")] public string Path { get; set; }
    }
    public sealed class PluginSourceComparisonRequest : SourceComparisonRequest
    {
        [JsonProperty("pluginId", Required = Required.Always)] public string PluginId { get; set; }
    }
    public sealed class BrowserSourceComparisonRequest : SourceComparisonRequest
    {
        [JsonProperty("target", Required = Required.Always)] public BrowserTargetDescriptor Target { get; set; }
    }
    [JsonConverter(typeof(StringEnumConverter))]
    public enum SourceFileChange { Added, Removed, Changed, Unchanged }
    public sealed class SourceComparisonFileDto
    {
        [JsonProperty("path")] public string Path { get; set; }
        [JsonProperty("change")] public SourceFileChange Change { get; set; }
    }
    public sealed class SourceComparisonDto
    {
        [JsonProperty("beforeId")] public string BeforeId { get; set; }
        [JsonProperty("afterId")] public string AfterId { get; set; }
        [JsonProperty("beforeLabel")] public string BeforeLabel { get; set; }
        [JsonProperty("afterLabel")] public string AfterLabel { get; set; }
        [JsonProperty("files")] public IReadOnlyList<SourceComparisonFileDto> Files { get; set; }
        [JsonProperty("path")] public string Path { get; set; }
        [JsonProperty("beforeText")] public string BeforeText { get; set; }
        [JsonProperty("afterText")] public string AfterText { get; set; }
        [JsonProperty("complete")] public bool Complete { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
    }
}
