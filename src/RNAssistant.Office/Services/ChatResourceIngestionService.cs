using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    internal sealed class ChatResourceIngestionService
    {
        private readonly AttachmentStore _attachments;
        private readonly DocumentArtifactStore _documentArtifacts;

        public ChatResourceIngestionService(AttachmentStore attachments, DocumentArtifactStore documentArtifacts)
        {
            _attachments = attachments ?? throw new ArgumentNullException("attachments");
            _documentArtifacts = documentArtifacts ?? throw new ArgumentNullException(nameof(documentArtifacts));
        }

        public ChatAttachment Stage(
            ChatSession session,
            string fileName,
            string contentType,
            byte[] bytes,
            ResourceRef visualSelectionSource = null)
        {
            var chatId = RequireChatId(session);
            var resource = _attachments.Import(fileName, contentType, bytes, chatId);
            resource.VisualSelectionSource = visualSelectionSource?.Copy();
            try { _attachments.SaveDraftMetadata(resource); }
            catch
            {
                _attachments.DeleteDrafts(new ChatMessage { Attachments = new List<ChatAttachment> { resource } });
                throw;
            }
            return resource;
        }

        public ChatAttachment StageHtmlVisualSelection(ChatSession session, StageHtmlVisualSelectionPayload request)
        {
            if (session == null || request == null || request.ChatId != session.Id ||
                string.IsNullOrWhiteSpace(request.ArtifactId) || request.ArtifactRevision < 1 ||
                request.PreviewGeneration < 1)
                throw new ArgumentException("Требуются чат, источник и поколение HTML-preview.");
            if (!string.Equals(session.ActiveHtmlArtifactId, request.ArtifactId, StringComparison.Ordinal))
                throw new InvalidOperationException("HTML-preview изменился. Выберите элемент заново.");
            var matches = (session.Artifacts ?? new List<ChatArtifact>()).Where(item => item != null &&
                item.Id == request.ArtifactId && item.Kind == ChatArtifactKinds.HtmlWorkspace &&
                item.Revision == request.ArtifactRevision).Take(2).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException("Версия HTML-источника изменилась. Выберите элемент заново.");
            var source = ChatResourceUri.CreateArtifactRevision(session, matches[0]);
            var body = HtmlVisualSelectionPolicy.Capture(request.Selection, matches[0].Title);
            var draft = Stage(session, "html-selection-" + request.Selection.Tag + ".json",
                "application/json", body, source);
            if (draft.Status != "error") return draft;
            _attachments.DeleteDraft(draft.Id, session.Id);
            throw new InvalidOperationException("Не удалось подготовить снимок HTML-элемента.");
        }

        public IReadOnlyList<ChatAttachment> LoadDrafts(
            ChatSession session,
            IEnumerable<string> resourceDraftIds)
        {
            var drafts = _attachments.LoadDrafts(resourceDraftIds, RequireChatId(session));
            foreach (var draft in drafts.Where(item => item.VisualSelectionSource != null))
            {
                var active = (session.Artifacts ?? new List<ChatArtifact>()).SingleOrDefault(item =>
                    item != null && item.Id == session.ActiveHtmlArtifactId && item.Kind == ChatArtifactKinds.HtmlWorkspace);
                var exact = active == null ? null : ChatResourceUri.CreateArtifactRevision(session, active);
                if (exact == null || !draft.VisualSelectionSource.IsExact ||
                    !string.Equals(draft.VisualSelectionSource.Uri,
                        exact.Uri, StringComparison.Ordinal) ||
                    !string.Equals(draft.VisualSelectionSource.Revision,
                        exact.Revision, StringComparison.Ordinal))
                    throw new InvalidOperationException("Выделение HTML устарело. Удалите его из черновика и выберите элемент заново.");
            }
            return drafts;
        }

        public void Discard(ChatSession session, string resourceDraftId)
        {
            _attachments.DeleteDraft(resourceDraftId, RequireChatId(session));
        }

        public void CommitAndLink(ChatSession session, ChatMessage message, int messageIndex)
        {
            if (message == null) throw new ArgumentNullException("message");
            var chatId = RequireChatId(session);
            if (string.IsNullOrWhiteSpace(session.DocumentAuthorityId))
                throw new InvalidOperationException("A bound document identity is required before committing originals.");
            if (session.Messages == null || messageIndex < 0 || messageIndex >= session.Messages.Count ||
                !object.ReferenceEquals(session.Messages[messageIndex], message))
            {
                throw new InvalidOperationException("The resource message is not at the expected chat position.");
            }
            if ((message.Attachments ?? new List<ChatAttachment>()).Any(resource =>
                resource == null || (string.IsNullOrWhiteSpace(resource.ContentSha256) &&
                    !string.Equals(resource.DraftChatId, chatId, StringComparison.OrdinalIgnoreCase))))
            {
                throw new InvalidOperationException("Every staged resource must belong to the target chat.");
            }
            if (message.Attachments.Any(a => string.IsNullOrWhiteSpace(a.ContentSha256)))
                _attachments.CommitToCas(message);
            ChatResourceReferenceService.LinkMessageResources(session, messageIndex);
            foreach (var source in message.Attachments.Where(item => item.VisualSelectionSource != null)
                .Select(item => item.VisualSelectionSource))
                if (!message.ResourceRefs.Any(reference => reference.Uri == source.Uri && reference.Revision == source.Revision))
                    message.ResourceRefs.Add(source.Copy());
            foreach (var original in message.Attachments)
            {
                var artifact = session.Artifacts.Single(item => item.Id == "attachment_" + original.Id);
                var oldReference = RNAssistant.Core.Services.ChatResourceUri.CreateArtifactRevision(session, artifact);
                var published = _documentArtifacts.PublishOriginal(session, artifact, original);
                session.Artifacts[session.Artifacts.IndexOf(artifact)] = published;
                RNAssistant.Core.Services.ArtifactWorkingSet.Set(session, published, false);
                message.ResourceRefs.RemoveAll(reference => reference.Uri == oldReference.Uri);
                var exact = RNAssistant.Core.Services.ChatResourceUri.CreateArtifactRevision(session, published);
                if (!message.ResourceRefs.Any(reference => reference.Uri == exact.Uri)) message.ResourceRefs.Add(exact);
            }
        }

        public void DeleteDrafts(ChatMessage message)
        {
            _attachments.DeleteDrafts(message);
        }

        private static string RequireChatId(ChatSession session)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.Id))
            {
                throw new InvalidOperationException("A chat session is required for resource ingestion.");
            }
            return session.Id;
        }
    }

    internal static class HtmlVisualSelectionPolicy
    {
        internal static byte[] Capture(HtmlVisualSelection selection, string sourceTitle)
        {
            if (selection == null || !Tag(selection.Tag) || selection.Selector == null ||
                selection.Selector.Length > 1024 || selection.StableUiId == null || selection.StableUiId.Length > 128 ||
                selection.TextExcerpt == null || selection.TextExcerpt.Length > 512 ||
                selection.Attributes == null || selection.Attributes.Count > 12 ||
                selection.Ancestry == null || selection.Ancestry.Count > 8 ||
                (selection.StableUiId.Length > 0 && (!selection.Attributes.ContainsKey("data-ui-id") ||
                    selection.Attributes["data-ui-id"] != selection.StableUiId)) ||
                !Attributes(selection.Attributes) || selection.Ancestry.Any(item =>
                    item == null || !Tag(item.Tag) || item.Attributes == null ||
                    item.Attributes.Count > 12 || !Attributes(item.Attributes)) ||
                (selection.Selector.Length == 0 && selection.StableUiId.Length == 0) ||
                selection.Rect == null || selection.Viewport == null ||
                !Number(selection.Rect.X) || !Number(selection.Rect.Y) ||
                !Number(selection.Rect.Width) || !Number(selection.Rect.Height) ||
                !Number(selection.Viewport.Width) || !Number(selection.Viewport.Height) ||
                !Number(selection.Viewport.Dpr) || selection.Rect.Width < 0 || selection.Rect.Height < 0 ||
                selection.Viewport.Width <= 0 ||
                selection.Viewport.Height <= 0 || selection.Viewport.Dpr <= 0)
                throw new InvalidOperationException("Снимок HTML-элемента недействителен.");
            var serialized = JsonConvert.SerializeObject(selection);
            if (Encoding.UTF8.GetByteCount(serialized) > 16384)
                throw new InvalidOperationException("Снимок HTML-элемента слишком велик.");
            var title = (sourceTitle ?? "HTML-проект").Trim();
            if (title.Length == 0) title = "HTML-проект";
            if (title.Length > 256) title = title.Substring(0, 256);
            return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new {
                type = "rnassistant.html-visual-selection", version = 1,
                source = title, evidence = "untrusted preview DOM; source mapping unverified", selection
            }));
        }

        private static bool Tag(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 32 || value[0] < 'a' || value[0] > 'z') return false;
            return value.Skip(1).All(character => character >= 'a' && character <= 'z' ||
                character >= '0' && character <= '9' || character == '-');
        }

        private static bool Attributes(Dictionary<string, string> attributes)
        {
            return attributes.All(item =>
                (item.Key == "id" || item.Key == "class" || item.Key == "role" ||
                 item.Key == "data-ui-id" || item.Key == "data-testid") &&
                item.Value != null && item.Value.Length <= 128 &&
                !Regex.IsMatch(item.Value, "password|secret|token|api.?key|bearer|credential|authorization|eyJ[A-Za-z0-9_-]{16,}|sk-[A-Za-z0-9_-]{12,}", RegexOptions.IgnoreCase));
        }

        private static bool Number(double value)
        { return !double.IsNaN(value) && !double.IsInfinity(value) && Math.Abs(value) <= 10000000; }
    }
}
