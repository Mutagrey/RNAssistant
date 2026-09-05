using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Contracts
{
    public sealed class ResourceDownloadOpenResponse
    {
        [JsonProperty("leaseId")] public string LeaseId { get; set; }
        [JsonProperty("url")] public string Url { get; set; }
        [JsonProperty("payload")] public PayloadRef Payload { get; set; }
        [JsonProperty("maxChunkBytes")] public int MaxChunkBytes { get; set; }
        [JsonProperty("expiresUtc")] public DateTime ExpiresUtc { get; set; }
    }

    public sealed class ResourceUploadOpenRequest : ChatPayload
    {
        [JsonProperty("fileName")] public string FileName { get; set; }
        [JsonProperty("contentType")] public string ContentType { get; set; }
        [JsonProperty("byteLength")] public long ByteLength { get; set; }
    }

    public sealed class ResourceUploadLeaseRequest : ChatPayload
    {
        [JsonProperty("leaseId")] public string LeaseId { get; set; }
    }

    public sealed class ResourceUploadOpenResponse
    {
        [JsonProperty("leaseId")] public string LeaseId { get; set; }
        [JsonProperty("url")] public string Url { get; set; }
        [JsonProperty("byteLength")] public long ByteLength { get; set; }
        [JsonProperty("maxChunkBytes")] public int MaxChunkBytes { get; set; }
        [JsonProperty("expiresUtc")] public DateTime ExpiresUtc { get; set; }
    }

    public sealed class ResourceUploadBatchResponse
    {
        [JsonProperty("leaseId")] public string LeaseId { get; set; }
        [JsonProperty("nextOffset")] public int NextOffset { get; set; }
    }

    public sealed class ResourceChangedMessage
    {
        [JsonProperty("type")] public string Type { get { return "resourceChanged"; } }
        [JsonProperty("scope")] public string Scope { get; set; }
        [JsonProperty("generation")] public long Generation { get; set; }
        [JsonProperty("commitId")] public string CommitId { get; set; }
        [JsonProperty("resources")] public string[] Resources { get; set; }
        [JsonProperty("allInScope")] public bool AllInScope { get; set; }
    }

    public sealed class ResourceDataOpenRequest
    {
        public string ChatId { get; set; }
        public string WorkspaceId { get; set; }
        public string BindingName { get; set; }
    }

    public sealed class ResourceDataCloseRequest
    {
        public string ChatId { get; set; }
        public string WorkspaceId { get; set; }
        public string LeaseId { get; set; }
    }

    public sealed class ResourceDataCloseResponse
    {
        [JsonProperty("closed")] public bool Closed { get; set; }
    }

    public sealed class ResourceDataOpenResponse
    {
        [JsonProperty("leaseId")] public string LeaseId { get; set; }
        [JsonProperty("url")] public string Url { get; set; }
        [JsonProperty("descriptor")] public ResourceDescriptor Descriptor { get; set; }
        [JsonProperty("view")] public string View { get; set; }
        [JsonProperty("path")] public string ViewPath { get; set; }
        [JsonProperty("expiresUtc")] public DateTime ExpiresUtc { get; set; }
        [JsonProperty("maxBatchBytes")] public int MaxBatchBytes { get; set; }
        [JsonProperty("maxBatchItems")] public int MaxBatchItems { get; set; }
        [JsonProperty("binary", NullValueHandling = NullValueHandling.Ignore)] public ResourceBinaryView Binary { get; set; }
    }

    public sealed class ResourceDataBatch
    {
        [JsonProperty("resource")] public ResourceRef Resource { get; set; }
        [JsonProperty("view")] public string View { get; set; }
        [JsonProperty("text", NullValueHandling = NullValueHandling.Ignore)] public string Text { get; set; }
        [JsonProperty("offset")] public long Offset { get; set; }
        [JsonProperty("nextOffset")] public long NextOffset { get; set; }
        [JsonProperty("done")] public bool Done { get; set; }
        [JsonProperty("coverage")] public ResourceCoverage Coverage { get; set; }
        [JsonProperty("columns", NullValueHandling = NullValueHandling.Ignore)] public IReadOnlyList<ResourceTableColumn> Columns { get; set; }
        [JsonProperty("rows", NullValueHandling = NullValueHandling.Ignore)] public IReadOnlyList<IDictionary<string, object>> Rows { get; set; }
    }

    public sealed class ResourceDataError
    {
        [JsonProperty("code")] public string Code { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
    }
}
