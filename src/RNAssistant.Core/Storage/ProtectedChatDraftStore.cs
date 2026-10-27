using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    // Unsent UI state only. Chat events remain the sole durable conversation history.
    public sealed class ProtectedChatDraftStore
    {
        private const int MaxRecords = 512;
        private const int MaxTextLength = 262144;
        private const int MaxFileBytes = 32 * 1024 * 1024;
        private readonly string _path;
        private readonly Func<byte[], byte[]> _protect;
        private readonly Func<byte[], byte[]> _unprotect;

        public ProtectedChatDraftStore(AppDataPaths paths)
            : this(paths, ProtectedSecretStore.ProtectBytes, ProtectedSecretStore.UnprotectBytes) { }

        // An injected codec lets the host-neutral Harness exercise storage without Windows DPAPI.
        public ProtectedChatDraftStore(AppDataPaths paths, Func<byte[], byte[]> protect, Func<byte[], byte[]> unprotect)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            _path = paths.ChatDraftFile;
            _protect = protect ?? throw new ArgumentNullException("protect");
            _unprotect = unprotect ?? throw new ArgumentNullException("unprotect");
        }

        public IReadOnlyList<ChatInputDraft> Load()
        {
            using (StorageFileSystem.AcquireWriteLock(_path + ".lck")) return ReadLocked();
        }

        public void Save(ChatInputDraft draft)
        {
            Validate(draft);
            using (StorageFileSystem.AcquireWriteLock(_path + ".lck"))
            {
                var records = ReadLocked();
                records.RemoveAll(item => SameKey(item, draft));
                if (!draft.IsEmpty) records.Add(draft);
                if (records.Count > MaxRecords) throw new InvalidOperationException("Too many unsent chat drafts.");
                WriteLocked(records);
            }
        }

        public void DeleteOffice(string chatId) { Delete(item => item.Owner == "office" && item.ChatId == chatId); }
        public void DeleteWorkspace(string workspaceId, string sessionId = null)
        {
            Delete(item => item.Owner == "workspace" && item.WorkspaceId == workspaceId &&
                (sessionId == null || item.SessionId == sessionId));
        }

        private void Delete(Func<ChatInputDraft, bool> predicate)
        {
            using (StorageFileSystem.AcquireWriteLock(_path + ".lck"))
            {
                if (!File.Exists(_path)) return;
                var records = ReadLocked();
                if (records.RemoveAll(item => predicate(item)) > 0) WriteLocked(records);
            }
        }

        private List<ChatInputDraft> ReadLocked()
        {
            if (!File.Exists(_path)) return new List<ChatInputDraft>();
            if (StorageFileSystem.IsReparsePoint(_path)) throw new IOException("Chat draft file cannot be a reparse point.");
            var info = new FileInfo(_path);
            if (info.Length <= 0 || info.Length > MaxFileBytes) throw new InvalidDataException("Chat draft file has an invalid size.");
            var json = new UTF8Encoding(false, true).GetString(_unprotect(File.ReadAllBytes(_path)));
            var envelope = JsonConvert.DeserializeObject<ChatDraftEnvelope>(json);
            if (envelope == null || envelope.Version != 1 || envelope.Records == null || envelope.Records.Count > MaxRecords)
                throw new InvalidDataException("Chat draft file has an unsupported format.");
            foreach (var item in envelope.Records) Validate(item);
            if (envelope.Records.GroupBy(Key).Any(group => group.Count() != 1))
                throw new InvalidDataException("Chat draft file has duplicate identities.");
            return envelope.Records;
        }

        private void WriteLocked(List<ChatInputDraft> records)
        {
            if (records.Count == 0) { if (File.Exists(_path)) File.Delete(_path); return; }
            var json = JsonConvert.SerializeObject(new ChatDraftEnvelope { Version = 1, Records = records });
            var bytes = _protect(Encoding.UTF8.GetBytes(json));
            if (bytes == null || bytes.Length > MaxFileBytes) throw new InvalidOperationException("Chat drafts exceed the storage limit.");
            StorageFileSystem.WriteAtomic(_path, temp => File.WriteAllBytes(temp, bytes));
        }

        private static string Key(ChatInputDraft item)
        {
            return item.Owner == "office" ? "office:" + item.ChatId :
                "workspace:" + JsonConvert.SerializeObject(new[] { item.WorkspaceId, item.SessionId });
        }
        private static bool SameKey(ChatInputDraft left, ChatInputDraft right) { return Key(left) == Key(right); }

        private static void Validate(ChatInputDraft item)
        {
            if (item == null || (item.Owner != "office" && item.Owner != "workspace") ||
                item.Text == null || item.Text.Length > MaxTextLength ||
                (item.Owner == "office" && (string.IsNullOrWhiteSpace(item.ChatId) || item.ChatId.Length > 256 ||
                    item.WorkspaceId != null || item.SessionId != null || item.PendingInput != null)) ||
                (item.Owner == "workspace" && (string.IsNullOrWhiteSpace(item.WorkspaceId) || item.WorkspaceId.Length > 256 ||
                    string.IsNullOrWhiteSpace(item.SessionId) || item.SessionId.Length > 256 || item.ChatId != null ||
                    (item.Mode != "agent" && item.Mode != "plan") || item.PendingOfficeInput != null)) ||
                item.ResourceDraftIds == null || item.ResourceDraftIds.Count > 10 ||
                item.ResourceDraftIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128) ||
                item.NextAnnotationNumber < 1 || item.NextAnnotationNumber > 1000001)
                throw new InvalidDataException("Chat draft has an invalid identity or content.");
            var pending = item.PendingInput;
            if (pending != null && (item.Owner != "workspace" || string.IsNullOrWhiteSpace(pending.RequestId) ||
                pending.RequestId.Length > 128 || pending.Message == null || pending.Message.Length > MaxTextLength ||
                (pending.Mode != "agent" && pending.Mode != "plan") ||
                pending.ResourceDraftIds == null || pending.ResourceDraftIds.Count > 10 ||
                pending.ResourceDraftIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128) ||
                (pending.EditMessageId != null && (string.IsNullOrWhiteSpace(pending.EditMessageId) ||
                    pending.EditMessageId.Length > 128 || !pending.ExpectedRevision.HasValue ||
                    pending.ResourceDraftIds.Count > 0)) ||
                (pending.ExpectedRevision.HasValue && (pending.ExpectedRevision < 0 || pending.EditMessageId == null))))
                throw new InvalidDataException("Chat draft pending input is invalid.");
            var officePending = item.PendingOfficeInput;
            if (officePending != null && (item.Owner != "office" || string.IsNullOrWhiteSpace(officePending.OperationId) ||
                officePending.OperationId.Length > 128 || officePending.Text == null || officePending.Text.Length > MaxTextLength ||
                (officePending.Delivery != "Steer" && officePending.Delivery != "Queue") ||
                officePending.ResourceDraftIds == null || officePending.ResourceDraftIds.Count > 10 ||
                officePending.ResourceDraftIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128)))
                throw new InvalidDataException("Chat draft pending Office input is invalid.");
            var visual = item.PendingVisualAnnotation;
            if (visual != null)
            {
                if (string.IsNullOrWhiteSpace(visual.ArtifactId) ||
                    visual.ArtifactId.Length > 256 || visual.ArtifactRevision < 1 ||
                    visual.Number < 1 || visual.Number >= item.NextAnnotationNumber ||
                    visual.Comment == null || visual.Comment.Length > 2048 ||
                    (visual.ReplacingDraftId != null &&
                     (visual.ReplacingDraftId.Length == 0 || visual.ReplacingDraftId.Length > 128 ||
                      !item.ResourceDraftIds.Contains(visual.ReplacingDraftId))))
                    throw new InvalidDataException("Pending visual annotation is invalid.");
                try { HtmlVisualSelectionPolicy.Capture(visual.Selection, "HTML", visual.Comment, visual.Number); }
                catch (InvalidOperationException error)
                { throw new InvalidDataException("Pending visual annotation is invalid.", error); }
            }
        }

        private sealed class ChatDraftEnvelope
        {
            public int Version { get; set; }
            public List<ChatInputDraft> Records { get; set; }
        }
    }

    public sealed class ChatInputDraft
    {
        public string Owner { get; set; }
        public string ChatId { get; set; }
        public string WorkspaceId { get; set; }
        public string SessionId { get; set; }
        public string Text { get; set; }
        public string Mode { get; set; }
        public bool Reasoning { get; set; }
        public List<string> ResourceDraftIds { get; set; } = new List<string>();
        public int NextAnnotationNumber { get; set; } = 1;
        public PendingVisualAnnotation PendingVisualAnnotation { get; set; }
        public PendingChatInput PendingInput { get; set; }
        public PendingOfficeChatInput PendingOfficeInput { get; set; }
        [JsonIgnore]
        public bool IsEmpty { get { return string.IsNullOrEmpty(Text) && ResourceDraftIds.Count == 0 && NextAnnotationNumber == 1 && PendingVisualAnnotation == null && PendingInput == null &&
            PendingOfficeInput == null && (Owner == "office" || (Mode == "agent" && !Reasoning)); } }
    }

    public sealed class PendingVisualAnnotation
    {
        public string ArtifactId { get; set; }
        public int ArtifactRevision { get; set; }
        public int Number { get; set; }
        public string Comment { get; set; }
        public HtmlVisualSelection Selection { get; set; }
        public string ReplacingDraftId { get; set; }
    }

    public sealed class PendingChatInput
    {
        public string RequestId { get; set; }
        // This relation is saved atomically with the current draft. Text equality
        // alone cannot distinguish a later identical draft after WebView restart.
        public bool MatchesDraftVersion { get; set; }
        public string Message { get; set; }
        public string EditMessageId { get; set; }
        public long? ExpectedRevision { get; set; }
        public List<string> ResourceDraftIds { get; set; } = new List<string>();
        public string Mode { get; set; }
        public bool Reasoning { get; set; }
    }

    public sealed class PendingOfficeChatInput
    {
        public string OperationId { get; set; }
        public string Text { get; set; }
        public List<string> ResourceDraftIds { get; set; } = new List<string>();
        public string Delivery { get; set; }
    }
}
