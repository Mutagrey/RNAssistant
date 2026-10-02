using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace RNAssistant.Office.Contracts
{
    public static class ArtifactLibraryResourceClasses
    {
        public const string ImmutableOriginal = "immutable_original";
        public const string ImmutableSnapshot = "immutable_snapshot";
        public const string VersionedDocument = "versioned_document";
        public const string VersionedAggregate = "versioned_aggregate";
        public const string DerivedResource = "derived_resource";
    }

    public static class ArtifactLibraryGroups
    {
        public const string AuthoredDocuments = "authored_documents";
        public const string FilesMedia = "files_media";
        public const string GeneratedSnapshots = "generated_snapshots";
        public const string SystemEvidence = "system_evidence";
    }

    public sealed class ArtifactLibraryProjectionDto
    {
        [JsonProperty("sessionRevision")]
        public long SessionRevision { get; set; }

        [JsonProperty("heads")]
        public IReadOnlyList<ArtifactLibraryHeadDto> Heads { get; set; }

        [JsonProperty("removedResourceUris")]
        public IReadOnlyList<string> RemovedResourceUris { get; set; }

        [JsonProperty("totalHeads")] public int TotalHeads { get; set; }
        [JsonProperty("nextCursor")] public string NextCursor { get; set; }
    }

    public sealed class ArtifactLibraryHeadDto
    {
        [JsonProperty("canDetach")] public bool CanDetach { get; set; }
        [JsonProperty("availabilityIssue")] public string AvailabilityIssue { get; set; }
        [JsonProperty("artifactId")] public string ArtifactId { get; set; }
        [JsonProperty("logicalId")] public string LogicalId { get; set; }
        [JsonProperty("resourceClass")] public string ResourceClass { get; set; }
        [JsonProperty("group")] public string Group { get; set; }
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("displayKind")] public string DisplayKind { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("mimeType")] public string MimeType { get; set; }
        [JsonProperty("contentByteLength")] public long? ContentByteLength { get; set; }
        [JsonProperty("revision")] public int Revision { get; set; }
        [JsonProperty("versionLabel")] public string VersionLabel { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("resourceUri")] public string ResourceUri { get; set; }
        [JsonProperty("derivedFromResourceUri")] public string DerivedFromResourceUri { get; set; }
        [JsonProperty("sourceMessageId")] public string SourceMessageId { get; set; }
        [JsonProperty("runId")] public string RunId { get; set; }
        [JsonProperty("createdUtc")] public DateTime CreatedUtc { get; set; }
        [JsonProperty("historyCount")] public int HistoryCount { get; set; }
        [JsonProperty("history", NullValueHandling = NullValueHandling.Ignore)]
        public IReadOnlyList<ArtifactLibraryRevisionDto> History { get; set; }
    }

    public sealed class ArtifactLibraryPageRequest
    {
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("expectedSessionRevision")] public long ExpectedSessionRevision { get; set; }
        [JsonProperty("cursor")] public string Cursor { get; set; }
    }

    public sealed class ArtifactLibraryPageResponse
    {
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("sessionRevision")] public long SessionRevision { get; set; }
        [JsonProperty("heads")] public IReadOnlyList<ArtifactLibraryHeadDto> Heads { get; set; }
        [JsonProperty("artifacts")] public IReadOnlyList<ChatArtifactDto> Artifacts { get; set; }
        [JsonProperty("nextCursor")] public string NextCursor { get; set; }
    }

    public sealed class ArtifactLibraryHistoryRequest
    {
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("expectedSessionRevision")] public long ExpectedSessionRevision { get; set; }
        [JsonProperty("headArtifactId")] public string HeadArtifactId { get; set; }
        [JsonProperty("targetArtifactId")] public string TargetArtifactId { get; set; }
        [JsonProperty("cursor")] public string Cursor { get; set; }
    }

    public sealed class ArtifactLibraryHistoryResponse
    {
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("sessionRevision")] public long SessionRevision { get; set; }
        [JsonProperty("headArtifactId")] public string HeadArtifactId { get; set; }
        [JsonProperty("totalCount")] public int TotalCount { get; set; }
        [JsonProperty("items")] public IReadOnlyList<ArtifactLibraryRevisionDto> Items { get; set; }
        [JsonProperty("nextCursor")] public string NextCursor { get; set; }
    }

    public sealed class ArtifactLibraryRevisionDto
    {
        [JsonProperty("artifactId")] public string ArtifactId { get; set; }
        [JsonProperty("revision")] public int Revision { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("resourceUri")] public string ResourceUri { get; set; }
        [JsonProperty("parentArtifactId")] public string ParentArtifactId { get; set; }
        [JsonProperty("parentResourceUri")] public string ParentResourceUri { get; set; }
        [JsonProperty("restoredFromArtifactId")] public string RestoredFromArtifactId { get; set; }
        [JsonProperty("restoredFromResourceUri")] public string RestoredFromResourceUri { get; set; }
        [JsonProperty("sourceMessageId")] public string SourceMessageId { get; set; }
        [JsonProperty("runId")] public string RunId { get; set; }
        [JsonProperty("createdUtc")] public DateTime CreatedUtc { get; set; }
        [JsonProperty("relation")] public string Relation { get; set; }
        [JsonProperty("isHead")] public bool IsHead { get; set; }
        [JsonProperty("isOnActiveBranch")] public bool IsOnActiveBranch { get; set; }
    }
}
