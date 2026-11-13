using System.Collections.Generic;
using Newtonsoft.Json;

namespace RNAssistant.Application.Contracts
{
    public sealed class BrowserSourceRequest
    {
        [JsonProperty("ownerScope")] public BrowserOwnerScope OwnerScope { get; set; }
        [JsonProperty("locator")] public string Locator { get; set; }
        [JsonProperty("path")] public string Path { get; set; }
        [JsonProperty("offset")] public int Offset { get; set; }
        [JsonProperty("count")] public int Count { get; set; } = 32000;
        [JsonProperty("expectedRevision")] public string ExpectedRevision { get; set; }
        [JsonProperty("requestGeneration")] public long RequestGeneration { get; set; }
    }
    public sealed class BrowserSourcePage
    {
        [JsonProperty("target")] public BrowserTargetDescriptor Target { get; set; }
        [JsonProperty("files")] public IReadOnlyList<string> Files { get; set; }
        [JsonProperty("path")] public string Path { get; set; }
        [JsonProperty("text")] public string Text { get; set; }
        [JsonProperty("offset")] public int Offset { get; set; }
        [JsonProperty("totalCharacters")] public int TotalCharacters { get; set; }
        [JsonProperty("revision")] public string Revision { get; set; }
        [JsonProperty("requestGeneration")] public long RequestGeneration { get; set; }
    }
}
