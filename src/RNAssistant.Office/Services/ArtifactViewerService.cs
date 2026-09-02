using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    internal static class ArtifactViewerKinds
    {
        public const string Text = "text";
        public const string Markdown = "markdown";
        public const string Image = "image";
        public const string Pdf = "pdf";
    }

    internal sealed class ArtifactViewerService
    {
        public const int PageCharacters = 32000;
        public const int MaximumDocumentCharacters = 512000;
        public const long MaximumImageBytes = 20L * 1024L * 1024L;

        private static readonly HashSet<string> SourceExtensions = new HashSet<string>(
            new[]
            {
                ".txt", ".log", ".csv", ".tsv", ".xml", ".yaml", ".yml",
                ".ini", ".cfg", ".conf", ".cs", ".vb", ".js", ".mjs", ".cjs",
                ".css", ".scss", ".less", ".py", ".rb", ".java", ".kt", ".kts",
                ".c", ".h", ".cpp", ".hpp", ".sql", ".sh", ".ps1", ".bat"
            },
            StringComparer.OrdinalIgnoreCase);

        private readonly ResourceGatewayService _gateway;
        private readonly Func<ChatAttachment, byte[]> _readAttachmentBytes;
        private readonly ArtifactPdfViewerService _pdfViewer;

        public ArtifactViewerService(ResourceGatewayService gateway)
            : this(gateway, null)
        {
        }

        public ArtifactViewerService(
            ResourceGatewayService gateway,
            Func<ChatAttachment, byte[]> readAttachmentBytes)
        {
            _gateway = gateway ?? throw new ArgumentNullException("gateway");
            _readAttachmentBytes = readAttachmentBytes;
            _pdfViewer = new ArtifactPdfViewerService(_gateway, readAttachmentBytes);
        }

        internal ArtifactViewerService(
            ResourceGatewayService gateway,
            Func<ChatAttachment, byte[]> readAttachmentBytes,
            Func<byte[], int, ArtifactPdfPageRenderResult> renderPdfPage)
        {
            _gateway = gateway ?? throw new ArgumentNullException("gateway");
            _readAttachmentBytes = readAttachmentBytes;
            _pdfViewer = new ArtifactPdfViewerService(_gateway, readAttachmentBytes, renderPdfPage);
        }

        public ArtifactPdfViewerDto ReadPdfInfo(ChatSession session, string resourceUri)
        {
            return _pdfViewer.ReadInfo(session, resourceUri);
        }

        public ArtifactPdfPageDto ReadPdfPage(ChatSession session, string resourceUri, int pageIndex)
        {
            return _pdfViewer.ReadPage(session, resourceUri, pageIndex);
        }

        public ArtifactImageViewerDto ReadImage(ChatSession session, string resourceUri)
        {
            if (_readAttachmentBytes == null)
            {
                throw new InvalidOperationException("Artifact image byte reader is unavailable.");
            }
            var artifact = ResolveExactArtifact(session, resourceUri);
            var descriptor = _gateway.Resolve(session, resourceUri).Resource;
            var attachment = ChatArtifactResourceProvider.FindExactAttachment(session, artifact);
            var mimeType = NormalizeMimeType(attachment == null ? null : attachment.ContentType);
            if (attachment == null ||
                !string.Equals(artifact.Kind, ChatArtifactKinds.Image, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(attachment.Kind, "image", StringComparison.OrdinalIgnoreCase) ||
                !IsImageMimeType(mimeType) ||
                !string.Equals(NormalizeMimeType(artifact.MimeType), mimeType, StringComparison.Ordinal) ||
                !string.Equals(NormalizeMimeType(descriptor == null ? null : descriptor.MimeType), mimeType, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Artifact has no admitted exact image representation.");
            }
            if (!attachment.ContentByteLength.HasValue || attachment.ContentByteLength.Value <= 0 ||
                attachment.ContentByteLength.Value > MaximumImageBytes)
            {
                throw new InvalidOperationException("Artifact image exceeds the admitted viewer bound.");
            }
            var bytes = ReadExactAttachmentBytes(attachment, MaximumImageBytes, "image");
            return new ArtifactImageViewerDto
            {
                ResourceUri = resourceUri,
                ViewerKind = ArtifactViewerKinds.Image,
                Title = descriptor.Title ?? artifact.Title ?? attachment.FileName ?? "Image",
                MimeType = mimeType,
                ContentSha256 = attachment.ContentSha256,
                ByteLength = bytes.LongLength,
                Base64Content = Convert.ToBase64String(bytes)
            };
        }

        public ArtifactViewerPageDto ReadPage(ChatSession session, string resourceUri, string cursor)
        {
            var artifact = ResolveExactArtifact(session, resourceUri);
            var revision = Math.Max(1, artifact.Revision);

            var descriptor = _gateway.Resolve(session, resourceUri).Resource;
            var viewerKind = ViewerKind(descriptor, artifact);
            ChatAttachment exactPdfAttachment = null;
            if (string.Equals(viewerKind, ArtifactViewerKinds.Pdf, StringComparison.Ordinal))
            {
                exactPdfAttachment = _pdfViewer.ValidateTextRead(session, artifact, descriptor);
            }
            var request = new ResourceReadRequest
            {
                Reference = new ResourceRef(resourceUri, revision.ToString(CultureInfo.InvariantCulture)),
                Representation = ResourceRepresentations.Text,
                Cursor = cursor,
                MaxChars = PageCharacters
            };
            var selection = _gateway.Read(session, request);
            var result = selection == null ? null : selection.Result;
            if (result == null || !result.RawContentIncluded || result.Resource == null ||
                result.Resource.Reference == null ||
                !string.Equals(result.Resource.Reference.Uri, resourceUri, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Artifact text representation is unavailable.");
            }
            var text = result.Text ?? string.Empty;
            var offset = result.Offset;
            if (offset >= MaximumDocumentCharacters || text.Length > MaximumDocumentCharacters - offset)
            {
                throw new InvalidOperationException("Artifact viewer continuation exceeds the bounded document limit.");
            }
            if (result.ReturnedCharacters != text.Length ||
                result.TotalCharacters < offset + text.Length ||
                string.IsNullOrWhiteSpace(result.ContentSha256))
            {
                throw new InvalidOperationException("Artifact viewer received inconsistent exact-read evidence.");
            }

            var attachment = exactPdfAttachment ?? ChatArtifactResourceProvider.FindExactAttachment(session, artifact);
            if (string.Equals(artifact.Kind, ChatArtifactKinds.Attachment, StringComparison.OrdinalIgnoreCase) &&
                attachment == null)
            {
                throw new InvalidOperationException("Attachment text evidence is unavailable.");
            }
            if (attachment != null &&
                (!string.Equals(attachment.ExtractedTextSha256, result.ContentSha256, StringComparison.OrdinalIgnoreCase) ||
                 attachment.ExtractedCharCount != result.TotalCharacters))
            {
                throw new InvalidOperationException("Attachment text representation evidence is inconsistent.");
            }
            var pdfSourceComplete = attachment == null ||
                !string.Equals(attachment.Kind, "pdf", StringComparison.OrdinalIgnoreCase) ||
                ((attachment.PageTextLengths ?? new List<int>()).Count == attachment.PageCount);
            var sourceComplete = attachment == null || (!attachment.TextTruncated && pdfSourceComplete);
            var nextOffset = offset + text.Length;
            var viewerLimitReached = !result.Complete && nextOffset >= MaximumDocumentCharacters;
            var complete = result.Complete && sourceComplete;
            return new ArtifactViewerPageDto
            {
                ResourceUri = resourceUri,
                ViewerKind = viewerKind,
                Title = descriptor.Title ?? artifact.Title ?? string.Empty,
                MimeType = descriptor.MimeType ?? artifact.MimeType ?? "text/plain",
                ContentSha256 = result.ContentSha256,
                Text = text,
                Offset = offset,
                ReturnedCharacters = text.Length,
                TotalCharacters = result.TotalCharacters,
                NextCursor = !result.Complete && nextOffset < MaximumDocumentCharacters
                    ? result.NextCursor
                    : null,
                Complete = complete,
                Truncated = !complete,
                SourceComplete = sourceComplete,
                FullReadAllowed = sourceComplete && result.TotalCharacters <= MaximumDocumentCharacters,
                ViewerLimitReached = viewerLimitReached || !sourceComplete,
                MaximumDocumentCharacters = MaximumDocumentCharacters
            };
        }

        private static string ViewerKind(ResourceDescriptor descriptor, ChatArtifact artifact)
        {
            var mimeType = NormalizeMimeType(descriptor == null ? null : descriptor.MimeType);
            var kind = artifact == null ? string.Empty : (artifact.Kind ?? string.Empty).Trim().ToLowerInvariant();
            var extension = Path.GetExtension(descriptor == null ? null : descriptor.Title ?? string.Empty) ?? string.Empty;
            if (mimeType == "text/markdown" || mimeType == "text/x-markdown" ||
                kind == ChatArtifactKinds.PlanDocument || kind == ChatArtifactKinds.Markdown ||
                string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".markdown", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".mdx", StringComparison.OrdinalIgnoreCase))
            {
                return ArtifactViewerKinds.Markdown;
            }
            if (mimeType == "application/pdf" && kind == ChatArtifactKinds.Attachment)
            {
                return ArtifactViewerKinds.Pdf;
            }
            if (mimeType == "text/html" || mimeType == "application/xhtml+xml" ||
                mimeType == "application/json" || mimeType.EndsWith("+json", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("This artifact format has a separate viewer.");
            }
            if (mimeType.StartsWith("text/", StringComparison.Ordinal) ||
                mimeType == "application/xml" || mimeType.EndsWith("+xml", StringComparison.Ordinal) ||
                mimeType == "application/javascript" || mimeType == "application/ecmascript" ||
                mimeType == "application/sql" || mimeType == "application/yaml" ||
                mimeType == "application/x-yaml" || SourceExtensions.Contains(extension))
            {
                return ArtifactViewerKinds.Text;
            }
            throw new InvalidOperationException("Artifact has no admitted text/source viewer representation.");
        }

        internal static string NormalizeMimeType(string value)
        {
            return (value ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
        }

        internal static ChatArtifact ResolveExactArtifact(ChatSession session, string resourceUri)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.Id))
            {
                throw new InvalidOperationException("A persisted chat is required for artifact viewing.");
            }
            var reference = new ResourceRef(resourceUri);
            string artifactId;
            int revision;
            if (!ChatResourceUri.TryParseArtifactRevision(session.Id, reference, out artifactId, out revision))
            {
                throw new InvalidOperationException("An exact artifact revision URI from the active chat is required.");
            }
            var artifact = (session.Artifacts ?? new List<ChatArtifact>()).SingleOrDefault(item =>
                item != null &&
                string.Equals(item.Id, artifactId, StringComparison.OrdinalIgnoreCase) &&
                Math.Max(1, item.Revision) == revision);
            if (artifact == null || !string.Equals(
                ChatResourceUri.CreateArtifactRevisionUri(session, artifact),
                resourceUri,
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Artifact viewer URI is not the canonical exact revision.");
            }
            return artifact;
        }

        private static bool IsImageMimeType(string mimeType)
        {
            return mimeType == "image/jpeg" || mimeType == "image/png" ||
                mimeType == "image/gif" || mimeType == "image/webp";
        }

        internal static bool IsSha256(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Length == 64 &&
                value.All(character => (character >= '0' && character <= '9') ||
                    (character >= 'a' && character <= 'f') ||
                    (character >= 'A' && character <= 'F'));
        }

        private byte[] ReadExactAttachmentBytes(ChatAttachment attachment, long maximumBytes, string kind)
        {
            var bytes = _readAttachmentBytes(attachment);
            if (bytes == null || !attachment.ContentByteLength.HasValue ||
                bytes.LongLength != attachment.ContentByteLength.Value || bytes.LongLength > maximumBytes ||
                !string.Equals(Sha256(bytes), attachment.ContentSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Artifact " + kind + " bytes do not match exact revision evidence.");
            }
            return bytes;
        }

        internal static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                return string.Concat(sha.ComputeHash(bytes).Select(value =>
                    value.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

    }
}
