using System;
using System.Collections.Generic;

namespace RNAssistant.Core.Models
{
    public static class CasHealthIssueKinds
    {
        public const string SourceUnreadable = "source_unreadable";
        public const string SourceInvalid = "source_invalid";
        public const string IncompleteTail = "incomplete_tail";
        public const string InvalidReference = "invalid_reference";
        public const string ReferenceConflict = "reference_conflict";
        public const string MissingBlob = "missing_blob";
        public const string CorruptBlob = "corrupt_blob";
        public const string InvalidBlobPath = "invalid_blob_path";
        public const string BlobUnreadable = "blob_unreadable";
    }

    public sealed class CasHealthIssue
    {
        public string Kind { get; set; }
        public string SourceType { get; set; }
        public string SourceId { get; set; }
        public string Location { get; set; }
        public string Sha256 { get; set; }
        public string Message { get; set; }
        public bool BlocksGarbageCollection { get; set; }
    }

    public sealed class CasOrphanBlob
    {
        public string Sha256 { get; set; }
        public long StoredByteLength { get; set; }
    }

    public sealed class CasHealthReport
    {
        public DateTime ScannedUtc { get; set; }
        public bool ReachabilityComplete { get; set; }
        public bool Healthy { get; set; }
        public bool CanGarbageCollect { get; set; }
        public int ChatStreamCount { get; set; }
        public int VbaJournalCount { get; set; }
        public int ReferenceOccurrenceCount { get; set; }
        public int ReferencedBlobCount { get; set; }
        public int StoredBlobCount { get; set; }
        public long StoredByteLength { get; set; }
        public int MissingBlobCount { get; set; }
        public int CorruptBlobCount { get; set; }
        public int OrphanBlobCount { get; set; }
        public long OrphanStoredByteLength { get; set; }
        public IReadOnlyList<CasHealthIssue> Issues { get; set; }
        public IReadOnlyList<CasOrphanBlob> OrphanBlobs { get; set; }

        public CasHealthReport()
        {
            Issues = new List<CasHealthIssue>();
            OrphanBlobs = new List<CasOrphanBlob>();
        }
    }

    public sealed class CasGarbageCollectionResult
    {
        public bool Completed { get; set; }
        public int DeletedBlobCount { get; set; }
        public long DeletedStoredByteLength { get; set; }
        public string Message { get; set; }
        public IReadOnlyList<CasHealthIssue> DeleteFailures { get; set; }
        public CasHealthReport Health { get; set; }

        public CasGarbageCollectionResult()
        {
            DeleteFailures = new List<CasHealthIssue>();
        }
    }
}
