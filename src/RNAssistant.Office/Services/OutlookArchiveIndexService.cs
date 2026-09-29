using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Domains.Outlook;

namespace RNAssistant.Office.Services
{
    internal sealed class OutlookArchiveIndexManifest
    {
        public string Id { get; set; }
        public string Generation { get; set; }
        [JsonIgnore] public bool RefreshRequested { get; set; }
        public string DocumentKey { get; set; }
        public DateTime FromUtc { get; set; }
        public DateTime ToUtc { get; set; }
        public bool IncludePst { get; set; }
        public string SourceSignature { get; set; }
        public string Cursor { get; set; }
        public bool Complete { get; set; }
        public int ExaminedItems { get; set; }
        public int IndexedMessages { get; set; }
        public int UniqueMessages { get; set; }
        public int CapturedBodies { get; set; }
        public int FailedBodies { get; set; }
        public int AttachmentCandidates { get; set; }
        public int Errors { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public List<string> ErrorSamples { get; set; } = new List<string>();
        public List<ChatBlobReference> Pages { get; set; } = new List<ChatBlobReference>();
    }

    internal sealed class OutlookArchiveIndexedMail
    {
        public string StoreId { get; set; }
        public string EntryId { get; set; }
        public string FolderPath { get; set; }
        public string Subject { get; set; }
        public string Sender { get; set; }
        public DateTime ReceivedUtc { get; set; }
        public string InternetMessageId { get; set; }
        public string ConversationId { get; set; }
        public int AttachmentCount { get; set; }
        public DateTime LastModificationUtc { get; set; }
        public string Error { get; set; }
        public ChatBlobReference Body { get; set; }
        public bool Duplicate { get; set; }
    }

    internal sealed class OutlookArchiveIndexService
    {
        private readonly string _root;
        private readonly ChatBlobStore _blobs;
        private readonly Dictionary<string, HashSet<string>> _seen =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        internal OutlookArchiveIndexService(AppDataPaths paths, ChatBlobStore blobs)
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            _blobs = blobs ?? throw new ArgumentNullException(nameof(blobs));
            _root = Path.Combine(paths.ResourceAuthorityDirectory, "outlook-archive-cache");
        }

        internal OutlookArchiveIndexManifest Open(string documentKey, DateTime fromUtc, DateTime toUtc, bool includePst,
            bool refresh = false)
        {
            if (string.IsNullOrWhiteSpace(documentKey) || !documentKey.StartsWith("outlook-mailbox:", StringComparison.Ordinal) ||
                fromUtc.Kind != DateTimeKind.Utc || toUtc.Kind != DateTimeKind.Utc || fromUtc >= toUtc)
                throw new InvalidOperationException("A mailbox and exact UTC period are required.");
            var id = AppDataPaths.SafeFileName(documentKey + "|" + fromUtc.ToString("O") + "|" + toUtc.ToString("O") + "|" + includePst);
            if (refresh) _seen.Remove(id);
            var path = PointerPath(documentKey, id);
            if (!refresh && File.Exists(path))
            {
                var reference = JsonConvert.DeserializeObject<ChatBlobReference>(File.ReadAllText(path, Encoding.UTF8));
                var json = _blobs.ReadText(reference);
                if (json == null) throw new IOException("The Outlook archive checkpoint is unavailable; refresh explicitly to restart.");
                var current = JsonConvert.DeserializeObject<OutlookArchiveIndexManifest>(json);
                if (current == null || current.Id != id || current.DocumentKey != documentKey ||
                    current.FromUtc != fromUtc || current.ToUtc != toUtc || current.IncludePst != includePst ||
                    current.Pages == null || current.ErrorSamples == null ||
                    string.IsNullOrWhiteSpace(current.Generation))
                    throw new IOException("The Outlook archive checkpoint is invalid.");
                return current;
            }
            return new OutlookArchiveIndexManifest { Id = id, DocumentKey = documentKey,
                FromUtc = fromUtc, ToUtc = toUtc, IncludePst = includePst,
                Generation = Guid.NewGuid().ToString("N"), RefreshRequested = refresh };
        }

