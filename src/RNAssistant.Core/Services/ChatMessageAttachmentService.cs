using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    // Derived from the accepted user message. No filename registry, mutable alias
    // or second attachment store is allowed to choose an original.
    public static class ChatMessageAttachmentService
    {
        public const string TargetPrefix = "message attachment: ";

        public static string Target(ChatMessage message, ChatAttachment attachment)
        {
            return TargetPrefix + (attachment.FileName ?? string.Empty).Trim() +
                " [sent " + message.CreatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) +
                "; uploaded " + attachment.CreatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) + "]";
        }

        public static void Link(ChatSession session, ChatMessage message)
        {
            var attachments = (message?.Attachments ?? new List<ChatAttachment>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Id)).ToList();
            if (attachments.Count == 0) return;
            session.Artifacts = session.Artifacts ?? new List<ChatArtifact>();
            if ((session.Messages ?? new List<ChatMessage>()).Count(item => item != null &&
                string.Equals(item.Id, message.Id, StringComparison.OrdinalIgnoreCase)) != 1)
                throw new InvalidOperationException("Attachment source message identity is ambiguous.");
            if (attachments.GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1))
                throw new InvalidOperationException("Attachment identity is ambiguous within its source message.");
            foreach (var attachment in attachments)
            {
                var id = "attachment_" + attachment.Id;
                var matches = session.Artifacts.Where(item => item != null &&
                    string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
                if (matches.Count > 1) throw new InvalidOperationException("Attachment artifact identity is ambiguous: " + id);
                var artifact = matches.Count == 1 ? matches[0] : null;
                if (artifact != null && !string.IsNullOrEmpty(artifact.AvailabilityIssue)) continue;
                var expectedKind = string.Equals(attachment.Kind, "image", StringComparison.OrdinalIgnoreCase)
                    ? ChatArtifactKinds.Image : ChatArtifactKinds.Attachment;
                if (artifact == null)
                {
                    artifact = new ChatArtifact
                    {
                        Id = id, Kind = expectedKind,
                        Title = string.IsNullOrWhiteSpace(attachment.FileName) ? "Вложение" : attachment.FileName,
                        MimeType = attachment.ContentType, SourceMessageId = message.Id,
                        RelativePath = attachment.RelativePath,
                        ContentSha256 = attachment.ContentSha256, ContentByteLength = attachment.ContentByteLength,
                        MetadataJson = JsonConvert.SerializeObject(new
                        {
                            attachmentId = attachment.Id, attachment.Kind, attachment.Size,
                            attachment.PageCount, attachment.ExtractedCharCount, attachment.TextTruncated
                        })
                    };
                    session.Artifacts.Add(artifact);
                }
                else if (!string.Equals(artifact.Kind, expectedKind, StringComparison.OrdinalIgnoreCase) ||
                    ((artifact.DocumentAuthorityId != session.DocumentAuthorityId || string.IsNullOrWhiteSpace(artifact.DocumentAuthorityId)) &&
                        !string.Equals(artifact.SourceMessageId, message.Id, StringComparison.OrdinalIgnoreCase)) ||
                    !string.Equals(artifact.MimeType, attachment.ContentType, StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(artifact.ContentSha256) ||
                    !string.Equals(artifact.ContentSha256, attachment.ContentSha256, StringComparison.OrdinalIgnoreCase) ||
                    !artifact.ContentByteLength.HasValue || !attachment.ContentByteLength.HasValue ||
                    artifact.ContentByteLength.Value != attachment.ContentByteLength.Value ||
                    !string.Equals(AttachmentId(artifact), attachment.Id, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Attachment artifact cannot be rebound to different immutable evidence: " + id);
                message.ResourceRefs = message.ResourceRefs ?? new List<ResourceRef>();
                var reference = ChatResourceUri.CreateArtifactRevision(session, artifact);
                if (!message.ResourceRefs.Any(item => item != null && item.Uri == reference.Uri &&
                    item.Revision == reference.Revision)) message.ResourceRefs.Add(reference);
            }
        }

        public sealed class Binding
        {
            public ChatMessage Message { get; internal set; }
            public ChatAttachment Attachment { get; internal set; }
            public ResourceRef Reference { get; internal set; }
            public string Target { get; internal set; }
        }

        public static IReadOnlyList<Binding> WorkspaceBindings(ChatSession session)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.WorkspaceId) ||
                !string.IsNullOrWhiteSpace(session.DocumentAuthorityId))
                throw new InvalidOperationException("A workspace chat is required for attachment binding.");
            return (session.Messages ?? new List<ChatMessage>()).AsEnumerable().Reverse()
                .Where(message => message != null && !message.ProtocolMessage && message.Role == "user")
                .SelectMany(message => (message.Attachments ?? new List<ChatAttachment>())
                    .Where(attachment => attachment != null && attachment.VisualSelectionSource?.IsExact == true)
                    .Select(attachment => new Binding { Message = message, Attachment = attachment,
                        Target = Target(message, attachment) })).ToArray();
        }

        public static Binding ResolveWorkspace(ChatSession session, string target)
        {
            var matches = WorkspaceBindings(session).Where(item => item.Target == target).Take(2).ToArray();
            if (matches.Length == 0)
                throw new ResourceRequestException("The selected workspace attachment is unavailable.",
                    "resource_attachment_not_found", false);
            if (matches.Length != 1)
                throw new ResourceRequestException("Multiple workspace attachments have this target.",
                    "resource_target_ambiguous", false);
            BindWorkspace(session, matches[0]);
            return matches[0];
        }

        public static void BindWorkspace(ChatSession session, Binding binding)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.WorkspaceId) ||
                !string.IsNullOrWhiteSpace(session.DocumentAuthorityId))
                throw new InvalidOperationException("A workspace chat is required for attachment binding.");
            var message = binding?.Message;
            var attachment = binding?.Attachment;
            if (message == null || attachment == null || string.IsNullOrWhiteSpace(message.Id) ||
                session.Messages.Count(item => item != null && item.Id == message.Id) != 1 ||
                !session.Messages.Any(item => object.ReferenceEquals(item, message)) ||
                string.IsNullOrWhiteSpace(attachment.Id) || attachment.Kind != "text" ||
                !string.Equals(attachment.ContentType, "application/json", StringComparison.OrdinalIgnoreCase) ||
                attachment.Size < 1 || attachment.Size > HtmlVisualSelectionPolicy.MaximumBodyBytes ||
                !string.IsNullOrWhiteSpace(attachment.DraftChatId) ||
                string.IsNullOrWhiteSpace(attachment.ContentSha256) ||
                attachment.ContentByteLength != attachment.Size ||
                attachment.VisualSelectionSource?.IsExact != true ||
                message.Attachments == null ||
                message.Attachments.Count(item => item != null && item.Id == attachment.Id) != 1 ||
                !message.Attachments.Any(item => object.ReferenceEquals(item, attachment)))
                throw new ResourceRequestException("The workspace selection has no unambiguous committed original.",
                    "resource_attachment_binding_invalid", false);
            var artifacts = (session.Artifacts ?? new List<ChatArtifact>()).Where(item => item != null &&
                item.Id == "attachment_" + attachment.Id).Take(2).ToArray();
            if (artifacts.Length != 1 || artifacts[0].Revision != 1 ||
                artifacts[0].Kind != ChatArtifactKinds.Attachment ||
                artifacts[0].DocumentAuthorityId != null || artifacts[0].WorkspaceId != null ||
                artifacts[0].SourceMessageId != message.Id ||
                artifacts[0].MimeType != attachment.ContentType ||
                artifacts[0].RelativePath != null ||
                artifacts[0].ContentSha256 != attachment.ContentSha256 ||
                artifacts[0].ContentByteLength != attachment.ContentByteLength ||
                AttachmentId(artifacts[0]) != attachment.Id)
                throw new ResourceRequestException("The workspace selection artifact is unavailable or ambiguous.",
                    "resource_attachment_binding_invalid", false);
            var expected = ChatResourceUri.CreateArtifactRevision(session, artifacts[0]);
            var references = (message.ResourceRefs ?? new List<ResourceRef>()).Where(item => item != null &&
                item.IsExact && item.Uri == expected.Uri && item.Revision == expected.Revision).Take(2).ToArray();
            if (references.Length != 1)
                throw new ResourceRequestException("The workspace selection is missing its exact message link.",
                    "resource_attachment_binding_invalid", false);
            binding.Reference = expected;
        }

        private static string AttachmentId(ChatArtifact artifact)
        {
            try { return (string)JObject.Parse(artifact?.MetadataJson ?? "{}")
                .GetValue("attachmentId", StringComparison.OrdinalIgnoreCase) ?? string.Empty; }
            catch (JsonException) { return string.Empty; }
        }
    }
}
