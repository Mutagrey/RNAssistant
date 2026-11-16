using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    public sealed partial class WorkspaceStore
    {
        // Maintenance owns run quiescence. These existing owner locks also pin
        // connection changes and relocations which do not start an agent run.
        internal IDisposable AcquireBackupLease()
        {
            var catalog = Path.Combine(_paths.WorkspaceDirectory, "catalog.lck");
            var locatorDirectory = Path.Combine(_paths.WorkspaceDirectory, "file-locators");
            RequireBackupDirectory(_paths.WorkspaceDirectory);
            if (Directory.Exists(locatorDirectory) || File.Exists(locatorDirectory)) RequireBackupDirectory(locatorDirectory);
            RequireOptionalBackupLock(catalog);
            var lease = new BackupLease { Catalog = StorageFileSystem.AcquireWriteLock(catalog) };
            try
            {
                var locators = Path.Combine(locatorDirectory, "locators.lck");
                RequireOptionalBackupLock(locators);
                lease.Locators = StorageFileSystem.AcquireWriteLock(locators);
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }

        // Source calls hold AcquireBackupLease; restore validates a private root.
        // No external workspace directory or manifest is read or rewritten here.
        internal string[] GetBackupFiles()
        {
            RequireBackupDirectory(_paths.WorkspaceDirectory);
            var files = new List<string>();
            var identities = new Dictionary<string, WorkspaceAssociation>(StringComparer.Ordinal);
            var aliases = new Dictionary<string, WorkspaceAssociation>(StringComparer.Ordinal);
            foreach (var path in Directory.EnumerateFileSystemEntries(_paths.WorkspaceDirectory))
            {
                var name = Path.GetFileName(path);
                if (name == "file-locators" || name == "file-moves") { RequireBackupDirectory(path); continue; }
                if (name == "run-leases")
                {
                    RequireBackupDirectory(path);
                    foreach (var entry in Directory.EnumerateFileSystemEntries(path))
                    {
                        var leaseName = Path.GetFileName(entry);
                        if (!BackupHexFile(leaseName, "", ".lck", 64)) throw new InvalidDataException("Unknown workspace run lease.");
                        RequireBackupLock(entry);
                    }
                    continue;
                }
                if (name == "catalog.lck" || name.EndsWith(".definition.lck", StringComparison.Ordinal) &&
                    ValidId(name.Substring(0, name.Length - ".definition.lck".Length)))
                { RequireBackupLock(path); continue; }
                var idFile = name.EndsWith(".json", StringComparison.Ordinal) && ValidId(Path.GetFileNameWithoutExtension(name));
                var pathFile = BackupHexFile(name, "path-", ".json", 64);
                if (!idFile && !pathFile) throw new InvalidDataException("Unknown workspace identity file.");
                var value = ReadBackupRecord<WorkspaceAssociation>(path,
                    new[] { "WorkspaceId", "RootPath", "Name" }, new[] { "ManifestStored", "HiddenFromProjectTree" });
                if (!ValidId(value.WorkspaceId) || string.IsNullOrWhiteSpace(value.Name) || !BackupAbsolutePath(value.RootPath))
                    throw new InvalidDataException("Workspace backup association is invalid.");
                if (idFile)
                {
                    if (name != value.WorkspaceId + ".json") throw new InvalidDataException("Workspace backup identity does not match its filename.");
                    identities.Add(value.WorkspaceId, value);
                }
                else
                {
                    if (name != "path-" + TextPatternEngine.Sha256(value.RootPath) + ".json")
                        throw new InvalidDataException("Workspace backup path does not match its filename.");
                    aliases.Add(value.RootPath, value);
                }
                files.Add(path);
            }
            if (identities.Count > 1000) throw new InvalidDataException("Workspace catalog exceeds its limit.");
            foreach (var item in identities.Values)
            {
                WorkspaceAssociation alias;
                if (!aliases.TryGetValue(item.RootPath, out alias) || alias.WorkspaceId != item.WorkspaceId)
                    throw new InvalidDataException("Workspace backup is missing its current path association.");
            }
            // A move retains old path aliases; HideProject only updates the id
            // record. Neither case authorizes rewriting the existing metadata.
            if (aliases.Values.Any(alias => !identities.ContainsKey(alias.WorkspaceId)))
                throw new InvalidDataException("Workspace backup contains an orphan path association.");
            files.AddRange(WorkspaceFileService.GetBackupFiles(_paths,
                identities.ToDictionary(item => item.Key, item => item.Value.RootPath, StringComparer.Ordinal)));
            return files.OrderBy(path => path, StringComparer.Ordinal).ToArray();
        }

        internal static bool IsBackupRelativePath(string relative)
        {
            var parts = (relative ?? string.Empty).Split('/');
            if (parts.Length == 2 && parts[0] == "workspaces")
                return parts[1].EndsWith(".json", StringComparison.Ordinal) && ValidId(Path.GetFileNameWithoutExtension(parts[1])) ||
                    BackupHexFile(parts[1], "path-", ".json", 64);
            return WorkspaceFileService.IsBackupRelativePath(relative);
        }

        internal void ValidateBackupScopes(IEnumerable<string> scopes)
        {
            foreach (var scope in scopes)
            {
                if (scope == null || !scope.StartsWith("workspace:", StringComparison.Ordinal)) continue;
                var id = scope.Substring("workspace:".Length);
                // The journal supports arbitrary logical scopes; this owner
                // binds only the same ids accepted by OpenConnectedHistory.
                if (!ValidId(id)) continue;
                var association = ReadAssociation(IdPath(id));
                if (association == null || association.WorkspaceId != id)
                    throw new InvalidDataException("Workspace journal is missing its saved identity association.");
            }
        }

        internal static bool BackupHexFile(string name, string prefix, string suffix, int length)
        { return name != null && name.Length == prefix.Length + length + suffix.Length &&
            name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(suffix, StringComparison.Ordinal) &&
            name.Substring(prefix.Length, length).All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'); }

        internal static bool BackupAbsolutePath(string path)
        {
            try { return !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) && Path.GetFullPath(path) == path; }
            catch (Exception error) when (error is ArgumentException || error is NotSupportedException || error is IOException) { return false; }
        }

        internal static void RequireBackupDirectory(string path)
        { if (!StorageFileSystem.IsRegularDirectory(path)) throw new InvalidDataException("Workspace backup directory is unavailable or linked."); }

        internal static void RequireBackupLock(string path)
        {
            if (!File.Exists(path) || Directory.Exists(path) || StorageFileSystem.IsReparsePoint(path) || new FileInfo(path).Length != 0)
                throw new InvalidDataException("Workspace backup lease must be an empty regular file.");
        }

        private static void RequireOptionalBackupLock(string path)
        { if (File.Exists(path) || Directory.Exists(path)) RequireBackupLock(path); }

        internal static T ReadBackupRecord<T>(string path, string[] strings, string[] booleans = null)
        {
            if (!File.Exists(path) || Directory.Exists(path) || StorageFileSystem.IsReparsePoint(path) || new FileInfo(path).Length > 1024 * 1024)
                throw new InvalidDataException("Workspace backup record is unavailable, linked or oversized.");
            try
            {
                using (var input = new StreamReader(path, Utf8, false))
                using (var reader = new JsonTextReader(input) { MaxDepth = 4, DateParseHandling = DateParseHandling.None })
                {
                    var value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    var flags = booleans ?? new string[0];
                    if (reader.Read() || value.Properties().Count() != strings.Length + flags.Length ||
                        strings.Any(field => value[field]?.Type != JTokenType.String) ||
                        flags.Any(field => value[field]?.Type != JTokenType.Boolean))
                        throw new InvalidDataException("Workspace backup record has an invalid shape.");
                    return value.ToObject<T>(JsonSerializer.Create(new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error }));
                }
            }
            catch (JsonException error) { throw new InvalidDataException("Workspace backup record is malformed.", error); }
        }

        private sealed class BackupLease : IDisposable
        {
            internal IDisposable Catalog;
            internal IDisposable Locators;
            public void Dispose() { Locators?.Dispose(); Locators = null; Catalog?.Dispose(); Catalog = null; }
        }
    }
}
