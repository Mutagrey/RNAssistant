using RNAssistant.Core.Persistence;
using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;

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
            ConversationMetadata session,
            string fileName,
            string contentType,
            byte[] bytes,
            ResourceRef visualSelectionSource = null)
        {
            var chatId = RequireChatId(session?.Id);
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

        public IReadOnlyList<ChatAttachment> LoadDrafts(
            ChatSession session,
            IEnumerable<string> resourceDraftIds)
        {
            var drafts = _attachments.LoadDrafts(resourceDraftIds, RequireChatId(session?.Id));
            foreach (var draft in drafts.Where(item => item.VisualSelectionSource != null))
            {
                RequireVisualSource(session, draft.VisualSelectionSource);
            }
            return drafts;
        }
        private ChatArtifact RequireVisualSource(ChatSession session, ResourceRef source)
        {
            if (source?.IsExact != true || !DocumentArtifactStore.Owns(session, source)) throw new InvalidOperationException("Источник выделения принадлежит другому документу.");
            ChatArtifact artifact;
            try { artifact = _documentArtifacts.Read(ConversationMetadata.From(session), source, false); }
            catch (System.IO.InvalidDataException error) { throw new InvalidOperationException("Точный HTML-источник выделения недоступен.", error); }
            if ((artifact.Kind != ChatArtifactKinds.HtmlWorkspace && !BrowserTargetResolver.IsHtmlResource(artifact)) ||
                _documentArtifacts.IsRemoved(ConversationMetadata.From(session), artifact))
                throw new InvalidOperationException("Точный HTML-источник выделения недоступен.");
            return artifact;
        }
        public ChatAttachment StageBrowserVisualSelection(ChatSession session, BrowserAnnotationCapture capture, BrowserAnnotationRequest request)
        {
            if (session == null || request == null || capture?.Owner?.Kind != BrowserOwnerKind.Document ||
                capture.Owner.ChatId != session.Id || capture.Owner.ScopeId != session.DocumentAuthorityId || capture.Page == null ||
                capture.Page.WorkspaceId != null || !BrowserContentSession.ValidPath(capture.Page.Path))
                throw new InvalidOperationException("Аннотация принадлежит другому документу или чату.");
            var artifact = RequireVisualSource(session, capture.Source);
            capture.Page.Identity(capture.Source);
            return _attachments.StageVisualSelection(session.Id, request.Selection, artifact.Title,
                request.Comment, request.AnnotationNumber, capture.Source, capture.Page);
        }

        public void Discard(ConversationMetadata session, string resourceDraftId)
        {
            _attachments.DeleteDraft(resourceDraftId, RequireChatId(session?.Id));
        }

        public void CommitAndLink(ChatSession session, ChatMessage message, int messageIndex)
        {
            if (message == null) throw new ArgumentNullException("message");
            var chatId = RequireChatId(session?.Id);
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

        private static string RequireChatId(string chatId)
        {
            if (string.IsNullOrWhiteSpace(chatId))
            {
                throw new InvalidOperationException("A chat session is required for resource ingestion.");
            }
            return chatId;
        }
    }
}
