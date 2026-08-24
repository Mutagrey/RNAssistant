using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    /// <summary>
    /// Builds CAS reachability only from fully validated canonical streams. Audit and Collect enter
    /// the supplied runtime maintenance gate so a blob write cannot race event append/GC.
    /// </summary>
    public sealed class CasMaintenanceService
    {
        private readonly AppDataPaths _paths;
        private readonly ChatStore _chatStore;
        private readonly VbaJournalStore _vbaJournalStore;
        private readonly ChatBlobStore _blobs;
        private readonly Func<IDisposable> _maintenanceLeaseProvider;
        private readonly Action _ensureQuiescent;

        public CasMaintenanceService(
            AppDataPaths paths,
            ChatStore chatStore,
            VbaJournalStore vbaJournalStore,
            Func<StorageProtector> protectionProvider,
            Func<IDisposable> maintenanceLeaseProvider,
            Action ensureQuiescent)
        {
            _paths = paths ?? throw new ArgumentNullException("paths");
            _chatStore = chatStore ?? throw new ArgumentNullException("chatStore");
            _vbaJournalStore = vbaJournalStore ?? throw new ArgumentNullException("vbaJournalStore");
            _blobs = new ChatBlobStore(paths, protectionProvider);
            _maintenanceLeaseProvider = maintenanceLeaseProvider ?? throw new ArgumentNullException("maintenanceLeaseProvider");
            _ensureQuiescent = ensureQuiescent ?? throw new ArgumentNullException("ensureQuiescent");
        }

        public CasHealthReport Audit()
        {
            using (_maintenanceLeaseProvider())
            {
                _ensureQuiescent();
                return AuditInternal().Report;
            }
        }

        public CasGarbageCollectionResult Collect()
        {
            using (_maintenanceLeaseProvider())
            {
                _ensureQuiescent();
                var scan = AuditInternal();
                if (!scan.Report.CanGarbageCollect)
                {
                    return new CasGarbageCollectionResult
                    {
                        Completed = false,
                        Message = "CAS garbage collection is blocked because reachability is incomplete.",
                        Health = scan.Report
                    };
                }

                var failures = new List<CasHealthIssue>();
                var deletedCount = 0;
                long deletedBytes = 0;
                foreach (var orphan in scan.OrphanCandidates)
                {
                    try
                    {
                        if (!_blobs.IsCanonicalPath(orphan.Path, orphan.Sha256))
                        {
                            failures.Add(DeleteFailure(orphan, "The orphan path is not a canonical CAS path."));
                            continue;
                        }
                        if (!File.Exists(orphan.Path))
                        {
                            failures.Add(DeleteFailure(orphan, "The orphan changed after the reachability scan."));
                            continue;
                        }
                        File.Delete(orphan.Path);
                        if (File.Exists(orphan.Path))
                        {
                            failures.Add(DeleteFailure(orphan, "The orphan blob still exists after deletion."));
                            continue;
                        }
                        deletedCount += 1;
                        deletedBytes += orphan.StoredByteLength;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        failures.Add(DeleteFailure(orphan, ex.Message));
                    }
                }

                var health = AuditInternal().Report;
                return new CasGarbageCollectionResult
                {
                    Completed = failures.Count == 0,
                    DeletedBlobCount = deletedCount,
                    DeletedStoredByteLength = deletedBytes,
                    Message = failures.Count == 0
                        ? "CAS garbage collection completed."
                        : "CAS garbage collection completed with deletion failures.",
                    DeleteFailures = failures,
                    Health = health
                };
            }
        }

        private CasAuditSnapshot AuditInternal()
        {
            var reachability = new CasReachabilityScan();
            _chatStore.ScanCasReferences(reachability);
            _vbaJournalStore.ScanCasReferences(reachability);

            var issues = new List<CasHealthIssue>(reachability.Issues);
            var stored = EnumerateStoredBlobs(issues);
            var storedByHash = stored.ToDictionary(item => item.Sha256, StringComparer.OrdinalIgnoreCase);
            var references = MergeReferences(reachability, issues);

            var missing = 0;
            var corrupt = 0;
            foreach (var reference in references.Values.OrderBy(item => item.Reference.Sha256, StringComparer.OrdinalIgnoreCase))
            {
                StoredCasBlob storedBlob;
                if (!storedByHash.TryGetValue(reference.Reference.Sha256, out storedBlob))
                {
                    missing += 1;
                    issues.Add(new CasHealthIssue
                    {
                        Kind = CasHealthIssueKinds.MissingBlob,
                        SourceType = reference.SourceType,
                        SourceId = reference.SourceId,
                        Location = reference.Location,
                        Sha256 = reference.Reference.Sha256,
                        Message = "A referenced CAS blob is missing."
                    });
                    continue;
                }
                if (_blobs.ReadBytes(reference.Reference) == null)
                {
                    corrupt += 1;
                    issues.Add(new CasHealthIssue
                    {
                        Kind = CasHealthIssueKinds.CorruptBlob,
                        SourceType = reference.SourceType,
                        SourceId = reference.SourceId,
                        Location = reference.Location,
                        Sha256 = reference.Reference.Sha256,
                        Message = "A referenced CAS blob is corrupt, unreadable, or protected with another key."
                    });
                }
            }

            var referencedHashes = new HashSet<string>(references.Keys, StringComparer.OrdinalIgnoreCase);
            var orphans = stored
                .Where(item => !referencedHashes.Contains(item.Sha256))
                .OrderBy(item => item.Sha256, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var reachabilityComplete = !issues.Any(item => item.BlocksGarbageCollection);
            var report = new CasHealthReport
            {
                ScannedUtc = DateTime.UtcNow,
                ReachabilityComplete = reachabilityComplete,
                Healthy = issues.Count == 0 && orphans.Count == 0,
                CanGarbageCollect = reachabilityComplete,
                ChatStreamCount = reachability.ChatStreamCount,
                VbaJournalCount = reachability.VbaJournalCount,
                ReferenceOccurrenceCount = reachability.References.Count,
                ReferencedBlobCount = references.Count,
                StoredBlobCount = stored.Count,
                StoredByteLength = stored.Sum(item => item.StoredByteLength),
                MissingBlobCount = missing,
                CorruptBlobCount = corrupt,
                OrphanBlobCount = orphans.Count,
                OrphanStoredByteLength = orphans.Sum(item => item.StoredByteLength),
                Issues = issues,
                OrphanBlobs = orphans.Select(item => new CasOrphanBlob
                {
                    Sha256 = item.Sha256,
                    StoredByteLength = item.StoredByteLength
                }).ToList()
            };
            return new CasAuditSnapshot { Report = report, OrphanCandidates = orphans };
        }

        private Dictionary<string, CasReferenceOccurrence> MergeReferences(
            CasReachabilityScan reachability,
            ICollection<CasHealthIssue> issues)
        {
            var result = new Dictionary<string, CasReferenceOccurrence>(StringComparer.OrdinalIgnoreCase);
            foreach (var occurrence in reachability.References)
            {
                CasReferenceOccurrence existing;
                if (!result.TryGetValue(occurrence.Reference.Sha256, out existing))
                {
                    result.Add(occurrence.Reference.Sha256, occurrence);
                    continue;
                }
                if (existing.Reference.ByteLength != occurrence.Reference.ByteLength)
                {
                    issues.Add(ReferenceConflict(occurrence,
                        "The same CAS hash is referenced with conflicting byte lengths."));
                    continue;
                }
                if (ProtectionConflicts(existing.Reference, occurrence.Reference))
                {
                    issues.Add(ReferenceConflict(occurrence,
                        "The same CAS hash is referenced with conflicting protection metadata."));
                    continue;
                }
                if (!HasProtectionMetadata(existing.Reference) && HasProtectionMetadata(occurrence.Reference))
                {
                    // Artifact/attachment fields retain hash+length only. Prefer a full event/journal
                    // reference when one exists so health also verifies its protection metadata.
                    result[occurrence.Reference.Sha256] = occurrence;
                }
            }
            return result;
        }

        private static CasHealthIssue ReferenceConflict(CasReferenceOccurrence occurrence, string message)
        {
            return new CasHealthIssue
            {
                Kind = CasHealthIssueKinds.ReferenceConflict,
                SourceType = occurrence.SourceType,
                SourceId = occurrence.SourceId,
                Location = occurrence.Location,
                Sha256 = occurrence.Reference.Sha256,
                Message = message,
                BlocksGarbageCollection = true
            };
        }

        private static bool ProtectionConflicts(ChatBlobReference left, ChatBlobReference right)
        {
            if (left == null || right == null) return false;
            var encryptionConflict = !string.IsNullOrWhiteSpace(left.Encryption) &&
                !string.IsNullOrWhiteSpace(right.Encryption) &&
                !string.Equals(left.Encryption, right.Encryption, StringComparison.OrdinalIgnoreCase);
            var keyConflict = !string.IsNullOrWhiteSpace(left.ProtectionKeyId) &&
                !string.IsNullOrWhiteSpace(right.ProtectionKeyId) &&
                !string.Equals(left.ProtectionKeyId, right.ProtectionKeyId, StringComparison.OrdinalIgnoreCase);
            return encryptionConflict || keyConflict;
        }

        private static bool HasProtectionMetadata(ChatBlobReference reference)
        {
            return reference != null && (!string.IsNullOrWhiteSpace(reference.Encryption) ||
                !string.IsNullOrWhiteSpace(reference.ProtectionKeyId));
        }

        private List<StoredCasBlob> EnumerateStoredBlobs(ICollection<CasHealthIssue> issues)
        {
            string[] paths;
            try
            {
                paths = Directory.Exists(_paths.ChatBlobDirectory)
                    ? Directory.GetFiles(_paths.ChatBlobDirectory, "*.blob", SearchOption.AllDirectories)
                    : new string[0];
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                issues.Add(new CasHealthIssue
                {
                    Kind = CasHealthIssueKinds.BlobUnreadable,
                    SourceType = "cas",
                    SourceId = "chat-blobs",
                    Message = "The CAS directory could not be enumerated: " + ex.Message,
                    BlocksGarbageCollection = true
                });
                return new List<StoredCasBlob>();
            }

            var result = new List<StoredCasBlob>();
            foreach (var path in paths)
            {
                var fileName = Path.GetFileNameWithoutExtension(path);
                if (!ChatBlobStore.ValidSha256(fileName) || !_blobs.IsCanonicalPath(path, fileName))
                {
                    issues.Add(new CasHealthIssue
                    {
                        Kind = CasHealthIssueKinds.InvalidBlobPath,
                        SourceType = "cas",
                        SourceId = RelativePath(_paths.ChatBlobDirectory, path),
                        Message = "A .blob file is outside the canonical SHA-256 CAS layout."
                    });
                    continue;
                }
                try
                {
                    result.Add(new StoredCasBlob
                    {
                        Sha256 = fileName.ToLowerInvariant(),
                        Path = path,
                        StoredByteLength = new FileInfo(path).Length
                    });
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    issues.Add(new CasHealthIssue
                    {
                        Kind = CasHealthIssueKinds.BlobUnreadable,
                        SourceType = "cas",
                        SourceId = RelativePath(_paths.ChatBlobDirectory, path),
                        Sha256 = fileName,
                        Message = "CAS blob metadata could not be read: " + ex.Message,
                        BlocksGarbageCollection = true
                    });
                }
            }
            return result;
        }

        private static CasHealthIssue DeleteFailure(StoredCasBlob orphan, string message)
        {
            return new CasHealthIssue
            {
                Kind = CasHealthIssueKinds.BlobUnreadable,
                SourceType = "cas",
                SourceId = "chat-blobs",
                Sha256 = orphan == null ? null : orphan.Sha256,
                Message = message
            };
        }

        internal static string RelativePath(string root, string path)
        {
            try
            {
                var rootUri = new Uri(AppendDirectorySeparator(Path.GetFullPath(root)));
                var pathUri = new Uri(Path.GetFullPath(path));
                return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString())
                    .Replace('/', Path.DirectorySeparatorChar);
            }
            catch
            {
                return Path.GetFileName(path ?? string.Empty);
            }
        }

        private static string AppendDirectorySeparator(string path)
        {
            if (string.IsNullOrEmpty(path) || path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)) return path;
            return path + Path.DirectorySeparatorChar;
        }

        private sealed class CasAuditSnapshot
        {
            public CasHealthReport Report { get; set; }
            public List<StoredCasBlob> OrphanCandidates { get; set; }
        }
    }

    internal sealed class CasReachabilityScan
    {
        public int ChatStreamCount { get; set; }
        public int VbaJournalCount { get; set; }
        public List<CasReferenceOccurrence> References { get; private set; }
        public List<CasHealthIssue> Issues { get; private set; }

        public CasReachabilityScan()
        {
            References = new List<CasReferenceOccurrence>();
            Issues = new List<CasHealthIssue>();
        }

        public void AddReference(ChatBlobReference reference, string sourceType, string sourceId, string location)
        {
            if (reference == null) return;
            if (!ChatBlobStore.ValidReference(reference))
            {
                Issues.Add(new CasHealthIssue
                {
                    Kind = CasHealthIssueKinds.InvalidReference,
                    SourceType = sourceType,
                    SourceId = sourceId,
                    Location = location,
                    Sha256 = reference.Sha256,
                    Message = "The event stream contains an invalid CAS reference.",
                    BlocksGarbageCollection = true
                });
                return;
            }
            References.Add(new CasReferenceOccurrence
            {
                Reference = new ChatBlobReference
                {
                    Sha256 = reference.Sha256.ToLowerInvariant(),
                    ByteLength = reference.ByteLength,
                    ContentType = reference.ContentType,
                    Encryption = reference.Encryption,
                    ProtectionKeyId = reference.ProtectionKeyId
                },
                SourceType = sourceType,
                SourceId = sourceId,
                Location = location
            });
        }

        public void AddTokenReferences(JToken token, string sourceType, string sourceId, string location)
        {
            if (token == null) return;
            var root = token as JObject;
            if (root != null)
            {
                AddPair(root, "Sha256", "ByteLength", null, sourceType, sourceId, location);
                AddPair(root, "ContentSha256", "ContentByteLength", "MimeType", sourceType, sourceId, location);
                AddPair(root, "ExtractedTextSha256", "ExtractedTextByteLength", null, sourceType, sourceId, location);
            }
            foreach (var child in token.Children())
            {
                AddTokenReferences(child, sourceType, sourceId,
                    string.IsNullOrWhiteSpace(child.Path) ? location : location + "." + child.Path);
            }
        }

        public void AddSourceIssue(string kind, string sourceType, string sourceId, string message)
        {
            Issues.Add(new CasHealthIssue
            {
                Kind = kind,
                SourceType = sourceType,
                SourceId = sourceId,
                Message = message,
                BlocksGarbageCollection = true
            });
        }

        private void AddPair(
            JObject value,
            string hashProperty,
            string lengthProperty,
            string contentTypeProperty,
            string sourceType,
            string sourceId,
            string location)
        {
            var hashToken = value[hashProperty];
            var lengthToken = value[lengthProperty];
            if (hashToken == null || hashToken.Type == JTokenType.Null || string.IsNullOrWhiteSpace((string)hashToken)) return;
            var reference = new ChatBlobReference
            {
                Sha256 = (string)hashToken,
                ByteLength = lengthToken != null && lengthToken.Type == JTokenType.Integer ? (long)lengthToken : -1,
                ContentType = contentTypeProperty == null ? null : (string)value[contentTypeProperty],
                Encryption = (string)value["Encryption"],
                ProtectionKeyId = (string)value["ProtectionKeyId"]
            };
            AddReference(reference, sourceType, sourceId, location + "." + hashProperty);
        }
    }

    internal sealed class CasReferenceOccurrence
    {
        public ChatBlobReference Reference { get; set; }
        public string SourceType { get; set; }
        public string SourceId { get; set; }
        public string Location { get; set; }
    }

    internal sealed class StoredCasBlob
    {
        public string Sha256 { get; set; }
        public string Path { get; set; }
        public long StoredByteLength { get; set; }
    }
}
