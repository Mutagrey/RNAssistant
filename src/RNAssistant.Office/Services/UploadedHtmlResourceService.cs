using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed class UploadedHtmlResourceService
    {
        private const int MaximumPreviewCharacters = 32000;
        private const int MaximumImportCharacters = 300000;

        private readonly ResourceGatewayService _gateway;
        private readonly Func<ChatAttachment, int, string> _readAttachmentText;

        public UploadedHtmlResourceService(
            ResourceGatewayService gateway,
            Func<ChatAttachment, int, string> readAttachmentText)
        {
            _gateway = gateway ?? throw new ArgumentNullException("gateway");
            _readAttachmentText = readAttachmentText ?? throw new ArgumentNullException("readAttachmentText");
        }

        public UploadedHtmlSourcePreviewDto Preview(ChatSession session, string sourceResourceUri)
        {
            var source = Resolve(session, sourceResourceUri);
            var selection = _gateway.Read(session, new ResourceReadRequest
            {
                Reference = ChatResourceUri.CreateArtifactRevision(session, source.Artifact),
                Representation = ResourceRepresentations.Text,
                MaxChars = MaximumPreviewCharacters
            });
            var result = selection == null ? null : selection.Result;
            if (result == null || !result.RawContentIncluded)
            {
                throw new InvalidOperationException("Uploaded HTML source is unavailable.");
            }
            return new UploadedHtmlSourcePreviewDto
            {
                SourceResourceUri = source.ResourceUri,
                MimeType = source.Artifact.MimeType,
                ContentSha256 = source.Artifact.ContentSha256,
                Text = result.Text ?? string.Empty,
                ReturnedCharacters = result.ReturnedCharacters,
                TotalCharacters = result.TotalCharacters,
                Complete = result.Complete,
                Truncated = result.Truncated
            };
        }

        public UploadedHtmlImportResult Import(
            ChatSession session,
            string sourceResourceUri,
            string expectedActiveHtmlArtifactId,
            string targetPath)
        {
            if (session == null) throw new InvalidOperationException("Chat session is required.");
            if (!string.Equals(
                expectedActiveHtmlArtifactId ?? string.Empty,
                session.ActiveHtmlArtifactId ?? string.Empty,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("HTML workspace changed; reload it before importing the upload.");
            }
            HtmlWorkspaceArtifactService.EnsureMutable(session);
            var source = Resolve(session, sourceResourceUri);
            var normalizedPath = HtmlArtifactToolExecutor.NormalizeWorkspacePath(targetPath);
            if (!normalizedPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase) &&
                !normalizedPath.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Imported HTML target path must end with .html or .htm.");
            }
            var existing = (session.HtmlWorkspace == null
                    ? new List<HtmlWorkspaceFile>()
                    : session.HtmlWorkspace.Files ?? new List<HtmlWorkspaceFile>())
                .Any(item => item != null && string.Equals(
                    HtmlArtifactToolExecutor.NormalizeWorkspacePath(item.Path),
                    normalizedPath,
                    StringComparison.OrdinalIgnoreCase));
            if (existing)
            {
                throw new InvalidOperationException("HTML workspace already contains this path; choose a new import path.");
            }
            if (source.Attachment.TextTruncated)
            {
                throw new InvalidOperationException("Uploaded HTML extraction is truncated and cannot be imported exactly.");
            }
            var content = _readAttachmentText(source.Attachment, MaximumImportCharacters + 1) ?? string.Empty;
            if (content.Length > MaximumImportCharacters || source.Attachment.ExtractedCharCount > MaximumImportCharacters)
            {
                throw new InvalidOperationException("Uploaded HTML is too large to import. Limit is 300000 characters.");
            }
            if (source.Attachment.ExtractedCharCount > 0 &&
                source.Attachment.ExtractedCharCount != content.Length)
            {
                throw new InvalidOperationException("Complete uploaded HTML source is unavailable; import was not performed.");
            }

            var imported = HtmlArtifactToolExecutor.UpsertFile(
                session,
                normalizedPath,
                "html",
                content,
                true);
            var revision = (session.Artifacts ?? new List<ChatArtifact>()).Single(item =>
                item != null &&
                string.Equals(item.Id, session.ActiveHtmlArtifactId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Kind, ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase));
            var metadata = ParseMetadata(revision.MetadataJson);
            metadata["importedFromUri"] = source.ResourceUri;
            metadata["importedFromArtifactId"] = source.Artifact.Id;
            metadata["importedSourceContentSha256"] = source.Artifact.ContentSha256;
            metadata["importedPath"] = imported.Path;
            revision.MetadataJson = metadata.ToString(Formatting.None);
            revision.RelatedArtifactIds = revision.RelatedArtifactIds ?? new List<string>();
            if (!revision.RelatedArtifactIds.Contains(source.Artifact.Id, StringComparer.OrdinalIgnoreCase))
            {
                revision.RelatedArtifactIds.Add(source.Artifact.Id);
            }
            return new UploadedHtmlImportResult
            {
                ImportedPath = imported.Path,
                ImportedFromResourceUri = source.ResourceUri,
                RevisionArtifactId = revision.Id
            };
        }

        private UploadedHtmlSource Resolve(ChatSession session, string sourceResourceUri)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.Id))
                throw new InvalidOperationException("A persisted chat session is required.");
            if (string.IsNullOrWhiteSpace(sourceResourceUri))
                throw new InvalidOperationException("An exact uploaded HTML resource URI is required.");
            var resolved = _gateway.Resolve(session, sourceResourceUri);
            var descriptor = resolved == null ? null : resolved.Resource;
            var canonicalUri = descriptor == null || descriptor.Reference == null
                ? null
                : descriptor.Reference.Uri;
            if (!string.Equals(canonicalUri, sourceResourceUri, StringComparison.Ordinal))
                throw new InvalidOperationException("The uploaded HTML URI is not the exact canonical revision.");

            string artifactId;
            int revision;
            if (!ChatResourceUri.TryParseArtifactRevision(
                session.Id,
                new ResourceRef(sourceResourceUri),
                out artifactId,
                out revision))
            {
                throw new InvalidOperationException("The uploaded HTML URI is invalid for this chat.");
            }
            var artifacts = (session.Artifacts ?? new List<ChatArtifact>()).Where(item =>
                item != null && string.Equals(item.Id, artifactId, StringComparison.OrdinalIgnoreCase) &&
                item.Revision == revision).ToList();
            if (artifacts.Count != 1) throw new InvalidOperationException("Uploaded HTML artifact identity is ambiguous.");
            var artifact = artifacts[0];
            if (!string.Equals(artifact.Kind, ChatArtifactKinds.Attachment, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(artifact.Kind, ChatArtifactKinds.File, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Only an immutable uploaded HTML original can be imported.");
            }

            var attachmentId = MetadataText(artifact, "attachmentId");
            var messages = (session.Messages ?? new List<ChatMessage>()).Where(message =>
                message != null && string.Equals(message.Id, artifact.SourceMessageId, StringComparison.OrdinalIgnoreCase)).ToList();
            var attachments = messages.SelectMany(message => message.Attachments ?? new List<ChatAttachment>())
                .Where(item => item != null && string.Equals(item.Id, attachmentId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (messages.Count != 1 || attachments.Count != 1)
                throw new InvalidOperationException("Uploaded HTML attachment identity is unavailable or ambiguous.");
            var attachment = attachments[0];
            if (!string.Equals(attachment.Kind, "text", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(artifact.ContentSha256) ||
                !string.Equals(attachment.ContentSha256, artifact.ContentSha256, StringComparison.OrdinalIgnoreCase) ||
                attachment.ContentByteLength != artifact.ContentByteLength ||
                string.Equals(attachment.Status, "error", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(attachment.Status, "missing", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Uploaded HTML source evidence is incomplete.");
            }
            var mediaType = (artifact.MimeType ?? attachment.ContentType ?? string.Empty).Split(';')[0].Trim();
            var extension = Path.GetExtension(attachment.FileName ?? artifact.Title ?? string.Empty);
            if (!string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".html", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".htm", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The selected upload is not HTML.");
            }
            return new UploadedHtmlSource
            {
                Artifact = artifact,
                Attachment = attachment,
                ResourceUri = canonicalUri
            };
        }

        private static JObject ParseMetadata(string json)
        {
            try { return JObject.Parse(json ?? "{}"); }
            catch (JsonException) { return new JObject(); }
        }

        private static string MetadataText(ChatArtifact artifact, string name)
        {
            var metadata = ParseMetadata(artifact == null ? null : artifact.MetadataJson);
            var value = metadata.GetValue(name, StringComparison.OrdinalIgnoreCase);
            return value == null ? null : (string)value;
        }

        private sealed class UploadedHtmlSource
        {
            public ChatArtifact Artifact { get; set; }
            public ChatAttachment Attachment { get; set; }
            public string ResourceUri { get; set; }
        }
    }

    internal sealed class UploadedHtmlImportResult
    {
        public string ImportedPath { get; set; }
        public string ImportedFromResourceUri { get; set; }
        public string RevisionArtifactId { get; set; }
    }
}
