using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Office.Contracts
{
    public sealed class ModelRequestDiagnosticsMessage
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("payload")]
        public ModelRequestDiagnosticsDto Payload { get; set; }
    }

    public sealed class ModelRequestDiagnosticsDto
    {
        [JsonProperty("requestId")]
        public string RequestId { get; set; }

        [JsonProperty("phase")]
        public string Phase { get; set; }

        [JsonProperty("model")]
        public string Model { get; set; }

        [JsonProperty("streamRequested")]
        public bool StreamRequested { get; set; }

        [JsonProperty("elapsedMs")]
        public long ElapsedMs { get; set; }

        [JsonProperty("preparationMs", NullValueHandling = NullValueHandling.Ignore)]
        public long? PreparationMs { get; set; }

        [JsonProperty("responseHeadersMs", NullValueHandling = NullValueHandling.Ignore)]
        public long? ResponseHeadersMs { get; set; }

        [JsonProperty("firstChunkMs", NullValueHandling = NullValueHandling.Ignore)]
        public long? FirstChunkMs { get; set; }

        [JsonProperty("totalMs", NullValueHandling = NullValueHandling.Ignore)]
        public long? TotalMs { get; set; }

        [JsonProperty("requestBytes", NullValueHandling = NullValueHandling.Ignore)]
        public long? RequestBytes { get; set; }

        [JsonProperty("statusCode", NullValueHandling = NullValueHandling.Ignore)]
        public int? StatusCode { get; set; }

        [JsonProperty("failureKind", NullValueHandling = NullValueHandling.Ignore)]
        public string FailureKind { get; set; }

        [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
        public string Error { get; set; }

        public static ModelRequestDiagnosticsDto From(LlmRequestDiagnosticUpdate source)
        {
            if (source == null) return null;
            return new ModelRequestDiagnosticsDto
            {
                RequestId = source.RequestId,
                Phase = source.Phase,
                Model = source.Model,
                StreamRequested = source.StreamRequested,
                ElapsedMs = source.ElapsedMs,
                PreparationMs = source.PreparationMs,
                ResponseHeadersMs = source.ResponseHeadersMs,
                FirstChunkMs = source.FirstChunkMs,
                TotalMs = source.TotalMs,
                RequestBytes = source.RequestBytes,
                StatusCode = source.StatusCode,
                FailureKind = source.FailureKind.HasValue ? source.FailureKind.Value.ToString() : null,
                Error = source.Error
            };
        }
    }

    public sealed class RuntimeLogResponse
    {
        [JsonProperty("content")]
        public string Content { get; set; }

        [JsonProperty("path")]
        public string Path { get; set; }
    }

    public sealed class CasHealthResponse
    {
        private const int MaxDetails = 250;

        [JsonProperty("scannedUtc")] public System.DateTime ScannedUtc { get; set; }
        [JsonProperty("reachabilityComplete")] public bool ReachabilityComplete { get; set; }
        [JsonProperty("healthy")] public bool Healthy { get; set; }
        [JsonProperty("canGarbageCollect")] public bool CanGarbageCollect { get; set; }
        [JsonProperty("chatStreamCount")] public int ChatStreamCount { get; set; }
        [JsonProperty("vbaJournalCount")] public int VbaJournalCount { get; set; }
        [JsonProperty("referenceOccurrenceCount")] public int ReferenceOccurrenceCount { get; set; }
        [JsonProperty("referencedBlobCount")] public int ReferencedBlobCount { get; set; }
        [JsonProperty("storedBlobCount")] public int StoredBlobCount { get; set; }
        [JsonProperty("storedByteLength")] public long StoredByteLength { get; set; }
        [JsonProperty("missingBlobCount")] public int MissingBlobCount { get; set; }
        [JsonProperty("corruptBlobCount")] public int CorruptBlobCount { get; set; }
        [JsonProperty("orphanBlobCount")] public int OrphanBlobCount { get; set; }
        [JsonProperty("orphanStoredByteLength")] public long OrphanStoredByteLength { get; set; }
        [JsonProperty("detailsTruncated")] public bool DetailsTruncated { get; set; }
        [JsonProperty("issues")] public IReadOnlyList<CasHealthIssueDto> Issues { get; set; }
        [JsonProperty("orphanBlobs")] public IReadOnlyList<CasOrphanBlobDto> OrphanBlobs { get; set; }

        public static CasHealthResponse From(CasHealthReport source)
        {
            source = source ?? new CasHealthReport();
            var issues = source.Issues ?? new CasHealthIssue[0];
            var orphans = source.OrphanBlobs ?? new CasOrphanBlob[0];
            return new CasHealthResponse
            {
                ScannedUtc = source.ScannedUtc,
                ReachabilityComplete = source.ReachabilityComplete,
                Healthy = source.Healthy,
                CanGarbageCollect = source.CanGarbageCollect,
                ChatStreamCount = source.ChatStreamCount,
                VbaJournalCount = source.VbaJournalCount,
                ReferenceOccurrenceCount = source.ReferenceOccurrenceCount,
                ReferencedBlobCount = source.ReferencedBlobCount,
                StoredBlobCount = source.StoredBlobCount,
                StoredByteLength = source.StoredByteLength,
                MissingBlobCount = source.MissingBlobCount,
                CorruptBlobCount = source.CorruptBlobCount,
                OrphanBlobCount = source.OrphanBlobCount,
                OrphanStoredByteLength = source.OrphanStoredByteLength,
                DetailsTruncated = issues.Count > MaxDetails || orphans.Count > MaxDetails,
                Issues = issues.Take(MaxDetails).Select(CasHealthIssueDto.From).ToList(),
                OrphanBlobs = orphans.Take(MaxDetails).Select(CasOrphanBlobDto.From).ToList()
            };
        }
    }

    public sealed class CasHealthIssueDto
    {
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("sourceType")] public string SourceType { get; set; }
        [JsonProperty("sourceId")] public string SourceId { get; set; }
        [JsonProperty("location")] public string Location { get; set; }
        [JsonProperty("sha256")] public string Sha256 { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
        [JsonProperty("blocksGarbageCollection")] public bool BlocksGarbageCollection { get; set; }

        public static CasHealthIssueDto From(CasHealthIssue source)
        {
            return source == null ? null : new CasHealthIssueDto
            {
                Kind = source.Kind,
                SourceType = source.SourceType,
                SourceId = source.SourceId,
                Location = source.Location,
                Sha256 = source.Sha256,
                Message = source.Message,
                BlocksGarbageCollection = source.BlocksGarbageCollection
            };
        }
    }

    public sealed class CasOrphanBlobDto
    {
        [JsonProperty("sha256")] public string Sha256 { get; set; }
        [JsonProperty("storedByteLength")] public long StoredByteLength { get; set; }

        public static CasOrphanBlobDto From(CasOrphanBlob source)
        {
            return source == null ? null : new CasOrphanBlobDto
            {
                Sha256 = source.Sha256,
                StoredByteLength = source.StoredByteLength
            };
        }
    }

    public sealed class CasGarbageCollectionResponse
    {
        [JsonProperty("completed")] public bool Completed { get; set; }
        [JsonProperty("deletedBlobCount")] public int DeletedBlobCount { get; set; }
        [JsonProperty("deletedStoredByteLength")] public long DeletedStoredByteLength { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
        [JsonProperty("deleteFailures")] public IReadOnlyList<CasHealthIssueDto> DeleteFailures { get; set; }
        [JsonProperty("health")] public CasHealthResponse Health { get; set; }

        public static CasGarbageCollectionResponse From(CasGarbageCollectionResult source)
        {
            source = source ?? new CasGarbageCollectionResult();
            return new CasGarbageCollectionResponse
            {
                Completed = source.Completed,
                DeletedBlobCount = source.DeletedBlobCount,
                DeletedStoredByteLength = source.DeletedStoredByteLength,
                Message = source.Message,
                DeleteFailures = (source.DeleteFailures ?? new CasHealthIssue[0])
                    .Select(CasHealthIssueDto.From).ToList(),
                Health = CasHealthResponse.From(source.Health)
            };
        }
    }
}
