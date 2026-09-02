using Newtonsoft.Json;

namespace RNAssistant.Office.Contracts
{
    public sealed class ArtifactViewerPagePayload : ChatPayload
    {
        [JsonProperty("resourceUri")]
        public string ResourceUri { get; set; }

        [JsonProperty("cursor")]
        public string Cursor { get; set; }
    }

    public sealed class ArtifactImageViewerPayload : ChatPayload
    {
        [JsonProperty("resourceUri")]
        public string ResourceUri { get; set; }
    }

    public sealed class ArtifactViewerPageDto
    {
        [JsonProperty("resourceUri")] public string ResourceUri { get; set; }
        [JsonProperty("viewerKind")] public string ViewerKind { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("mimeType")] public string MimeType { get; set; }
        [JsonProperty("contentSha256")] public string ContentSha256 { get; set; }
        [JsonProperty("text")] public string Text { get; set; }
        [JsonProperty("offset")] public int Offset { get; set; }
        [JsonProperty("returnedCharacters")] public int ReturnedCharacters { get; set; }
        [JsonProperty("totalCharacters")] public int TotalCharacters { get; set; }
        [JsonProperty("nextCursor")] public string NextCursor { get; set; }
        [JsonProperty("complete")] public bool Complete { get; set; }
        [JsonProperty("truncated")] public bool Truncated { get; set; }
        [JsonProperty("sourceComplete")] public bool SourceComplete { get; set; }
        [JsonProperty("fullReadAllowed")] public bool FullReadAllowed { get; set; }
        [JsonProperty("viewerLimitReached")] public bool ViewerLimitReached { get; set; }
        [JsonProperty("maximumDocumentCharacters")] public int MaximumDocumentCharacters { get; set; }
    }

    public sealed class ArtifactImageViewerDto
    {
        [JsonProperty("resourceUri")] public string ResourceUri { get; set; }
        [JsonProperty("viewerKind")] public string ViewerKind { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("mimeType")] public string MimeType { get; set; }
        [JsonProperty("contentSha256")] public string ContentSha256 { get; set; }
        [JsonProperty("byteLength")] public long ByteLength { get; set; }
        [JsonProperty("base64Content")] public string Base64Content { get; set; }
    }
}
