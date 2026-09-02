using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using PDFtoImage;
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

    internal sealed class ArtifactPdfPageRenderResult
    {
        public byte[] Bytes { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    internal sealed class ArtifactViewerService
    {
        public const int PageCharacters = 32000;
        public const int MaximumDocumentCharacters = 512000;
        public const long MaximumImageBytes = 20L * 1024L * 1024L;
        public const int MaximumPdfPages = 10000;
        public const int MaximumPdfPageDimension = 2048;
        public const long MaximumPdfPageImageBytes = 10L * 1024L * 1024L;

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
        private readonly Func<byte[], int, ArtifactPdfPageRenderResult> _renderPdfPage;

        public ArtifactViewerService(ResourceGatewayService gateway)
            : this(gateway, null)
        {
        }

        public ArtifactViewerService(
            ResourceGatewayService gateway,
            Func<ChatAttachment, byte[]> readAttachmentBytes)
            : this(gateway, readAttachmentBytes, RenderPdfPage)
        {
        }

        internal ArtifactViewerService(
            ResourceGatewayService gateway,
            Func<ChatAttachment, byte[]> readAttachmentBytes,
            Func<byte[], int, ArtifactPdfPageRenderResult> renderPdfPage)
        {
            _gateway = gateway ?? throw new ArgumentNullException("gateway");
            _readAttachmentBytes = readAttachmentBytes;
            _renderPdfPage = renderPdfPage;
        }

        public ArtifactPdfViewerDto ReadPdfInfo(ChatSession session, string resourceUri)
        {
            var artifact = ResolveExactArtifact(session, resourceUri);
            var descriptor = _gateway.Resolve(session, resourceUri).Resource;
            var attachment = ResolveExactPdfAttachment(session, artifact, descriptor);
            var pageCount = ValidPdfPageCount(attachment);
            var pageTextLengths = ValidPdfTextEvidence(attachment, pageCount);
            var textTruncated = attachment.TextTruncated || pageTextLengths.Count < pageCount;
            var warning = attachment.ExtractionWarning;
            if (string.IsNullOrWhiteSpace(warning) &&
                (pageTextLengths.Count == 0 || pageTextLengths.All(value => value < 20)))
            {
                warning = "PDF contains little or no extractable text; page images are shown for scanned content.";
            }
            return new ArtifactPdfViewerDto
            {
                ResourceUri = resourceUri,
                ViewerKind = ArtifactViewerKinds.Pdf,
                Title = descriptor.Title ?? artifact.Title ?? attachment.FileName ?? "PDF",
                MimeType = "application/pdf",
                ContentSha256 = attachment.ContentSha256,
                ByteLength = attachment.ContentByteLength.Value,
                PageCount = pageCount,
                PageTextLengths = pageTextLengths,
                ExtractedTextSha256 = attachment.ExtractedTextSha256,
                ExtractedCharacters = attachment.ExtractedCharCount,
                TextTruncated = textTruncated,
                ExtractionWarning = warning
            };
        }

        public ArtifactPdfPageDto ReadPdfPage(ChatSession session, string resourceUri, int pageIndex)
        {
            if (_readAttachmentBytes == null || _renderPdfPage == null)
            {
                throw new InvalidOperationException("Artifact PDF renderer is unavailable.");
            }
            var artifact = ResolveExactArtifact(session, resourceUri);
            var descriptor = _gateway.Resolve(session, resourceUri).Resource;
            var attachment = ResolveExactPdfAttachment(session, artifact, descriptor);
            var pageCount = ValidPdfPageCount(attachment);
            if (pageIndex < 0 || pageIndex >= pageCount)
            {
                throw new InvalidOperationException("Artifact PDF page is outside the exact document bounds.");
            }
            var pdfBytes = ReadExactAttachmentBytes(attachment, MaximumImageBytes, "PDF");
            ArtifactPdfPageRenderResult rendered;
            try
            {
                rendered = _renderPdfPage(pdfBytes, pageIndex);
            }
            catch (Exception error) when (IsNativeRendererLoadFailure(error))
            {
                throw new InvalidOperationException(
                    "PDF page rendering is unavailable for the current process architecture.", error);
            }
            if (rendered == null || rendered.Bytes == null || rendered.Bytes.LongLength <= 0 ||
                rendered.Bytes.LongLength > MaximumPdfPageImageBytes ||
                rendered.Width <= 0 || rendered.Width > MaximumPdfPageDimension ||
                rendered.Height <= 0 || rendered.Height > MaximumPdfPageDimension ||
                !IsJpeg(rendered.Bytes))
            {
                throw new InvalidOperationException("Artifact PDF renderer returned an invalid bounded page image.");
            }
            return new ArtifactPdfPageDto
            {
                ResourceUri = resourceUri,
                ViewerKind = ArtifactViewerKinds.Pdf,
                ContentSha256 = attachment.ContentSha256,
                PageIndex = pageIndex,
                PageCount = pageCount,
                Width = rendered.Width,
                Height = rendered.Height,
                ImageMimeType = "image/jpeg",
                ImageContentSha256 = Sha256(rendered.Bytes),
                ImageByteLength = rendered.Bytes.LongLength,
                ImageBase64Content = Convert.ToBase64String(rendered.Bytes)
            };
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
                exactPdfAttachment = ResolveExactPdfAttachment(session, artifact, descriptor);
                var pdfPageCount = ValidPdfPageCount(exactPdfAttachment);
                ValidPdfTextEvidence(exactPdfAttachment, pdfPageCount);
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

        private static string NormalizeMimeType(string value)
        {
            return (value ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
        }

        private static ChatArtifact ResolveExactArtifact(ChatSession session, string resourceUri)
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

        private static bool IsSha256(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Length == 64 &&
                value.All(character => (character >= '0' && character <= '9') ||
                    (character >= 'a' && character <= 'f') ||
                    (character >= 'A' && character <= 'F'));
        }

        private static ChatAttachment ResolveExactPdfAttachment(
            ChatSession session,
            ChatArtifact artifact,
            ResourceDescriptor descriptor)
        {
            var attachment = ChatArtifactResourceProvider.FindExactAttachment(session, artifact);
            if (attachment == null ||
                !string.Equals(artifact.Kind, ChatArtifactKinds.Attachment, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(attachment.Kind, "pdf", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(NormalizeMimeType(attachment.ContentType), "application/pdf", StringComparison.Ordinal) ||
                !string.Equals(NormalizeMimeType(artifact.MimeType), "application/pdf", StringComparison.Ordinal) ||
                !string.Equals(NormalizeMimeType(descriptor == null ? null : descriptor.MimeType),
                    "application/pdf", StringComparison.Ordinal) ||
                !IsSha256(attachment.ContentSha256) ||
                !attachment.ContentByteLength.HasValue || attachment.ContentByteLength.Value <= 0 ||
                attachment.ContentByteLength.Value > MaximumImageBytes)
            {
                throw new InvalidOperationException("Artifact has no admitted exact PDF representation.");
            }
            return attachment;
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

        private static int ValidPdfPageCount(ChatAttachment attachment)
        {
            if (attachment.PageCount <= 0 || attachment.PageCount > MaximumPdfPages)
            {
                throw new InvalidOperationException("Artifact PDF page count is outside the viewer bound.");
            }
            return attachment.PageCount;
        }

        private static List<int> ValidPdfTextEvidence(ChatAttachment attachment, int pageCount)
        {
            if (attachment.ExtractedCharCount < 0 || attachment.ExtractedCharCount > 1000000 ||
                !IsSha256(attachment.ExtractedTextSha256))
            {
                throw new InvalidOperationException("Artifact PDF extracted-text evidence is inconsistent.");
            }
            var pageTextLengths = (attachment.PageTextLengths ?? new List<int>()).ToList();
            if (pageTextLengths.Count > pageCount || pageTextLengths.Any(value => value < 0))
            {
                throw new InvalidOperationException("Artifact PDF page-text evidence is inconsistent.");
            }
            return pageTextLengths;
        }

        private static ArtifactPdfPageRenderResult RenderPdfPage(byte[] pdfBytes, int pageIndex)
        {
            var size = Conversion.GetPageSize(pdfBytes, new Index(pageIndex));
            if (size.Width <= 0 || size.Height <= 0 ||
                float.IsNaN(size.Width) || float.IsInfinity(size.Width) ||
                float.IsNaN(size.Height) || float.IsInfinity(size.Height))
            {
                throw new InvalidOperationException("PDF page has invalid dimensions.");
            }
            int width;
            int height;
            RenderOptions options;
            if (size.Width >= size.Height)
            {
                width = MaximumPdfPageDimension;
                height = Math.Max(1, (int)Math.Round(MaximumPdfPageDimension * size.Height / size.Width));
                options = new RenderOptions(Width: width, Height: null, WithAspectRatio: true);
            }
            else
            {
                height = MaximumPdfPageDimension;
                width = Math.Max(1, (int)Math.Round(MaximumPdfPageDimension * size.Width / size.Height));
                options = new RenderOptions(Width: null, Height: height, WithAspectRatio: true);
            }
            using (var output = new MemoryStream())
            {
                Conversion.SaveJpeg(output, pdfBytes, new Index(pageIndex), null, options);
                return new ArtifactPdfPageRenderResult
                {
                    Bytes = output.ToArray(),
                    Width = width,
                    Height = height
                };
            }
        }

        private static bool IsNativeRendererLoadFailure(Exception error)
        {
            for (var current = error; current != null; current = current.InnerException)
            {
                if (current is DllNotFoundException || current is BadImageFormatException ||
                    current is EntryPointNotFoundException)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsJpeg(byte[] bytes)
        {
            return bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xd8 &&
                bytes[bytes.Length - 2] == 0xff && bytes[bytes.Length - 1] == 0xd9;
        }

        private static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                return string.Concat(sha.ComputeHash(bytes).Select(value =>
                    value.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

    }
}
