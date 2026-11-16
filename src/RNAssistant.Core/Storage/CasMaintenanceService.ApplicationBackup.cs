using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    public sealed class ApplicationBackupResult
    {
        public string Format { get; set; }
        public string Destination { get; set; }
        public int FileCount { get; set; }
        public long ByteLength { get; set; }
        public IReadOnlyList<string> ExternalWorkspaceRoots { get; set; }
    }

    public sealed class ApplicationRestoreResult
    {
        public string Format { get; set; }
        public string Destination { get; set; }
        public int FileCount { get; set; }
        public long ByteLength { get; set; }
        public IReadOnlyList<string> ExternalWorkspaceRoots { get; set; }
        public IReadOnlyList<string> MissingExternalWorkspaceRoots { get; set; }
        public bool IndexRebuilt { get; set; }
        public bool RestartRequired { get; set; }
        public AppDataPaths RestoredPaths { get; set; }
    }

    // A complete profile snapshot over the existing storage owners. The application
    // root remains separate from external workspaces, live documents and WebView state.
    public sealed partial class CasMaintenanceService
    {
        public const string ApplicationBackupFormat = "rnassistant-application-backup-v3";
        private static readonly string[] ApplicationRootFiles = {
            "settings.json", "secret.bin", "history-secret.bin", "history-protection.salt",
            "chat-drafts.bin", "native-window-state.json"
        };
        private static readonly string[] ApplicationTreeRoots = {
            "assets", "html-projects", "plugin-workspaces", "prompt-backups", "plugin-icons",
            "tools", "skills", "vendors", "attachments"
        };
        private static readonly string[] ExcludedRootDirectories = {
            "locks", "webview", "logs", "plugin-staging", "plugin-preview-data", "plugin-results"
        };

        public ApplicationBackupResult BackupApplication(string destination)
        {
            RequireDestinationOutside(destination, _paths.Root, "Application backup destination must be outside the live data root.");
            using (_maintenanceLeaseProvider())
            using (PluginStore.AcquireRuntimeUse(_paths, true))
            {
                _ensureQuiescent();
                var workspaces = new WorkspaceStore(_paths);
                var documents = new DocumentAuthorityRegistry(_paths);
                using (workspaces.AcquireBackupLease())
                using (documents.AcquireBackupLease())
                {
                    var ownerFiles = workspaces.GetBackupFiles(out var workspaceRoots)
                        .Concat(documents.ValidateBackupState()).ToArray();
                    var snapshot = AuditInternal();
                    RequireApplicationClosure(snapshot.Report, "Backup");
                    var protector = _protectionProvider();
                    RequireBackupProtectionProfile(_paths, protector);
                    var scopes = snapshot.JournalStreams.Select(stream => stream.Identity).ToList();
                    IReadOnlyList<MutationAttempt> moves = new MutationAttempt[0];
                    if (snapshot.JournalStreams.Any(stream => stream.Resources))
                        scopes.AddRange(new ResourceJournal(_paths, protector).ReadBackupIdentityReferences(out moves));
                    workspaces.ValidateBackupScopes(scopes);
                    WorkspaceFileService.ValidateBackupScopes(_paths, scopes);
                    WorkspaceFileService.ValidateBackupMoveIntents(_paths, moves);

                    var sources = ApplicationBackupSources(snapshot, ownerFiles);
                    var manifest = new ApplicationBackupManifest
                    {
                        Format = ApplicationBackupFormat,
                        CreatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                        HashAlgorithm = BackupHashAlgorithm(protector),
                        ProtectionKeyId = protector.KeyId,
                        Encryption = protector.EncryptionMode,
                        WorkspaceRoots = workspaceRoots.ToList()
                    };
                    var staging = CreateStaging(destination);
                    try
                    {
                        foreach (var source in sources.OrderBy(path => path, StringComparer.Ordinal))
                        {
                            var relative = RelativePath(_paths.Root, source).Replace('\\', '/');
                            var copied = CopyBackupFile(source, SafeApplicationBackupPath(Path.Combine(staging, "data"), relative));
                            copied.Path = relative;
                            manifest.Files.Add(copied);
                        }
                        var copiedPaths = AppDataPaths.CreateForRoot(Path.Combine(staging, "data"));
                        RequireBackupProtectionProfile(copiedPaths, protector);
                        var copiedWorkspaces = new WorkspaceStore(copiedPaths);
                        copiedWorkspaces.GetBackupFiles();
                        new DocumentAuthorityRegistry(copiedPaths).ValidateBackupState();
                        copiedWorkspaces.ValidateBackupScopes(scopes);
                        WorkspaceFileService.ValidateBackupScopes(copiedPaths, scopes);
                        WorkspaceFileService.ValidateBackupMoveIntents(copiedPaths, moves);
                        var current = _protectionProvider();
                        if (current.KeyId != protector.KeyId || current.EncryptionMode != protector.EncryptionMode ||
                            current.IntegrityMode != protector.IntegrityMode)
                            throw new InvalidOperationException("History protection changed during application backup.");
                        manifest.Digest = ApplicationBackupDigest(manifest, protector);
                        using (var file = new FileStream(Path.Combine(staging, "backup.json"), FileMode.CreateNew,
                            FileAccess.Write, FileShare.None))
                        {
                            WriteApplicationBackupManifest(file, manifest);
                            file.Flush(true);
                        }
                        Directory.Move(staging, Path.GetFullPath(destination));
                        return new ApplicationBackupResult
                        {
                            Format = manifest.Format,
                            Destination = Path.GetFullPath(destination),
                            FileCount = manifest.Files.Count,
                            ByteLength = manifest.Files.Sum(file => file.Bytes),
                            ExternalWorkspaceRoots = manifest.WorkspaceRoots.ToArray()
                        };
                    }
                    finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
                }
            }
        }

        public static ApplicationRestoreResult RestoreApplication(string backup, string destination,
            Func<AppDataPaths, StorageProtector> protectionProvider)
        {
            if (protectionProvider == null) throw new ArgumentNullException(nameof(protectionProvider));
            RequireDestinationOutside(destination, backup, "Restore destination must be outside the backup folder.");
            if (StorageFileSystem.IsReparsePoint(backup) || StorageFileSystem.IsReparsePoint(Path.Combine(backup, "backup.json")))
                throw new InvalidDataException("Application backup cannot be a link.");
            var manifest = ReadApplicationBackupManifest(backup);
            var staging = CreateStaging(destination);
            try
            {
                foreach (var expected in manifest.Files)
                {
                    var copied = CopyBackupFile(SafeApplicationBackupPath(Path.Combine(backup, "data"), expected.Path),
                        SafeApplicationBackupPath(staging, expected.Path));
                    if (copied.Bytes != expected.Bytes || copied.Sha256 != expected.Sha256)
                        throw new InvalidDataException("Application backup file checksum differs from its manifest.");
                }
                var paths = AppDataPaths.CreateForRoot(staging);
                var protector = protectionProvider(paths) ?? throw new InvalidOperationException("Application backup protection is unavailable.");
                if (manifest.ProtectionKeyId != protector.KeyId || manifest.HashAlgorithm != BackupHashAlgorithm(protector) ||
                    manifest.Encryption != protector.EncryptionMode || manifest.Digest != ApplicationBackupDigest(manifest, protector))
                    throw new InvalidDataException("Application backup identity/authentication failed.");
                ValidateRestoredApplication(paths, protector);
                var missingRoots = manifest.WorkspaceRoots.Where(root => !Directory.Exists(root)).ToArray();
                Directory.Move(staging, Path.GetFullPath(destination));
                return new ApplicationRestoreResult
                {
                    Format = manifest.Format,
                    Destination = Path.GetFullPath(destination),
                    FileCount = manifest.Files.Count,
                    ByteLength = manifest.Files.Sum(file => file.Bytes),
                    ExternalWorkspaceRoots = manifest.WorkspaceRoots.ToArray(),
                    MissingExternalWorkspaceRoots = missingRoots,
                    IndexRebuilt = true,
                    RestartRequired = true,
                    RestoredPaths = AppDataPaths.CreateForRoot(Path.GetFullPath(destination))
                };
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        }

        private List<string> ApplicationBackupSources(CasAuditSnapshot snapshot, string[] ownerFiles)
        {
            var sources = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in Directory.EnumerateFileSystemEntries(_paths.Root))
            {
                if (Directory.Exists(entry))
                {
                    var name = Path.GetFileName(entry);
                    if (!ApplicationTreeRoots.Contains(name, StringComparer.Ordinal) &&
                        !new[] { "chats", "vba-journals", "resource-authority", "chat-blobs", "storage-v2", "workspaces" }
                            .Contains(name, StringComparer.Ordinal) && !ExcludedRootDirectories.Contains(name, StringComparer.Ordinal))
                        throw new InvalidDataException("Unknown application data directory cannot be backed up: " + name);
                    continue;
                }
                var fileName = Path.GetFileName(entry);
                if (fileName == "plugins.use" || IsTransientFileName(fileName)) continue;
                AddApplicationSource(sources, entry);
            }
            foreach (var directory in ApplicationTreeRoots)
                AddApplicationTree(sources, Path.Combine(_paths.Root, directory));
            foreach (var directory in new[] { _paths.ChatDirectory, _paths.VbaJournalDirectory,
                _paths.ResourceAuthorityDirectory, _paths.ChatBlobDirectory, _paths.JournalStorageDirectory })
                AddApplicationTree(sources, directory);
            foreach (var source in snapshot.JournalStreams.SelectMany(stream => stream.Segments)
                .Concat(snapshot.References.Select(reference => _blobs.PathFor(reference.Sha256)))
                .Concat(snapshot.OrphanCandidates.Select(blob => blob.Path))
                .Concat(ownerFiles))
                AddApplicationSource(sources, source);
            return sources.ToList();
        }

        private void AddApplicationTree(ICollection<string> sources, string root)
        {
            if (!Directory.Exists(root)) return;
            var paths = StorageFileSystem.GetFilesRecursive(root, "*", (path, message) =>
            {
                throw new InvalidDataException("Application backup could not read " + path + ": " + message);
            });
            foreach (var path in paths)
            {
                var name = Path.GetFileName(path);
                var relative = RelativePath(_paths.Root, path).Replace('\\', '/');
                if (IsTransientFileName(name) || IsTransientApplicationPath(relative)) continue;
                AddApplicationSource(sources, path);
            }
        }

        private void AddApplicationSource(ICollection<string> sources, string path)
        {
            if (!File.Exists(path) || Directory.Exists(path) || StorageFileSystem.IsReparsePoint(path))
                throw new InvalidDataException("Application backup source is unavailable or linked: " + path);
            var relative = RelativePath(_paths.Root, path).Replace('\\', '/');
            SafeApplicationBackupPath(_paths.Root, relative);
            sources.Add(Path.GetFullPath(path));
        }

        private static void RequireApplicationClosure(CasHealthReport report, string operation)
        {
            if (report == null || !report.ReachabilityComplete || !report.CanGarbageCollect ||
                report.MissingBlobCount != 0 || report.CorruptBlobCount != 0 ||
                (report.Issues != null && report.Issues.Count != 0))
                throw new InvalidDataException(operation + " requires complete, validated legacy and native application data.");
        }

        private static void ValidateRestoredApplication(AppDataPaths paths, StorageProtector protector)
        {
            RequireBackupProtectionProfile(paths, protector);
            new SettingsService(paths).Load();
            var workspaces = new WorkspaceStore(paths);
            workspaces.GetBackupFiles();
            new DocumentAuthorityRegistry(paths).ValidateBackupState();
            var service = new CasMaintenanceService(paths, new ChatStore(paths, () => protector),
                new VbaJournalStore(paths, () => protector), () => protector,
                () => EmptyBackupLease.Instance, () => { });
            var snapshot = service.AuditInternal();
            RequireApplicationClosure(snapshot.Report, "Restore");
            var scopes = snapshot.JournalStreams.Select(stream => stream.Identity).ToList();
            IReadOnlyList<MutationAttempt> moves = new MutationAttempt[0];
            if (snapshot.JournalStreams.Any(stream => stream.Resources))
                scopes.AddRange(new ResourceJournal(paths, protector).ReadBackupIdentityReferences(out moves));
            workspaces.ValidateBackupScopes(scopes);
            WorkspaceFileService.ValidateBackupScopes(paths, scopes);
            WorkspaceFileService.ValidateBackupMoveIntents(paths, moves);
            JournalIndex.Repair(paths, protector, true, CancellationToken.None);
        }

        private static ApplicationBackupManifest ReadApplicationBackupManifest(string backup)
        {
            ApplicationBackupManifest manifest;
            using (var file = File.OpenText(Path.Combine(backup, "backup.json")))
            using (var reader = new JsonTextReader(file) { MaxDepth = 24, DateParseHandling = DateParseHandling.None })
            {
                var root = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                var fields = new[] { "Format", "CreatedUtc", "HashAlgorithm", "Encryption", "ProtectionKeyId", "WorkspaceRoots", "Files", "Digest" };
                if (root.Properties().Count() != fields.Length || fields.Any(field => root.Property(field) == null))
                    throw new InvalidDataException("Application backup manifest has missing or unknown fields.");
                if (new[] { "Format", "CreatedUtc", "HashAlgorithm", "Encryption", "Digest" }
                    .Any(field => root[field].Type != JTokenType.String))
                    throw new InvalidDataException("Application backup manifest has an invalid string field.");
                if (root["ProtectionKeyId"].Type != JTokenType.String && root["ProtectionKeyId"].Type != JTokenType.Null)
                    throw new InvalidDataException("Application backup manifest has an invalid protection identity.");
                if (root["WorkspaceRoots"].Type != JTokenType.Array || root["Files"].Type != JTokenType.Array ||
                    ((JArray)root["WorkspaceRoots"]).Any(item => item.Type != JTokenType.String))
                    throw new InvalidDataException("Application backup manifest has an invalid collection.");
                if (root["Files"].Any(token => token.Type != JTokenType.Object || token["Path"]?.Type != JTokenType.String ||
                    token["Bytes"]?.Type != JTokenType.Integer || token["Sha256"]?.Type != JTokenType.String ||
                    ((JObject)token).Properties().Count() != 3))
                    throw new InvalidDataException("Application backup manifest has invalid file metadata.");
                manifest = root.ToObject<ApplicationBackupManifest>(JsonSerializer.Create(
                    new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error }));
                if (reader.Read()) throw new InvalidDataException("Trailing application backup manifest data.");
            }
            DateTime created;
            if (manifest?.Format != ApplicationBackupFormat || manifest.Files == null || manifest.WorkspaceRoots == null ||
                manifest.Files.Any(file => file == null || file.Bytes < 0 || !LowerHex(file.Sha256, 64)) ||
                manifest.Files.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() != manifest.Files.Count ||
                manifest.Files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count ||
                manifest.WorkspaceRoots.Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.WorkspaceRoots.Count ||
                manifest.WorkspaceRoots.Any(root => !WorkspaceStore.BackupAbsolutePath(root)) ||
                !DateTime.TryParseExact(manifest.CreatedUtc, "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out created) || !LowerHex(manifest.Digest, 64))
                throw new InvalidDataException("Unsupported or ambiguous application backup manifest.");
            foreach (var file in manifest.Files)
                SafeApplicationBackupPath(Path.Combine(backup, "data"), file.Path);
            var dataRoot = Path.Combine(backup, "data");
            if (!Directory.Exists(dataRoot)) throw new InvalidDataException("Application backup data directory is unavailable.");
            var actual = StorageFileSystem.GetFilesRecursive(dataRoot, "*", (path, message) =>
            {
                throw new InvalidDataException("Application backup could not read " + path + ": " + message);
            }).Select(path => RelativePath(dataRoot, path).Replace('\\', '/')).ToArray();
            var expectedPaths = new HashSet<string>(manifest.Files.Select(file => file.Path), StringComparer.Ordinal);
            if (actual.Length != expectedPaths.Count || actual.Any(path => !expectedPaths.Contains(path)))
                throw new InvalidDataException("Application backup contains files outside its manifest.");
            return manifest;
        }

        private static string SafeApplicationBackupPath(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || relative.IndexOf('\\') >= 0 ||
                relative.IndexOf(':') >= 0 || relative.IndexOf('\0') >= 0 || Path.IsPathRooted(relative))
                throw new InvalidDataException("Invalid application backup relative path.");
            var parts = relative.Split('/');
            if (parts.Any(part => !SafeApplicationPathComponent(part))) throw new InvalidDataException("Invalid application backup path component.");
            var allowed = parts.Length == 1 && ApplicationRootFiles.Contains(relative, StringComparer.Ordinal) ||
                ApplicationTreeRoots.Contains(parts[0], StringComparer.Ordinal) && parts.Length >= 2 ||
                parts.Length == 3 && parts[0] == "chats" && LowerHex(parts[1], 64) &&
                    (parts[2] == "active.txt" || LowerHex(parts[2].Substring(0, Math.Min(64, parts[2].Length)), 64) &&
                        parts[2].Length == 77 && parts[2].EndsWith(".events.jsonl", StringComparison.Ordinal)) ||
                parts.Length == 3 && parts[0] == "vba-journals" && LowerHex(parts[1], 64) &&
                    parts[2] == "mutations.events.jsonl" ||
                parts.Length == 2 && parts[0] == "resource-authority" &&
                    (parts[1] == "documents.json" || parts[1] == "mutation-attempts.jsonl" ||
                        parts[1].Length > 64 && LowerHex(parts[1].Substring(0, 64), 64) &&
                        new[] { ".authority.jsonl", ".revisions.jsonl", ".views.jsonl" }
                            .Contains(parts[1].Substring(64), StringComparer.Ordinal)) ||
                parts.Length == 4 && parts[0] == "resource-authority" && parts[1] == "outlook-archive-cache" &&
                    LowerHex(parts[2], 64) && parts[3].Length == 77 && LowerHex(parts[3].Substring(0, 64), 64) &&
                    parts[3].EndsWith(".ref.json", StringComparison.Ordinal) ||
                parts.Length == 3 && parts[0] == "chat-blobs" && LowerHex(parts[1], 2) && parts[2].Length == 69 &&
                    parts[2].StartsWith(parts[1], StringComparison.Ordinal) && LowerHex(parts[2].Substring(0, 64), 64) &&
                    parts[2].EndsWith(".blob", StringComparison.Ordinal) ||
                parts.Length == 4 && parts[0] == "storage-v2" && parts[1] == "conversation-streams" &&
                    LowerHex(parts[2], 64) && SegmentName(parts[3]) ||
                parts.Length == 4 && parts[0] == "storage-v2" && parts[1] == "resource-authority" &&
                    parts[2] == "resource-stream" && SegmentName(parts[3]) ||
                relative == "storage-v2/generation.json" || WorkspaceStore.IsBackupRelativePath(relative);
            if (!allowed) throw new InvalidDataException("Unknown application backup file owner: " + relative);
            var path = Path.GetFullPath(root);
            foreach (var part in parts)
            {
                if ((File.Exists(path) || Directory.Exists(path)) && StorageFileSystem.IsReparsePoint(path))
                    throw new InvalidDataException("Application backup path cannot follow links.");
                path = Path.Combine(path, part);
            }
            return path;
        }

        private static bool SafeApplicationPathComponent(string part)
        {
            return !string.IsNullOrEmpty(part) && part != "." && part != ".." && part.Length <= 255 &&
                part[part.Length - 1] != '.' && part[part.Length - 1] != ' ' &&
                !part.Any(ch => char.IsControl(ch) || ch == '<' || ch == '>' || ch == '"' || ch == '|' ||
                    ch == '?' || ch == '*' || ch == ':' || ch == '\\');
        }

        private static bool IsTransientFileName(string name)
        {
            return string.IsNullOrEmpty(name) || name.EndsWith(".lck", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("index.audit.", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".projection.sqlite", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".projection.sqlite-wal", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".projection.sqlite-shm", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTransientApplicationPath(string relative)
        {
            return relative == "storage-v2/index.sqlite" || relative == "storage-v2/index.sqlite-wal" ||
                relative == "storage-v2/index.sqlite-shm";
        }

        private static void RequireDestinationOutside(string destination, string parent, string message)
        {
            if (string.IsNullOrWhiteSpace(destination) || string.IsNullOrWhiteSpace(parent)) throw new ArgumentException(message);
            var target = Path.GetFullPath(destination);
            var root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (string.Equals(target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(message);
        }

        private static string ApplicationBackupDigest(ApplicationBackupManifest manifest, StorageProtector protector)
        {
            var digest = manifest.Digest;
            manifest.Digest = null;
            try { return protector.ComputeEventHash(stream => WriteApplicationBackupManifest(stream, manifest), manifest.HashAlgorithm, manifest.ProtectionKeyId); }
            finally { manifest.Digest = digest; }
        }

        private static void WriteApplicationBackupManifest(Stream stream, ApplicationBackupManifest manifest)
        {
            using (var text = new StreamWriter(stream, new UTF8Encoding(false), 4096, true))
            using (var json = new JsonTextWriter(text))
            {
                JsonSerializer.CreateDefault().Serialize(json, manifest);
                json.Flush();
            }
        }

        private sealed class EmptyBackupLease : IDisposable
        {
            internal static readonly EmptyBackupLease Instance = new EmptyBackupLease();
            public void Dispose() { }
        }
    }

    internal sealed class ApplicationBackupManifest
    {
        public string Format { get; set; }
        public string CreatedUtc { get; set; }
        public string HashAlgorithm { get; set; }
        public string Encryption { get; set; }
        public string ProtectionKeyId { get; set; }
        public List<string> WorkspaceRoots { get; set; } = new List<string>();
        public List<JournalBackupFile> Files { get; set; } = new List<JournalBackupFile>();
        public string Digest { get; set; }
    }
}
