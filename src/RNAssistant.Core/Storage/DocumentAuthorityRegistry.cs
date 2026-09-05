using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    public sealed class DocumentAuthorityRegistry
    {
        private readonly string _path;
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, string> RuntimeBindings =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public DocumentAuthorityRegistry(AppDataPaths paths)
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            _path = Path.Combine(paths.ResourceAuthorityDirectory, "documents.json");
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
