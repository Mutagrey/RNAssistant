using Newtonsoft.Json;

namespace RNAssistant.Office.Contracts
{
    public sealed class ArtifactLinkChangeRequest
    {
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("expectedSessionRevision")] public long? ExpectedSessionRevision { get; set; }
        [JsonProperty("resourceUri")] public string ResourceUri { get; set; }
        // Plans are selected when attached; originals are simply linked.
        [JsonProperty("detached")] public bool? Detached { get; set; }
    }
}
