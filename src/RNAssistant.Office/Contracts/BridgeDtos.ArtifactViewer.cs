using System.Collections.Generic;
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

    public sealed class ArtifactPdfViewerPayload : ChatPayload
    {
        [JsonProperty("resourceUri")]
        public string ResourceUri { get; set; }
    }

    public sealed class ArtifactPdfPagePayload : ChatPayload
    {
        [JsonProperty("resourceUri")]
        public string ResourceUri { get; set; }

        [JsonProperty("pageIndex")]
        public int PageIndex { get; set; }
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

    public sealed class ArtifactPdfViewerDto
    {
        [JsonProperty("resourceUri")] public string ResourceUri { get; set; }
        [JsonProperty("viewerKind")] public string ViewerKind { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("mimeType")] public string MimeType { get; set; }
        [JsonProperty("contentSha256")] public string ContentSha256 { get; set; }
        [JsonProperty("byteLength")] public long ByteLength { get; set; }
        [JsonProperty("pageCount")] public int PageCount { get; set; }
        [JsonProperty("pageTextLengths")] public List<int> PageTextLengths { get; set; }
        [JsonProperty("extractedTextSha256")] public string ExtractedTextSha256 { get; set; }
        [JsonProperty("extractedCharacters")] public int ExtractedCharacters { get; set; }
        [JsonProperty("textTruncated")] public bool TextTruncated { get; set; }
        [JsonProperty("extractionWarning")] public string ExtractionWarning { get; set; }
    }

    public sealed class ArtifactPdfPageDto
    {
        [JsonProperty("resourceUri")] public string ResourceUri { get; set; }
        [JsonProperty("viewerKind")] public string ViewerKind { get; set; }
        [JsonProperty("contentSha256")] public string ContentSha256 { get; set; }
        [JsonProperty("pageIndex")] public int PageIndex { get; set; }
        [JsonProperty("pageCount")] public int PageCount { get; set; }
        [JsonProperty("width")] public int Width { get; set; }
        [JsonProperty("height")] public int Height { get; set; }
        [JsonProperty("imageMimeType")] public string ImageMimeType { get; set; }
        [JsonProperty("imageContentSha256")] public string ImageContentSha256 { get; set; }
        [JsonProperty("imageByteLength")] public long ImageByteLength { get; set; }
        [JsonProperty("imageBase64Content")] public string ImageBase64Content { get; set; }
    }
}
