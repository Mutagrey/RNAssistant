using System.Collections.Generic;

namespace RNAssistant.Core.Models
{
    public static class TrajectoryVisibility
    {
        public const string Current = "current";
        public const string Shadowed = "shadowed";
        public const string LogOnly = "log-only";

        public static bool IsValid(string value)
        {
            return string.IsNullOrWhiteSpace(value) ||
                string.Equals(value, Current, System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, Shadowed, System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, LogOnly, System.StringComparison.OrdinalIgnoreCase);
        }
    }

    public sealed class TrajectoryQueryRequest
    {
        public string Cursor { get; set; }
        public int PageSize { get; set; }
        public string Search { get; set; }
        public long? MinSequence { get; set; }
        public long? MaxSequence { get; set; }
        public List<string> EventTypes { get; set; }
        public string RunId { get; set; }
        public string TurnId { get; set; }
        public string StepId { get; set; }
        public string ToolCallId { get; set; }
        public string ArtifactId { get; set; }
        public string ResourceUri { get; set; }
        public string Status { get; set; }
        public string Visibility { get; set; }

        public TrajectoryQueryRequest()
        {
            PageSize = 100;
            EventTypes = new List<string>();
        }
    }

    public sealed class TrajectoryEventRecord
    {
        public SessionEvent Event { get; set; }
        public string Visibility { get; set; }
        public List<long> SourceEventSeqs { get; set; }
        public List<string> SourceEventIds { get; set; }
        public List<string> ToolCallIds { get; set; }
        public List<string> ArtifactIds { get; set; }
        public List<ResourceRef> ResourceRefs { get; set; }
        public List<string> Statuses { get; set; }

        public TrajectoryEventRecord()
        {
            SourceEventSeqs = new List<long>();
            SourceEventIds = new List<string>();
            ToolCallIds = new List<string>();
            ArtifactIds = new List<string>();
            ResourceRefs = new List<ResourceRef>();
            Statuses = new List<string>();
        }
    }

    public sealed class TrajectoryQueryPage
    {
        public int TotalEvents { get; set; }
        public int TotalMatches { get; set; }
        public string Cursor { get; set; }
        public string NextCursor { get; set; }
        public bool HasMore { get; set; }
        public List<TrajectoryEventRecord> Records { get; set; }

        public TrajectoryQueryPage()
        {
            Records = new List<TrajectoryEventRecord>();
        }
    }

    public static class TrajectoryViews
    {
        public const string Raw = "raw";
        public const string ModelReplay = "model-replay";
        public const string ToolExecution = "tool-execution";
        public const string ArtifactLineage = "artifact-lineage";
        public const string ConfirmationPauses = "confirmation-pauses";
        public const string FailureRetries = "failure-retries";
        public const string TurnUsage = "turn-usage";

        public static bool IsSupported(string value)
        {
            return string.Equals(value, Raw, System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, ModelReplay, System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, ToolExecution, System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, ArtifactLineage, System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, ConfirmationPauses, System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, FailureRetries, System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, TurnUsage, System.StringComparison.OrdinalIgnoreCase);
        }

        public static string Normalize(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            return IsSupported(value) ? value : Raw;
        }
    }

    public sealed class TrajectoryViewQueryRequest
    {
        public string View { get; set; }
        public string Cursor { get; set; }
        public int PageSize { get; set; }
        public string Search { get; set; }
        public long? MinSequence { get; set; }
        public long? MaxSequence { get; set; }
        public string RunId { get; set; }
        public string TurnId { get; set; }
        public string StepId { get; set; }
        public string ToolCallId { get; set; }
        public string ArtifactId { get; set; }
        public string Status { get; set; }

        public TrajectoryViewQueryRequest()
        {
            View = TrajectoryViews.ModelReplay;
            PageSize = 100;
        }
    }

    public sealed class TrajectoryViewRow
    {
        public string Id { get; set; }
        public string View { get; set; }
        public string Kind { get; set; }
        public string Title { get; set; }
        public string Status { get; set; }
        public System.DateTime CreatedUtc { get; set; }
        public System.DateTime? CompletedUtc { get; set; }
        public long? DurationMs { get; set; }
        public long FirstSequence { get; set; }
        public long LastSequence { get; set; }
        public string RunId { get; set; }
        public string TurnId { get; set; }
        public string StepId { get; set; }
        public string ToolCallId { get; set; }
        public string ToolId { get; set; }
        public string ArtifactId { get; set; }
        public string ParentArtifactId { get; set; }
        public List<ResourceRef> ResourceRefs { get; set; }
        public int AttemptCount { get; set; }
        public int FailureCount { get; set; }
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
        public int? TotalTokens { get; set; }
        public int? EstimatedPromptTokens { get; set; }
        public decimal? CostUsd { get; set; }
        public Newtonsoft.Json.Linq.JObject Data { get; set; }
        public List<long> SourceEventSeqs { get; set; }
        public List<string> SourceEventIds { get; set; }

        public TrajectoryViewRow()
        {
            Data = new Newtonsoft.Json.Linq.JObject();
            SourceEventSeqs = new List<long>();
            SourceEventIds = new List<string>();
            ResourceRefs = new List<ResourceRef>();
        }
    }

    public sealed class TrajectoryViewPage
    {
        public string View { get; set; }
        public int TotalEvents { get; set; }
        public int TotalRows { get; set; }
        public int TotalMatches { get; set; }
        public string Cursor { get; set; }
        public string NextCursor { get; set; }
        public bool HasMore { get; set; }
        public List<TrajectoryViewRow> Rows { get; set; }

        public TrajectoryViewPage()
        {
            Rows = new List<TrajectoryViewRow>();
        }
    }

    public static class TrajectoryExportRedactionModes
    {
        public const string Metadata = "metadata";
        public const string Secrets = "secrets";
        public const string None = "none";

        public static bool IsValid(string value)
        {
            return string.Equals(value, Metadata, System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, Secrets, System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, None, System.StringComparison.OrdinalIgnoreCase);
        }

        public static string Normalize(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            return IsValid(value) ? value : Metadata;
        }
    }

    public sealed class TrajectoryExportRequest
    {
        public string View { get; set; }
        public string Search { get; set; }
        public long? MinSequence { get; set; }
        public long? MaxSequence { get; set; }
        public List<string> EventTypes { get; set; }
        public string RunId { get; set; }
        public string TurnId { get; set; }
        public string StepId { get; set; }
        public string ToolCallId { get; set; }
        public string ArtifactId { get; set; }
        public string ResourceUri { get; set; }
        public string Status { get; set; }
        public string Visibility { get; set; }
        public string RedactionMode { get; set; }
        public bool IncludeCasPayloads { get; set; }

        public TrajectoryExportRequest()
        {
            View = TrajectoryViews.Raw;
            EventTypes = new List<string>();
            RedactionMode = TrajectoryExportRedactionModes.Metadata;
        }
    }

    public sealed class TrajectoryExportResult
    {
        public string FileName { get; set; }
        public string ContentType { get; set; }
        public byte[] BundleBytes { get; set; }
        public string BundleSha256 { get; set; }
        public string RedactionMode { get; set; }
        public bool CasPayloadsIncluded { get; set; }
        public int EventCount { get; set; }
        public int DerivedRowCount { get; set; }
        public int ReferencedBlobCount { get; set; }
        public int IncludedBlobCount { get; set; }
        public long UncompressedByteLength { get; set; }

        public TrajectoryExportResult()
        {
            ContentType = "application/zip";
            BundleBytes = new byte[0];
        }
    }
}
