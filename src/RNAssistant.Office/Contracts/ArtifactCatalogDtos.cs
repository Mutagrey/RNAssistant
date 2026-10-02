using System;
using System.Collections.Generic;
using Newtonsoft.Json;
namespace RNAssistant.Office.Contracts
{
    public sealed class ArtifactCatalogRequest
    {
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("scope")] public string Scope { get; set; }
        [JsonProperty("query")] public string Query { get; set; }
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("documentId")] public string DocumentId { get; set; }
        [JsonProperty("cursor")] public string Cursor { get; set; }
    }
    public sealed class ArtifactCatalogItem
    {
        [JsonProperty("documentId")] public string DocumentId { get; set; }
        [JsonProperty("documentTitle")] public string DocumentTitle { get; set; }
        [JsonProperty("resourceUri")] public string ResourceUri { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("updatedUtc")] public DateTime UpdatedUtc { get; set; }
        [JsonProperty("linked")] public bool Linked { get; set; }
        [JsonProperty("selected")] public bool Selected { get; set; }
        [JsonProperty("canTransfer")] public bool CanTransfer { get; set; }
        [JsonProperty("availabilityIssue")] public string AvailabilityIssue { get; set; }
    }
    public sealed class ArtifactCatalogDocument
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
    }
    public sealed class ArtifactCatalogResponse
    {
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("sessionRevision")] public long SessionRevision { get; set; }
        [JsonProperty("documentId")] public string DocumentId { get; set; }
        [JsonProperty("items")] public IReadOnlyList<ArtifactCatalogItem> Items { get; set; }
        [JsonProperty("documents")] public IReadOnlyList<ArtifactCatalogDocument> Documents { get; set; }
        [JsonProperty("nextCursor")] public string NextCursor { get; set; }
        [JsonProperty("hasMore")] public bool HasMore { get { return NextCursor != null; } }
    }
    public sealed class ArtifactTransferRequest
    {
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("documentId")] public string DocumentId { get; set; }
        [JsonProperty("resourceUri")] public string ResourceUri { get; set; }
        [JsonProperty("expectedSessionRevision")] public long? ExpectedSessionRevision { get; set; }
        [JsonProperty("operationId")] public string OperationId { get; set; }
        [JsonProperty("uploadLeaseId")] public string UploadLeaseId { get; set; }
        [JsonProperty("preview")] public bool Preview { get; set; }
    }
    public sealed class ArtifactTransferDownload
    {
        [JsonProperty("fileName")] public string FileName { get; set; }
        [JsonProperty("contentType")] public string ContentType { get; set; }
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("data")] public ResourceDownloadOpenResponse Data { get; set; }
    }
}
