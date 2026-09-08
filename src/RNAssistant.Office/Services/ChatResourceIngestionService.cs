using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

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
            byte[] bytes)
        {
            var chatId = RequireChatId(session);
            var resource = _attachments.Import(fileName, contentType, bytes, chatId);
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
            return _attachments.LoadDrafts(resourceDraftIds, RequireChatId(session));
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
                resource == null || !string.Equals(resource.DraftChatId, chatId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Every staged resource must belong to the target chat.");
            }
            _attachments.CommitToCas(message);
            ChatResourceReferenceService.LinkMessageResources(session, messageIndex);
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
}
