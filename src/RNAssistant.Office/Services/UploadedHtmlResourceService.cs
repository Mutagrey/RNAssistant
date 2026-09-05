using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed class UploadedHtmlResourceService
    {
        private const int MaximumImportCharacters = 300000;

        private readonly ResourceGatewayService _gateway;

        public UploadedHtmlResourceService(ResourceGatewayService gateway)
        {
            _gateway = gateway ?? throw new ArgumentNullException("gateway");
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
            var normalizedPath = HtmlWorkspaceToolService.NormalizeWorkspacePath(targetPath);
            if (!normalizedPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase) &&
                !normalizedPath.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Imported HTML target path must end with .html or .htm.");
            }
            var existing = (session.HtmlWorkspace == null
                    ? new List<HtmlWorkspaceFile>()
                    : session.HtmlWorkspace.Files ?? new List<HtmlWorkspaceFile>())
                .Any(item => item != null && string.Equals(
                    HtmlWorkspaceToolService.NormalizeWorkspacePath(item.Path),
                    normalizedPath,
                    StringComparison.OrdinalIgnoreCase));
            if (existing)
            {
                throw new InvalidOperationException("HTML workspace already contains this path; choose a new import path.");
            }
            var content = ReadSource(session, source);
            var imported = HtmlWorkspaceToolService.UpsertFile(
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

        private string ReadSource(ChatSession session, UploadedHtmlSource source)
        {
            var expectedLength = source.Attachment.ExtractedCharCount;
            var expectedHash = source.Attachment.ExtractedTextSha256;
            if (source.Attachment.TextTruncated || expectedLength < 0 || string.IsNullOrWhiteSpace(expectedHash) ||
                !source.Attachment.ExtractedTextByteLength.HasValue)
                throw new InvalidOperationException("Complete uploaded HTML text evidence is required for import.");
            if (expectedLength > MaximumImportCharacters)
                throw new InvalidOperationException("Uploaded HTML is too large to import. Limit is 300000 characters.");
            if (source.Attachment.ExtractedTextByteLength.Value < 0 ||
                source.Attachment.ExtractedTextByteLength.Value > (long)expectedLength * 4)
                throw new InvalidOperationException("Uploaded HTML text byte evidence exceeds its character bound.");

            var reference = ChatResourceUri.CreateArtifactRevision(session, source.Artifact);
            var text = new StringBuilder(expectedLength);
            string cursor = null;
            do
            {
                var read = _gateway.Read(session, new ResourceReadRequest
                {
                    Reference = reference, Representation = ResourceRepresentations.Text,
                    MaxChars = 32000, Cursor = cursor
                })?.Result;
                if (read?.Resource?.Reference == null || read.Resource.Reference.Uri != reference.Uri ||
                    read.Resource.Reference.Revision != reference.Revision || !read.RawContentIncluded ||
                    read.Representation != ResourceRepresentations.Text || read.Text == null ||
                    !string.Equals(read.ContentSha256, expectedHash, StringComparison.OrdinalIgnoreCase) ||
                    read.Offset != text.Length || read.ReturnedCharacters != read.Text.Length ||
                    read.ReturnedCharacters > 32000 || read.TotalCharacters != expectedLength ||
                    read.ReturnedCharacters > expectedLength - text.Length)
                    throw new InvalidOperationException("RESOURCE_SNAPSHOT_UNAVAILABLE: uploaded HTML text evidence changed or is incomplete.");
                text.Append(read.Text);
                if (read.Complete)
                {
                    if (read.Truncated || !string.IsNullOrEmpty(read.NextCursor) || text.Length != expectedLength)
                        throw new InvalidOperationException("RESOURCE_SNAPSHOT_UNAVAILABLE: uploaded HTML text is incomplete.");
                    break;
                }
                if (read.ReturnedCharacters == 0 || text.Length >= expectedLength || string.IsNullOrEmpty(read.NextCursor))
                    throw new InvalidOperationException("RESOURCE_SNAPSHOT_UNAVAILABLE: uploaded HTML continuation is incomplete.");
                cursor = read.NextCursor;
            } while (true);
            var content = text.ToString();
            if (!string.Equals(TextPatternEngine.Sha256(content), expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("RESOURCE_SNAPSHOT_UNAVAILABLE: uploaded HTML text failed integrity verification.");
            return content;
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
