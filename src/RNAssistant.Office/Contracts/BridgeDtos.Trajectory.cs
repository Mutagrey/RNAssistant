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
    public sealed class ChatEventPayloadRequest : ChatPayload
    {
        [JsonProperty("eventId")]
        public string EventId { get; set; }
    }

    public sealed class ChatTrajectoryRequest : ChatPayload
    {
        [JsonProperty("view")] public string View { get; set; }
        [JsonProperty("cursor")] public string Cursor { get; set; }
        [JsonProperty("pageSize")] public int? PageSize { get; set; }
        [JsonProperty("search")] public string Search { get; set; }
        [JsonProperty("minSequence")] public long? MinSequence { get; set; }
        [JsonProperty("maxSequence")] public long? MaxSequence { get; set; }
        [JsonProperty("eventTypes")] public List<string> EventTypes { get; set; }
        [JsonProperty("runId")] public string RunId { get; set; }
        [JsonProperty("turnId")] public string TurnId { get; set; }
        [JsonProperty("stepId")] public string StepId { get; set; }
        [JsonProperty("toolCallId")] public string ToolCallId { get; set; }
        [JsonProperty("artifactId")] public string ArtifactId { get; set; }
        [JsonProperty("resourceUri")] public string ResourceUri { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("visibility")] public string Visibility { get; set; }

        public TrajectoryQueryRequest ToQueryRequest()
        {
            return new TrajectoryQueryRequest
            {
                Cursor = Cursor,
                PageSize = PageSize.GetValueOrDefault(100),
                Search = Search,
                MinSequence = MinSequence,
                MaxSequence = MaxSequence,
                EventTypes = EventTypes ?? new List<string>(),
                RunId = RunId,
                TurnId = TurnId,
                StepId = StepId,
                ToolCallId = ToolCallId,
                ArtifactId = ArtifactId,
                ResourceUri = ResourceUri,
                Status = Status,
                Visibility = Visibility
            };
        }

        public TrajectoryViewQueryRequest ToViewQueryRequest(string view)
        {
            if (!string.IsNullOrWhiteSpace(ResourceUri))
            {
                throw new InvalidOperationException("resourceUri is available only for raw trajectory queries.");
            }
            return new TrajectoryViewQueryRequest
            {
                View = view,
                Cursor = Cursor,
                PageSize = PageSize.GetValueOrDefault(100),
                Search = Search,
                MinSequence = MinSequence,
                MaxSequence = MaxSequence,
                RunId = RunId,
                TurnId = TurnId,
                StepId = StepId,
                ToolCallId = ToolCallId,
                ArtifactId = ArtifactId,
                Status = Status
            };
        }
    }

    public sealed class ChatTrajectoryExportRequest : ChatPayload
    {
        [JsonProperty("view")] public string View { get; set; }
        [JsonProperty("search")] public string Search { get; set; }
        [JsonProperty("minSequence")] public long? MinSequence { get; set; }
        [JsonProperty("maxSequence")] public long? MaxSequence { get; set; }
        [JsonProperty("eventTypes")] public List<string> EventTypes { get; set; }
        [JsonProperty("runId")] public string RunId { get; set; }
        [JsonProperty("turnId")] public string TurnId { get; set; }
        [JsonProperty("stepId")] public string StepId { get; set; }
        [JsonProperty("toolCallId")] public string ToolCallId { get; set; }
        [JsonProperty("artifactId")] public string ArtifactId { get; set; }
        [JsonProperty("resourceUri")] public string ResourceUri { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("visibility")] public string Visibility { get; set; }
        [JsonProperty("redactionMode")] public string RedactionMode { get; set; }
        [JsonProperty("includeCasPayloads")] public bool? IncludeCasPayloads { get; set; }

        public TrajectoryExportRequest ToExportRequest()
        {
            return new TrajectoryExportRequest
            {
                View = View,
                Search = Search,
                MinSequence = MinSequence,
                MaxSequence = MaxSequence,
                EventTypes = EventTypes ?? new List<string>(),
                RunId = RunId,
                TurnId = TurnId,
                StepId = StepId,
                ToolCallId = ToolCallId,
                ArtifactId = ArtifactId,
                ResourceUri = ResourceUri,
                Status = Status,
                Visibility = Visibility,
                RedactionMode = RedactionMode,
                IncludeCasPayloads = IncludeCasPayloads == true
            };
        }
    }

    public sealed class ChatTrajectoryResponse
    {
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("revision")] public long Revision { get; set; }
        [JsonProperty("view")] public string View { get; set; }
        [JsonProperty("totalEvents")] public int TotalEvents { get; set; }
        [JsonProperty("totalRows")] public int TotalRows { get; set; }
        [JsonProperty("totalMatches")] public int TotalMatches { get; set; }
        [JsonProperty("cursor")] public string Cursor { get; set; }
        [JsonProperty("nextCursor")] public string NextCursor { get; set; }
        [JsonProperty("hasMore")] public bool HasMore { get; set; }
        [JsonProperty("events")] public IReadOnlyList<SessionEventDto> Events { get; set; }
        [JsonProperty("rows")] public IReadOnlyList<TrajectoryViewRowDto> Rows { get; set; }
    }

    public sealed class ChatTrajectoryExportResponse
    {
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("fileName")] public string FileName { get; set; }
        [JsonProperty("contentType")] public string ContentType { get; set; }
        [JsonProperty("base64")] public string Base64 { get; set; }
        [JsonProperty("bundleSha256")] public string BundleSha256 { get; set; }
        [JsonProperty("byteLength")] public long ByteLength { get; set; }
        [JsonProperty("uncompressedByteLength")] public long UncompressedByteLength { get; set; }
        [JsonProperty("redactionMode")] public string RedactionMode { get; set; }
        [JsonProperty("casPayloadsIncluded")] public bool CasPayloadsIncluded { get; set; }
        [JsonProperty("eventCount")] public int EventCount { get; set; }
        [JsonProperty("derivedRowCount")] public int DerivedRowCount { get; set; }
        [JsonProperty("referencedBlobCount")] public int ReferencedBlobCount { get; set; }
        [JsonProperty("includedBlobCount")] public int IncludedBlobCount { get; set; }

        public static ChatTrajectoryExportResponse From(string chatId, TrajectoryExportResult result)
        {
            if (result == null) return null;
            return new ChatTrajectoryExportResponse
            {
                ChatId = chatId,
                FileName = result.FileName,
                ContentType = result.ContentType,
                Base64 = Convert.ToBase64String(result.BundleBytes ?? new byte[0]),
                BundleSha256 = result.BundleSha256,
                ByteLength = result.BundleBytes == null ? 0 : result.BundleBytes.LongLength,
                UncompressedByteLength = result.UncompressedByteLength,
                RedactionMode = result.RedactionMode,
                CasPayloadsIncluded = result.CasPayloadsIncluded,
                EventCount = result.EventCount,
                DerivedRowCount = result.DerivedRowCount,
                ReferencedBlobCount = result.ReferencedBlobCount,
                IncludedBlobCount = result.IncludedBlobCount
            };
        }
    }

    public sealed class TrajectoryViewRowDto
    {
        private const int MaxInlineDataChars = 65536;

        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("view")] public string View { get; set; }
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("createdUtc")] public DateTime CreatedUtc { get; set; }
        [JsonProperty("completedUtc")] public DateTime? CompletedUtc { get; set; }
        [JsonProperty("durationMs")] public long? DurationMs { get; set; }
        [JsonProperty("firstSequence")] public long FirstSequence { get; set; }
        [JsonProperty("lastSequence")] public long LastSequence { get; set; }
        [JsonProperty("runId")] public string RunId { get; set; }
        [JsonProperty("turnId")] public string TurnId { get; set; }
        [JsonProperty("stepId")] public string StepId { get; set; }
        [JsonProperty("modelAttemptId")] public string ModelAttemptId { get; set; }
        [JsonProperty("toolCallId")] public string ToolCallId { get; set; }
        [JsonProperty("toolId")] public string ToolId { get; set; }
        [JsonProperty("mutationId")] public string MutationId { get; set; }
        [JsonProperty("journalRunId")] public string JournalRunId { get; set; }
        [JsonProperty("artifactId")] public string ArtifactId { get; set; }
        [JsonProperty("parentArtifactId")] public string ParentArtifactId { get; set; }
        [JsonProperty("resourceRefs")] public IReadOnlyList<ResourceRef> ResourceRefs { get; set; }
        [JsonProperty("attemptCount")] public int AttemptCount { get; set; }
        [JsonProperty("failureCount")] public int FailureCount { get; set; }
        [JsonProperty("promptTokens")] public int? PromptTokens { get; set; }
        [JsonProperty("completionTokens")] public int? CompletionTokens { get; set; }
        [JsonProperty("totalTokens")] public int? TotalTokens { get; set; }
        [JsonProperty("estimatedPromptTokens")] public int? EstimatedPromptTokens { get; set; }
        [JsonProperty("costUsd")] public decimal? CostUsd { get; set; }
        [JsonProperty("dataJson")] public string DataJson { get; set; }
        [JsonProperty("dataTruncated")] public bool DataTruncated { get; set; }
        [JsonProperty("sourceEventSeqs")] public IReadOnlyList<long> SourceEventSeqs { get; set; }
        [JsonProperty("sourceEventIds")] public IReadOnlyList<string> SourceEventIds { get; set; }

        public static TrajectoryViewRowDto From(TrajectoryViewRow row)
        {
            if (row == null) return null;
            var data = row.Data == null ? "{}" : row.Data.ToString(Formatting.None);
            var bounded = data.Length <= MaxInlineDataChars ? data : data.Substring(0, MaxInlineDataChars);
            return new TrajectoryViewRowDto
            {
                Id = row.Id, View = row.View, Kind = row.Kind, Title = row.Title, Status = row.Status,
                CreatedUtc = row.CreatedUtc, CompletedUtc = row.CompletedUtc, DurationMs = row.DurationMs,
                FirstSequence = row.FirstSequence, LastSequence = row.LastSequence,
                RunId = row.RunId, TurnId = row.TurnId, StepId = row.StepId,
                ModelAttemptId = row.ModelAttemptId,
                ToolCallId = row.ToolCallId, ToolId = row.ToolId,
                MutationId = row.MutationId, JournalRunId = row.JournalRunId,
                ArtifactId = row.ArtifactId, ParentArtifactId = row.ParentArtifactId,
                ResourceRefs = row.ResourceRefs ?? new List<ResourceRef>(),
                AttemptCount = row.AttemptCount, FailureCount = row.FailureCount,
                PromptTokens = row.PromptTokens, CompletionTokens = row.CompletionTokens, TotalTokens = row.TotalTokens,
                EstimatedPromptTokens = row.EstimatedPromptTokens, CostUsd = row.CostUsd,
                DataJson = bounded, DataTruncated = bounded.Length < data.Length,
                SourceEventSeqs = row.SourceEventSeqs ?? new List<long>(),
                SourceEventIds = row.SourceEventIds ?? new List<string>()
            };
        }
    }

    public sealed class SessionEventDto
    {
        private const int MaxInlineDataChars = 65536;

        [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonProperty("sequence")] public long Sequence { get; set; }
        [JsonProperty("eventId")] public string EventId { get; set; }
        [JsonProperty("createdUtc")] public System.DateTime CreatedUtc { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("runId")] public string RunId { get; set; }
        [JsonProperty("turnId")] public string TurnId { get; set; }
        [JsonProperty("stepId")] public string StepId { get; set; }
        [JsonProperty("previousHash")] public string PreviousHash { get; set; }
        [JsonProperty("hashAlgorithm")] public string HashAlgorithm { get; set; }
        [JsonProperty("protectionKeyId")] public string ProtectionKeyId { get; set; }
        [JsonProperty("hash")] public string Hash { get; set; }
        [JsonProperty("dataEncrypted")] public bool DataEncrypted { get; set; }
        [JsonProperty("dataJson")] public string DataJson { get; set; }
        [JsonProperty("dataTruncated")] public bool DataTruncated { get; set; }
        [JsonProperty("payloadSha256")] public string PayloadSha256 { get; set; }
        [JsonProperty("payloadByteLength")] public long? PayloadByteLength { get; set; }
        [JsonProperty("payloadContentType")] public string PayloadContentType { get; set; }
        [JsonProperty("payloadEncryption")] public string PayloadEncryption { get; set; }
        [JsonProperty("visibility")] public string Visibility { get; set; }
        [JsonProperty("sourceEventSeqs")] public IReadOnlyList<long> SourceEventSeqs { get; set; }
        [JsonProperty("sourceEventIds")] public IReadOnlyList<string> SourceEventIds { get; set; }
        [JsonProperty("toolCallIds")] public IReadOnlyList<string> ToolCallIds { get; set; }
        [JsonProperty("artifactIds")] public IReadOnlyList<string> ArtifactIds { get; set; }
        [JsonProperty("resourceRefs")] public IReadOnlyList<ResourceRef> ResourceRefs { get; set; }
        [JsonProperty("statuses")] public IReadOnlyList<string> Statuses { get; set; }

        public static SessionEventDto From(SessionEvent sessionEvent)
        {
            if (sessionEvent == null) return null;
            var data = sessionEvent.Data == null ? string.Empty : sessionEvent.Data.ToString(Formatting.None);
            var bounded = data.Length <= MaxInlineDataChars ? data : data.Substring(0, MaxInlineDataChars);
            return new SessionEventDto
            {
                SchemaVersion = sessionEvent.SchemaVersion,
                Sequence = sessionEvent.Sequence,
                EventId = sessionEvent.EventId,
                CreatedUtc = sessionEvent.CreatedUtc,
                Type = sessionEvent.Type,
                RunId = sessionEvent.RunId,
                TurnId = sessionEvent.TurnId,
                StepId = sessionEvent.StepId,
                PreviousHash = sessionEvent.PreviousHash,
                HashAlgorithm = sessionEvent.HashAlgorithm,
                ProtectionKeyId = sessionEvent.ProtectionKeyId,
                Hash = sessionEvent.Hash,
                DataEncrypted = !string.IsNullOrWhiteSpace(sessionEvent.EncryptedData),
                DataJson = bounded,
                DataTruncated = bounded.Length < data.Length,
                PayloadSha256 = sessionEvent.Payload == null ? null : sessionEvent.Payload.Sha256,
                PayloadByteLength = sessionEvent.Payload == null ? (long?)null : sessionEvent.Payload.ByteLength,
                PayloadContentType = sessionEvent.Payload == null ? null : sessionEvent.Payload.ContentType,
                PayloadEncryption = sessionEvent.Payload == null ? null : sessionEvent.Payload.Encryption
            };
        }

        public static SessionEventDto From(TrajectoryEventRecord record)
        {
            var dto = record == null ? null : From(record.Event);
            if (dto == null) return null;
            dto.Visibility = record.Visibility;
            dto.SourceEventSeqs = record.SourceEventSeqs ?? new List<long>();
            dto.SourceEventIds = record.SourceEventIds ?? new List<string>();
            dto.ToolCallIds = record.ToolCallIds ?? new List<string>();
            dto.ArtifactIds = record.ArtifactIds ?? new List<string>();
            dto.ResourceRefs = record.ResourceRefs ?? new List<ResourceRef>();
            dto.Statuses = record.Statuses ?? new List<string>();
            return dto;
        }
    }

    public sealed class ChatEventPayloadResponse
    {
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("eventId")] public string EventId { get; set; }
        [JsonProperty("sha256")] public string Sha256 { get; set; }
        [JsonProperty("byteLength")] public long ByteLength { get; set; }
        [JsonProperty("contentType")] public string ContentType { get; set; }
        [JsonProperty("text")] public string Text { get; set; }
        [JsonProperty("textTruncated")] public bool TextTruncated { get; set; }
    }
}