        internal void Append(OutlookArchiveIndexManifest manifest, OutlookArchiveScanBatch batch)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            var path = PointerPath(manifest.DocumentKey, manifest.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None))
            {
                if (File.Exists(path) && !manifest.RefreshRequested)
                {
                    var current = Open(manifest.DocumentKey, manifest.FromUtc, manifest.ToUtc, manifest.IncludePst);
                    if (current.Generation != manifest.Generation || current.Cursor != manifest.Cursor ||
                        current.Pages.Count != manifest.Pages.Count)
                        throw new InvalidOperationException("Outlook archive checkpoint changed during this scan.");
                }
                try { AppendCore(manifest, batch, path); }
                catch { _seen.Remove(manifest.Id); throw; }
            }
            manifest.RefreshRequested = false;
        }

        private void AppendCore(OutlookArchiveIndexManifest manifest, OutlookArchiveScanBatch batch, string path)
        {
            if (manifest == null || batch == null || manifest.Complete ||
                string.IsNullOrWhiteSpace(batch.SourceSignature) ||
                !string.IsNullOrEmpty(manifest.SourceSignature) && manifest.SourceSignature != batch.SourceSignature ||
                !batch.Complete && (string.IsNullOrWhiteSpace(batch.NextCursor) || batch.NextCursor == manifest.Cursor))
                throw new InvalidOperationException("Outlook archive batch does not advance the exact source checkpoint.");
            var page = new List<OutlookArchiveIndexedMail>();
            var seen = Seen(manifest);
            foreach (var mail in batch.Messages ?? new OutlookArchiveMail[0])
            {
                if (mail == null || string.IsNullOrWhiteSpace(mail.StoreId) || string.IsNullOrWhiteSpace(mail.EntryId))
                    throw new InvalidOperationException("Outlook archive mail has no stable source identity.");
                var indexed = new OutlookArchiveIndexedMail { StoreId = mail.StoreId, EntryId = mail.EntryId,
                    FolderPath = mail.FolderPath, Subject = mail.Subject, Sender = mail.Sender,
                    ReceivedUtc = mail.ReceivedUtc, InternetMessageId = mail.InternetMessageId,
                    ConversationId = mail.ConversationId, AttachmentCount = mail.AttachmentCount,
                    LastModificationUtc = mail.LastModificationUtc,
                    Error = mail.Error, Body = mail.Body == null ? null :
                        _blobs.StoreText(mail.Body, "text/plain; charset=utf-8") };
                var duplicateKey = DuplicateKey(indexed);
                indexed.Duplicate = duplicateKey != null && !seen.Add(duplicateKey);
                page.Add(indexed);
            }
            var group = new List<OutlookArchiveIndexedMail>();
            var groupCharacters = 0;
            foreach (var item in page)
            {
                var length = item.Body == null ? 0 : (int)Math.Min(item.Body.ByteLength, 700000);
                if (group.Count > 0 && groupCharacters + length > 700000)
                {
                    manifest.Pages.Add(_blobs.StoreText(JsonConvert.SerializeObject(group), "application/json"));
                    group.Clear(); groupCharacters = 0;
                }
                group.Add(item);
                groupCharacters += length;
            }
            if (group.Count > 0)
                manifest.Pages.Add(_blobs.StoreText(JsonConvert.SerializeObject(group), "application/json"));
            manifest.SourceSignature = batch.SourceSignature;
            manifest.Cursor = batch.NextCursor;
            manifest.Complete = batch.Complete;
            manifest.ExaminedItems += batch.ExaminedItems;
            manifest.IndexedMessages += page.Count;
            manifest.UniqueMessages += page.Count(item => !item.Duplicate);
            manifest.CapturedBodies += page.Count(item => item.Body != null);
            manifest.FailedBodies += page.Count(item => item.Body == null);
            manifest.AttachmentCandidates += page.Sum(item => item.AttachmentCount);
            manifest.Errors += (batch.Errors == null ? 0 : batch.Errors.Count) + page.Count(item => !string.IsNullOrEmpty(item.Error));
            foreach (var error in batch.Errors ?? new string[0])
                if (manifest.ErrorSamples.Count < 50) manifest.ErrorSamples.Add(error);
            foreach (var item in page.Where(item => !string.IsNullOrEmpty(item.Error)))
                if (manifest.ErrorSamples.Count < 50) manifest.ErrorSamples.Add(item.FolderPath + ": " + item.Error);
            manifest.UpdatedUtc = DateTime.UtcNow;
            var manifestRef = _blobs.StoreText(JsonConvert.SerializeObject(manifest), "application/json");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonConvert.SerializeObject(manifestRef), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal IReadOnlyList<OutlookArchiveIndexManifest> List(string documentKey)
        {
            var directory = Path.Combine(_root, AppDataPaths.SafeFileName(documentKey));
            if (!Directory.Exists(directory)) return new OutlookArchiveIndexManifest[0];
            var result = new List<OutlookArchiveIndexManifest>();
            foreach (var path in Directory.GetFiles(directory, "*.ref.json"))
            {
                var reference = JsonConvert.DeserializeObject<ChatBlobReference>(File.ReadAllText(path, Encoding.UTF8));
                var json = _blobs.ReadText(reference);
                if (json == null) throw new IOException("An Outlook archive checkpoint is unavailable.");
                var manifest = JsonConvert.DeserializeObject<OutlookArchiveIndexManifest>(json);
                if (manifest == null || manifest.DocumentKey != documentKey || manifest.Pages == null)
                    throw new IOException("An Outlook archive checkpoint is invalid.");
                result.Add(manifest);
            }
            return result.OrderByDescending(item => item.UpdatedUtc).ToArray();
        }

        internal IReadOnlyList<OutlookArchiveIndexedMail> ReadPage(OutlookArchiveIndexManifest manifest, int pageIndex)
        {
            if (manifest == null || pageIndex < 0 || pageIndex >= manifest.Pages.Count)
                throw new InvalidOperationException("Archive page is unavailable.");
            var json = _blobs.ReadText(manifest.Pages[pageIndex]);
            if (json == null) throw new IOException("The exact archive page is unavailable.");
            var page = JsonConvert.DeserializeObject<List<OutlookArchiveIndexedMail>>(json);
            if (page == null) throw new IOException("The archive page is invalid.");
            return page;
        }

        internal string ReadBody(OutlookArchiveIndexedMail mail)
        {
            if (mail == null || mail.Body == null) return null;
            var body = _blobs.ReadText(mail.Body);
            if (body == null) throw new IOException("The exact indexed mail body is unavailable.");
            return body;
        }

        private HashSet<string> Seen(OutlookArchiveIndexManifest manifest)
        {
            HashSet<string> seen;
            if (_seen.TryGetValue(manifest.Id, out seen)) return seen;
            seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var page in Enumerable.Range(0, manifest.Pages.Count))
                foreach (var item in ReadPage(manifest, page))
                {
                    var key = DuplicateKey(item);
                    if (key != null) seen.Add(key);
                }
            _seen[manifest.Id] = seen;
            return seen;
        }

        private static string DuplicateKey(OutlookArchiveIndexedMail item)
        {
            if (item.Body == null) return null;
            var identity = !string.IsNullOrWhiteSpace(item.InternetMessageId)
                ? item.InternetMessageId.Trim().ToLowerInvariant()
                : (item.Sender ?? string.Empty).Trim().ToLowerInvariant() + "|" +
                    (item.Subject ?? string.Empty).Trim().ToLowerInvariant() + "|" +
                    item.ReceivedUtc.Ticks.ToString();
            return identity + "|" + item.Body.Sha256;
        }

        private string PointerPath(string documentKey, string id)
        {
            return Path.Combine(_root, AppDataPaths.SafeFileName(documentKey), id + ".ref.json");
        }
    }
}
