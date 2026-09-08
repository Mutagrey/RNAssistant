using System.Collections.Generic;
using Newtonsoft.Json;

namespace RNAssistant.Office.Contracts
{
    public sealed class DocumentArtifactListRequest
    {
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("query")] public string Query { get; set; }
        [JsonProperty("cursor")] public string Cursor { get; set; }
    }

    public sealed class DocumentArtifactListDto
    {
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("sessionRevision")] public long SessionRevision { get; set; }
        [JsonProperty("items")] public IReadOnlyList<DocumentArtifactLinkDto> Items { get; set; }
        [JsonProperty("hasMore")] public bool HasMore { get; set; }
        [JsonProperty("nextCursor")] public string NextCursor { get; set; }
    }

    public sealed class DocumentArtifactLinkDto
    {
        [JsonProperty("resourceUri")] public string ResourceUri { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("revision")] public int Revision { get; set; }
        [JsonProperty("linked")] public bool Linked { get; set; }
        [JsonProperty("selected")] public bool Selected { get; set; }
        [JsonProperty("availabilityIssue")] public string AvailabilityIssue { get; set; }
    }

    public sealed class ArtifactLinkChangeRequest
    {
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("expectedSessionRevision")] public long? ExpectedSessionRevision { get; set; }
        [JsonProperty("resourceUri")] public string ResourceUri { get; set; }
        // Plans are selected when attached; originals are simply linked.
        [JsonProperty("detached")] public bool? Detached { get; set; }
    }
}
