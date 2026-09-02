using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PDFtoImage;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    internal sealed class ArtifactPdfPageRenderResult
    {
        public byte[] Bytes { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    internal sealed class ArtifactPdfViewerService
    {
        public const int MaximumPages = 10000;
        public const int MaximumPageDimension = 2048;
        public const long MaximumPageImageBytes = 10L * 1024L * 1024L;
        public const int MaximumThumbnailDimension = 320;
        public const long MaximumThumbnailImageBytes = 1L * 1024L * 1024L;

        private readonly ResourceGatewayService _gateway;
        private readonly Func<ChatAttachment, byte[]> _readAttachmentBytes;
        private readonly Func<byte[], int, int, ArtifactPdfPageRenderResult> _renderPage;

        public ArtifactPdfViewerService(
            ResourceGatewayService gateway,
            Func<ChatAttachment, byte[]> readAttachmentBytes)
            : this(gateway, readAttachmentBytes, RenderPage)
        {
        }

        internal ArtifactPdfViewerService(
            ResourceGatewayService gateway,
            Func<ChatAttachment, byte[]> readAttachmentBytes,
            Func<byte[], int, int, ArtifactPdfPageRenderResult> renderPage)
        {
            _gateway = gateway ?? throw new ArgumentNullException("gateway");
            _readAttachmentBytes = readAttachmentBytes;
            _renderPage = renderPage;
        }

        public ArtifactPdfViewerDto ReadInfo(ChatSession session, string resourceUri)
        {
            var artifact = ArtifactViewerService.ResolveExactArtifact(session, resourceUri);
            var descriptor = _gateway.Resolve(session, resourceUri).Resource;
            var attachment = ResolveExactAttachment(session, artifact, descriptor);
            var pageCount = ValidPageCount(attachment);
            var pageTextLengths = ValidTextEvidence(attachment, pageCount);
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

        public ArtifactPdfPageDto ReadPage(ChatSession session, string resourceUri, int pageIndex)
        {
            return ReadRenderedPage(
                session,
                resourceUri,
                pageIndex,
                MaximumPageDimension,
                MaximumPageImageBytes);
        }

        public ArtifactPdfPageDto ReadThumbnail(ChatSession session, string resourceUri, int pageIndex)
        {
            return ReadRenderedPage(
                session,
                resourceUri,
                pageIndex,
                MaximumThumbnailDimension,
                MaximumThumbnailImageBytes);
        }

        private ArtifactPdfPageDto ReadRenderedPage(
            ChatSession session,
            string resourceUri,
            int pageIndex,
            int maximumDimension,
            long maximumImageBytes)
        {
            if (_readAttachmentBytes == null || _renderPage == null)
            {
                throw new InvalidOperationException("Artifact PDF renderer is unavailable.");
            }
            var artifact = ArtifactViewerService.ResolveExactArtifact(session, resourceUri);
            var descriptor = _gateway.Resolve(session, resourceUri).Resource;
            var attachment = ResolveExactAttachment(session, artifact, descriptor);
            var pageCount = ValidPageCount(attachment);
            if (pageIndex < 0 || pageIndex >= pageCount)
            {
                throw new InvalidOperationException("Artifact PDF page is outside the exact document bounds.");
            }
            var pdfBytes = ReadExactAttachmentBytes(attachment);
            ArtifactPdfPageRenderResult rendered;
            try
            {
                rendered = _renderPage(pdfBytes, pageIndex, maximumDimension);
            }
            catch (Exception error) when (IsNativeRendererLoadFailure(error))
            {
                throw new InvalidOperationException(
                    "PDF page rendering is unavailable for the current process architecture.", error);
            }
            if (rendered == null || rendered.Bytes == null || rendered.Bytes.LongLength <= 0 ||
                rendered.Bytes.LongLength > maximumImageBytes ||
                rendered.Width <= 0 || rendered.Width > maximumDimension ||
                rendered.Height <= 0 || rendered.Height > maximumDimension ||
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
                ImageContentSha256 = ArtifactViewerService.Sha256(rendered.Bytes),
                ImageByteLength = rendered.Bytes.LongLength,
                ImageBase64Content = Convert.ToBase64String(rendered.Bytes)
            };
        }

        internal ChatAttachment ValidateTextRead(
            ChatSession session,
            ChatArtifact artifact,
            ResourceDescriptor descriptor)
        {
            var attachment = ResolveExactAttachment(session, artifact, descriptor);
            ValidTextEvidence(attachment, ValidPageCount(attachment));
            return attachment;
        }

        private static ChatAttachment ResolveExactAttachment(
            ChatSession session,
            ChatArtifact artifact,
            ResourceDescriptor descriptor)
        {
            var attachment = ChatArtifactResourceProvider.FindExactAttachment(session, artifact);
            if (attachment == null ||
                !string.Equals(artifact.Kind, ChatArtifactKinds.Attachment, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(attachment.Kind, "pdf", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(ArtifactViewerService.NormalizeMimeType(attachment.ContentType), "application/pdf", StringComparison.Ordinal) ||
                !string.Equals(ArtifactViewerService.NormalizeMimeType(artifact.MimeType), "application/pdf", StringComparison.Ordinal) ||
                !string.Equals(ArtifactViewerService.NormalizeMimeType(descriptor == null ? null : descriptor.MimeType),
                    "application/pdf", StringComparison.Ordinal) ||
                !ArtifactViewerService.IsSha256(attachment.ContentSha256) ||
                !attachment.ContentByteLength.HasValue || attachment.ContentByteLength.Value <= 0 ||
                attachment.ContentByteLength.Value > ArtifactViewerService.MaximumImageBytes)
            {
                throw new InvalidOperationException("Artifact has no admitted exact PDF representation.");
            }
            return attachment;
        }

        private byte[] ReadExactAttachmentBytes(ChatAttachment attachment)
        {
            var bytes = _readAttachmentBytes(attachment);
            if (bytes == null || !attachment.ContentByteLength.HasValue ||
                bytes.LongLength != attachment.ContentByteLength.Value ||
                bytes.LongLength > ArtifactViewerService.MaximumImageBytes ||
                !string.Equals(ArtifactViewerService.Sha256(bytes), attachment.ContentSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Artifact PDF bytes do not match exact revision evidence.");
            }
            return bytes;
        }

        private static int ValidPageCount(ChatAttachment attachment)
        {
            if (attachment.PageCount <= 0 || attachment.PageCount > MaximumPages)
            {
                throw new InvalidOperationException("Artifact PDF page count is outside the viewer bound.");
            }
            return attachment.PageCount;
        }

        private static List<int> ValidTextEvidence(ChatAttachment attachment, int pageCount)
        {
            if (attachment.ExtractedCharCount < 0 || attachment.ExtractedCharCount > 1000000 ||
                !ArtifactViewerService.IsSha256(attachment.ExtractedTextSha256))
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

        private static ArtifactPdfPageRenderResult RenderPage(byte[] pdfBytes, int pageIndex, int maximumDimension)
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
                width = maximumDimension;
                height = Math.Max(1, (int)Math.Round(maximumDimension * size.Height / size.Width));
                options = new RenderOptions(Width: width, Height: null, WithAspectRatio: true);
            }
            else
            {
                height = maximumDimension;
                width = Math.Max(1, (int)Math.Round(maximumDimension * size.Width / size.Height));
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
    }
}
