using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    public sealed partial class CasMaintenanceService
    {
        private const string BackupFormat = "rnassistant-journal-backup-v1";
        private static readonly string[] KeyProfileFiles = { "settings.json", "secret.bin", "history-secret.bin", "history-protection.salt" };

        // Isolated new-generation API until P7. Never exports an incomplete mixture
        // of old/new stores or overwrites an existing backup. Raw key material is
        // never exported: secret.bin/history-secret.bin remain DPAPI CurrentUser.
        internal void BackupJournals(string destination)
        {
            using (_maintenanceLeaseProvider())
            {
                _ensureQuiescent();
                foreach (var directory in new[] { _paths.ChatDirectory, _paths.VbaJournalDirectory, _paths.ResourceAuthorityDirectory })
                    if (Directory.EnumerateFileSystemEntries(directory).Any(path => directory != _paths.ResourceAuthorityDirectory ||
                        (Path.GetFileName(path) != "mutation-attempts.jsonl.lck" && Path.GetFileName(path) != "authority.lck") ||
                        Directory.Exists(path) || StorageFileSystem.IsReparsePoint(path)))
                        throw new InvalidOperationException("Journal backup requires the new generation; legacy stores need their existing preservation path.");
                var snapshot = AuditInternal();
                if (!snapshot.Report.CanGarbageCollect || snapshot.Report.MissingBlobCount != 0 || snapshot.Report.CorruptBlobCount != 0)
                    throw new InvalidDataException("Backup requires complete, verified canonical closure.");
                var protector = _protectionProvider();
                if (protector.Encrypts || protector.UsesHmac)
                {
                    var settings = new SettingsService(_paths).Load();
                    var secret = settings.HistoryKeySource == HistoryKeySources.CustomSecret ? _paths.HistorySecretFile : _paths.SecretFile;
                    if (!File.Exists(_paths.SettingsFile) || !File.Exists(_paths.HistoryProtectionSaltFile) || !File.Exists(secret))
                        throw new InvalidOperationException("Protected backup requires its existing settings, salt and DPAPI key file.");
                }
                var manifest = new JournalBackupManifest { Format = BackupFormat, HashAlgorithm = BackupHashAlgorithm(protector),
                    ProtectionKeyId = protector.KeyId, Encryption = protector.EncryptionMode };
                var staging = CreateStaging(destination);
                try
                {
                    var sources = snapshot.JournalStreams.SelectMany(stream => stream.Segments)
                        .Concat(snapshot.References.Select(reference => _blobs.PathFor(reference.Sha256)))
                        .Concat(KeyProfileFiles.Select(name => Path.Combine(_paths.Root, name)).Where(File.Exists))
                        .Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal);
                    foreach (var source in sources)
                    {
                        var relative = RelativePath(_paths.Root, source).Replace('\\', '/');
                        var copied = CopyBackupFile(source, SafeBackupPath(Path.Combine(staging, "data"), relative));
                        copied.Path = relative; manifest.Files.Add(copied);
                    }
                    var current = _protectionProvider();
                    if (current.KeyId != protector.KeyId || current.EncryptionMode != protector.EncryptionMode || current.IntegrityMode != protector.IntegrityMode)
                        throw new InvalidOperationException("History protection changed during backup.");
                    manifest.Digest = BackupDigest(manifest, protector);
                    using (var file = new FileStream(Path.Combine(staging, "backup.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { WriteManifest(file, manifest); file.Flush(true); }
                    Directory.Move(staging, Path.GetFullPath(destination));
                }
                finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            }
        }

        internal static AppDataPaths RestoreJournals(string backup, string destination,
            Func<AppDataPaths, StorageProtector> protectionProvider)
        {
            if (protectionProvider == null) throw new ArgumentNullException(nameof(protectionProvider));
            if (StorageFileSystem.IsReparsePoint(backup) || StorageFileSystem.IsReparsePoint(Path.Combine(backup, "backup.json")))
                throw new InvalidDataException("Backup cannot be a link.");
            JournalBackupManifest manifest;
            using (var file = File.OpenText(Path.Combine(backup, "backup.json")))
            using (var reader = new JsonTextReader(file) { MaxDepth = 16 })
            {
                manifest = JsonSerializer.Create(new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error }).Deserialize<JournalBackupManifest>(reader);
                if (reader.Read()) throw new InvalidDataException("Trailing backup manifest data.");
            }
            if (manifest?.Format != BackupFormat || manifest.Files == null || manifest.Files.Any(file => file == null) ||
                manifest.Files.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() != manifest.Files.Count)
                throw new InvalidDataException("Unsupported or ambiguous backup manifest.");
            var staging = CreateStaging(destination);
            try
            {
                foreach (var expected in manifest.Files)
                {
                    var copied = CopyBackupFile(SafeBackupPath(Path.Combine(backup, "data"), expected.Path), SafeBackupPath(staging, expected.Path));
                    if (copied.Bytes != expected.Bytes || copied.Sha256 != expected.Sha256)
                        throw new InvalidDataException("Backup file checksum differs from its manifest.");
                }
                var paths = AppDataPaths.CreateForRoot(staging);
                var protector = protectionProvider(paths) ?? throw new InvalidOperationException("Backup protection is unavailable.");
                if (manifest.ProtectionKeyId != protector.KeyId || manifest.HashAlgorithm != BackupHashAlgorithm(protector) ||
                    manifest.Encryption != protector.EncryptionMode || manifest.Digest != BackupDigest(manifest, protector))
                    throw new InvalidDataException("Backup identity/authentication failed.");
                // A fresh root has no readers or writers. Verify the entire prefix
                // and nested closure, then recreate the one disposable index.
                var references = new CasReachabilityScan();
                JournalIndex.ScanCanonical(paths, protector, references);
                var blobs = new ChatBlobStore(paths, () => protector);
                foreach (var reference in references.References)
                    if (!blobs.HasVerifiedReference(reference.Reference)) throw new InvalidDataException("Restored canonical body is missing or corrupt.");
                JournalIndex.Repair(paths, protector, true, CancellationToken.None);
                Directory.Move(staging, Path.GetFullPath(destination));
                return AppDataPaths.CreateForRoot(Path.GetFullPath(destination));
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        }

        private static string CreateStaging(string destination)
        {
            var target = Path.GetFullPath(destination ?? throw new ArgumentNullException(nameof(destination)));
            if (File.Exists(target) || Directory.Exists(target)) throw new IOException("Backup/restore destination already exists.");
            var parent = Path.GetDirectoryName(target);
            StorageFileSystem.EnsureRegularDirectory(parent);
            var staging = Path.Combine(parent, ".rna-backup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            return staging;
        }

        private static string SafeBackupPath(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || relative.IndexOf('\\') >= 0 || relative.IndexOf(':') >= 0 || Path.IsPathRooted(relative))
                throw new InvalidDataException("Invalid backup relative path.");
            var parts = relative.Split('/');
            if (parts.Any(part => part == "" || part == "." || part == "..")) throw new InvalidDataException("Invalid backup path component.");
            var allowed = parts.Length == 1 && KeyProfileFiles.Contains(relative) ||
                parts.Length == 3 && parts[0] == "chat-blobs" && LowerHex(parts[1], 2) && parts[2].Length == 69 &&
                    parts[2].EndsWith(".blob", StringComparison.Ordinal) && LowerHex(parts[2].Substring(0, 64), 64) && parts[2].StartsWith(parts[1], StringComparison.Ordinal) ||
                parts.Length == 3 && parts[0] == "conversation-streams" && LowerHex(parts[1], 64) && SegmentName(parts[2]) ||
                parts.Length == 2 && parts[0] == "resource-stream" && SegmentName(parts[1]);
            if (!allowed) throw new InvalidDataException("Unknown backup file owner.");
            var path = Path.GetFullPath(root);
            foreach (var part in parts)
            {
                if ((File.Exists(path) || Directory.Exists(path)) && StorageFileSystem.IsReparsePoint(path))
                    throw new InvalidDataException("Backup path cannot follow links.");
                path = Path.Combine(path, part);
            }
            return path;
        }

        private static bool LowerHex(string value, int count)
        { return value.Length == count && value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'); }
        private static bool SegmentName(string name)
        { int value; return name.Length == 14 && name.EndsWith(".jsonl", StringComparison.Ordinal) && int.TryParse(name.Substring(0, 8), out value) && value > 0 && name.Substring(0, 8) == value.ToString("D8"); }
        private static string BackupHashAlgorithm(StorageProtector protector)
        { return protector.UsesHmac || protector.Encrypts ? HistoryIntegrityModes.HmacSha256 : HistoryIntegrityModes.Sha256; }
        private static string BackupDigest(JournalBackupManifest manifest, StorageProtector protector)
        {
            var digest = manifest.Digest; manifest.Digest = null;
            try { return protector.ComputeEventHash(stream => WriteManifest(stream, manifest), manifest.HashAlgorithm, manifest.ProtectionKeyId); }
            finally { manifest.Digest = digest; }
        }
        private static void WriteManifest(Stream stream, JournalBackupManifest manifest)
        {
            using (var text = new StreamWriter(stream, new UTF8Encoding(false), 4096, true))
            using (var json = new JsonTextWriter(text)) { JsonSerializer.CreateDefault().Serialize(json, manifest); json.Flush(); }
        }
        private static JournalBackupFile CopyBackupFile(string source, string destination)
        {
            if (StorageFileSystem.IsReparsePoint(source)) throw new InvalidDataException("Backup source is unavailable or linked.");
            StorageFileSystem.EnsureRegularDirectory(Path.GetDirectoryName(destination));
            long length = 0;
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var hash = SHA256.Create())
            {
                var buffer = new byte[81920]; int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                { output.Write(buffer, 0, read); hash.TransformBlock(buffer, 0, read, null, 0); length += read; }
                hash.TransformFinalBlock(new byte[0], 0, 0); output.Flush(true);
                return new JournalBackupFile { Bytes = length, Sha256 = BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant() };
            }
        }
    }

    internal sealed class JournalBackupManifest
    {
        public string Format { get; set; }
        public string HashAlgorithm { get; set; }
        public string Encryption { get; set; }
        public string ProtectionKeyId { get; set; }
        public List<JournalBackupFile> Files { get; set; } = new List<JournalBackupFile>();
        public string Digest { get; set; }
    }
    internal sealed class JournalBackupFile
    {
        public string Path { get; set; }
        public long Bytes { get; set; }
        public string Sha256 { get; set; }
    }
}
