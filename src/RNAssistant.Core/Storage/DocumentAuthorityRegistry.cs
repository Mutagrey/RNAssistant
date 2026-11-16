using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    public sealed class DocumentAuthorityEntry
    {
        public string AuthorityId { get; set; }
        public string Host { get; set; }
        public string Locator { get; set; }
    }

    public sealed class DocumentAuthorityRegistry
    {
        internal const string BackupRelativePath = "resource-authority/documents.json";
        private readonly string _path;
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, string> RuntimeBindings =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public DocumentAuthorityRegistry(AppDataPaths paths)
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            _path = Path.Combine(paths.ResourceAuthorityDirectory, "documents.json");
        }

        // The maintenance gate alone does not cover identity resolution. Keep the
        // same lock order as Resolve until the complete backup has been copied.
        internal IDisposable AcquireBackupLease()
        {
            Monitor.Enter(Sync);
            try
            {
                RequireBackupDirectory();
                IsBackupFilePresent(_path);
                var leasePath = _path + ".lck";
                if (IsBackupFilePresent(leasePath) && new FileInfo(leasePath).Length != 0)
                    throw new InvalidDataException("Document authority lease is not empty.");
                return new BackupLease(StorageFileSystem.AcquireWriteLock(leasePath));
            }
            catch { Monitor.Exit(Sync); throw; }
        }

        // Validate the original bytes, never the repaired/deduplicated Load view.
        // A missing file is valid for a root that has no persisted Office binding.
        internal IReadOnlyList<string> ValidateBackupState()
        {
            RequireBackupDirectory();
            if (!IsBackupFilePresent(_path)) return new string[0];
            try
            {
                JObject root;
                using (var input = File.OpenText(_path))
                using (var reader = new JsonTextReader(input) { MaxDepth = 8, DateParseHandling = DateParseHandling.None })
                {
                    root = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    if (reader.Read()) throw new InvalidDataException("Trailing document authority registry data.");
                }
                RequireBackupFields(root, "ContractVersion", "Documents");
                if (root["ContractVersion"].Type != JTokenType.Integer || root["ContractVersion"].ToString(Formatting.None) != "1" ||
                    root["Documents"].Type != JTokenType.Array)
                    throw new InvalidDataException("Unsupported document authority registry contract.");
                foreach (var token in (JArray)root["Documents"])
                {
                    var item = token as JObject;
                    RequireBackupFields(item, "AuthorityId", "Host", "Locator", "UpdatedUtc");
                    if (item["AuthorityId"].Type != JTokenType.String || item["Host"].Type != JTokenType.String ||
                        item["Locator"].Type != JTokenType.Null && item["Locator"].Type != JTokenType.String ||
                        item["UpdatedUtc"].Type != JTokenType.String)
                        throw new InvalidDataException("Invalid document authority binding shape.");
                }
                var state = root.ToObject<DocumentAuthorityRegistryState>(JsonSerializer.Create(
                    new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error }));
                var ids = new HashSet<string>(StringComparer.Ordinal);
                var locators = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                foreach (var item in state.Documents)
                {
                    if (string.IsNullOrWhiteSpace(item.AuthorityId) || item.AuthorityId != item.AuthorityId.Trim() ||
                        string.IsNullOrWhiteSpace(item.Host) || item.Host != NormalizeHost(item.Host) ||
                        item.Locator != null && (string.IsNullOrWhiteSpace(item.Locator) || item.Locator != item.Locator.Trim()) ||
                        !ids.Add(item.AuthorityId))
                        throw new InvalidDataException("Invalid or duplicate document authority identity.");
                    if (item.Locator == null) continue;
                    HashSet<string> hostLocators;
                    if (!locators.TryGetValue(item.Host, out hostLocators))
                        locators.Add(item.Host, hostLocators = new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    if (!hostLocators.Add(item.Locator))
                        throw new InvalidDataException("Ambiguous document authority locator.");
                }
                return new[] { _path };
            }
            catch (JsonException ex) { throw new InvalidDataException("Document authority registry is invalid.", ex); }
        }

        private void RequireBackupDirectory()
        {
            var directory = Path.GetDirectoryName(_path);
            if (!StorageFileSystem.IsRegularDirectory(directory) || !StorageFileSystem.IsRegularDirectory(Path.GetDirectoryName(directory)))
                throw new InvalidDataException("Document authority backup directory is unavailable or linked.");
        }

        private static bool IsBackupFilePresent(string path)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidDataException("Document authority backup file cannot be a directory or link.");
            return true;
        }

        private static void RequireBackupFields(JObject value, params string[] fields)
        {
            if (value == null || value.Properties().Count() != fields.Length || fields.Any(field => value.Property(field) == null))
                throw new InvalidDataException("Incomplete or unsupported document authority registry fields.");
        }

        private sealed class BackupLease : IDisposable
        {
            private IDisposable _fileLease;
            internal BackupLease(IDisposable fileLease) { _fileLease = fileLease; }
            public void Dispose()
            {
                var lease = Interlocked.Exchange(ref _fileLease, null);
                if (lease == null) return;
                try { lease.Dispose(); }
                finally { Monitor.Exit(Sync); }
            }
        }

        public void ResetAfterRuntimeDataClear()
        {
            lock (Sync)
            {
                var prefix = _path + "|";
                foreach (var key in RuntimeBindings.Keys.Where(item => item.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                    RuntimeBindings.Remove(key);
            }
        }

        public DocumentAuthorityId Resolve(string host, string runtimeId, string locator,
            string existingAuthorityId = null)
        {
            host = NormalizeHost(host);
            runtimeId = Normalize(runtimeId);
            locator = NormalizeLocator(locator);
            lock (Sync)
            using (StorageFileSystem.AcquireWriteLock(_path + ".lck"))
            {
                var state = Load();
                var runtimeKey = runtimeId == null ? null : _path + "|" + RuntimeKey(host, runtimeId);
                string authorityId;
                if (!string.IsNullOrWhiteSpace(existingAuthorityId))
                {
                    authorityId = existingAuthorityId.Trim();
                }
                else if (runtimeKey != null && RuntimeBindings.TryGetValue(runtimeKey, out authorityId))
                {
                }
                else
                {
                    var byLocator = locator == null ? null : state.Documents.SingleOrDefault(item =>
                        string.Equals(item.Host, host, StringComparison.Ordinal) &&
                        string.Equals(item.Locator, locator, StringComparison.OrdinalIgnoreCase));
                    authorityId = byLocator == null ? DocumentAuthorityId.Create().Id : byLocator.AuthorityId;
                }

                if (runtimeKey != null) RuntimeBindings[runtimeKey] = authorityId;
                var existing = state.Documents.SingleOrDefault(item =>
                    string.Equals(item.AuthorityId, authorityId, StringComparison.Ordinal));
                var changed = false;
                if (existing == null)
                {
                    existing = new DocumentAuthorityRecord
                    {
                        AuthorityId = authorityId,
                        Host = host,
                        Locator = locator,
                        UpdatedUtc = DateTime.UtcNow
                    };
                    state.Documents.Add(existing);
                    changed = locator != null;
                }
                else if (!string.Equals(existing.Host, host, StringComparison.Ordinal) ||
                    !string.Equals(existing.Locator, locator, StringComparison.OrdinalIgnoreCase))
                {
                    // Save/Save As moves the one logical document. The old locator is
                    // deliberately not retained as an alias of the live authority.
                    existing.Host = host;
                    existing.Locator = locator;
                    existing.UpdatedUtc = DateTime.UtcNow;
                    changed = true;
                }
                if (locator != null)
                {
                    var collisions = state.Documents.Where(item => item != existing &&
                        string.Equals(item.Host, host, StringComparison.Ordinal) &&
                        string.Equals(item.Locator, locator, StringComparison.OrdinalIgnoreCase)).ToList();
                    foreach (var collision in collisions) state.Documents.Remove(collision);
                    changed = changed || collisions.Count > 0;
                }
                if (changed) Save(state);
                return new DocumentAuthorityId(authorityId);
            }
        }

        public IReadOnlyList<DocumentAuthorityEntry> List()
        {
            lock (Sync)
                return Load().Documents.Select(item => new DocumentAuthorityEntry {
                    AuthorityId = item.AuthorityId, Host = item.Host, Locator = item.Locator }).ToArray();
        }

        private DocumentAuthorityRegistryState Load()
        {
            if (!File.Exists(_path)) return new DocumentAuthorityRegistryState();
            try
            {
                var state = JsonConvert.DeserializeObject<DocumentAuthorityRegistryState>(File.ReadAllText(_path));
                if (state == null) throw new InvalidDataException("Document authority registry is empty.");
                state.Documents = (state.Documents ?? new List<DocumentAuthorityRecord>())
                    .Where(item => item != null && !string.IsNullOrWhiteSpace(item.AuthorityId) &&
                        !string.IsNullOrWhiteSpace(item.Host))
                    .GroupBy(item => item.AuthorityId, StringComparer.Ordinal)
                    .Select(group => group.OrderByDescending(item => item.UpdatedUtc).First())
                    .ToList();
                return state;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Document authority registry is invalid.", ex);
            }
        }

        private void Save(DocumentAuthorityRegistryState state)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            StorageFileSystem.WriteAllTextAtomic(_path,
                JsonConvert.SerializeObject(state, Formatting.Indented));
        }

        private static string NormalizeHost(string value)
        {
            value = Normalize(value);
            if (value == null) throw new ArgumentException("Document host is required.", nameof(value));
            return value.ToLowerInvariant();
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string NormalizeLocator(string value)
        {
            value = Normalize(value);
            if (value == null) return null;
            try { return Path.GetFullPath(value); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return value;
            }
        }

        private static string RuntimeKey(string host, string runtimeId)
        {
            return runtimeId == null ? null : host + "\n" + runtimeId;
        }

        private sealed class DocumentAuthorityRegistryState
        {
            public int ContractVersion { get; set; }
            public List<DocumentAuthorityRecord> Documents { get; set; }

            public DocumentAuthorityRegistryState()
            {
                ContractVersion = 1;
                Documents = new List<DocumentAuthorityRecord>();
            }
        }

        private sealed class DocumentAuthorityRecord
        {
            public string AuthorityId { get; set; }
            public string Host { get; set; }
            public string Locator { get; set; }
            public DateTime UpdatedUtc { get; set; }
        }
    }
}
