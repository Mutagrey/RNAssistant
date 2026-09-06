using System;
using System.Globalization;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Services
{
    internal sealed partial class ResourceGatewayService
    {
        internal static bool IsBinaryView(string view)
        { return view == ResourceRepresentations.Raw || view == ResourceRepresentations.Image || view == ResourceRepresentations.Thumbnail ||
            view == ResourceRepresentations.RenderPage || view == ResourceRepresentations.PageThumbnail; }

        private ResourceReadSelection ReadBinaryView(ChatSession session, ResourceReadRequest request)
        {
            if (_mediaViews == null || _authority?.Payloads == null)
                throw new ResourceRequestException("The binary view provider is unavailable.", "RESOURCE_VIEW_UNAVAILABLE", false);
            ResourceReadCursor.RejectCursor(request);
            if (request.RowOffset != 0 || request.Fields != null && request.Fields.Count != 0)
                throw new ResourceRequestException("Binary views do not support row offsets or field selectors.", "RESOURCE_VIEW_INVALID", false);
            var artifact = ArtifactViewerService.ResolveExactArtifact(session, request.Reference.Uri);
            var exact = ChatResourceUri.CreateArtifactRevision(session, artifact);
            if (request.Reference.IsExact && exact.Revision != request.Reference.Revision)
                throw new ResourceRequestException("The binary view revision is unavailable.", "RESOURCE_SNAPSHOT_UNAVAILABLE", false);
            var page = 0;
            var raw = request.Representation == ResourceRepresentations.Raw;
            var paged = request.Representation == ResourceRepresentations.RenderPage || request.Representation == ResourceRepresentations.PageThumbnail;
            if (paged && (!int.TryParse(request.ViewPath, NumberStyles.None, CultureInfo.InvariantCulture, out page) || page < 0) ||
                !paged && !string.IsNullOrEmpty(request.ViewPath))
                throw new ResourceRequestException("An exact zero-based page selector is required for page views only.", "RESOURCE_VIEW_INVALID", false);
            var descriptor = Resolve(session, exact.Uri).Resource;
            var capability = descriptor.ViewCapabilities.SingleOrDefault(item => item.View == request.Representation);
            if (capability?.MaxPayloadBytes == null)
                throw new ResourceRequestException("The resource does not offer this binary view.", "RESOURCE_VIEW_UNAVAILABLE", false);
            descriptor.Metadata["sourceContentSha256"] = descriptor.ContentSha256;
            var view = "binary:" + request.Representation + (paged ? ":" + page.ToString(CultureInfo.InvariantCulture) : string.Empty);
            var scope = _authority.Scope(session, false);
            var revisions = (IResourceRevisionStore)_authority.Store;
            var retained = revisions.GetView(scope, exact, view);
            ResourceBinaryView binary;
            if (retained?.Payload != null)
            {
                // View metadata is itself a bounded CAS record; image bytes remain a retained part.
                if (retained.Payload.ByteLength > 4096)
                    throw new ResourceRequestException("The exact binary metadata is unavailable.", "RESOURCE_SNAPSHOT_UNAVAILABLE", false);
                binary = Newtonsoft.Json.JsonConvert.DeserializeObject<ResourceBinaryView>(
                    _authority.Payloads.ReadText(retained.Payload.ToBlobReference()));
            }
            else
            {
                byte[] bytes;
                string mimeType;
                binary = new ResourceBinaryView();
                if (raw)
                {
                    bytes = _mediaViews.ReadRawSource(session, exact.Uri);
                    mimeType = "application/octet-stream";
                }
                else if (paged)
                {
                    var image = request.Representation == ResourceRepresentations.PageThumbnail
                        ? _mediaViews.ReadPdfThumbnail(session, exact.Uri, page) : _mediaViews.ReadPdfPage(session, exact.Uri, page);
                    bytes = image.Bytes; mimeType = image.ImageMimeType;
                    binary.Width = image.Width; binary.Height = image.Height;
                    binary.PageIndex = image.PageIndex; binary.PageCount = image.PageCount;
                }
                else if (request.Representation == ResourceRepresentations.Thumbnail)
                {
                    var image = _mediaViews.ReadImageThumbnail(session, exact.Uri);
                    bytes = image.Bytes; mimeType = image.ImageMimeType;
                    binary.Width = image.Width; binary.Height = image.Height;
                }
                else
                {
                    var image = _mediaViews.ReadImage(session, exact.Uri);
                    bytes = image.Bytes; mimeType = image.MimeType;
                }
                binary.Payload = PayloadRef.FromBlob(_authority.Payloads.StoreBytes(bytes, mimeType));
                var metadata = PayloadRef.FromBlob(_authority.Payloads.StoreText(
                    Newtonsoft.Json.JsonConvert.SerializeObject(binary), "application/json"));
                if (revisions.GetRevision(scope, exact) == null)
                    revisions.RegisterRevision(scope, new ResourceRevisionMetadata(exact, descriptor.ContentSha256));
                revisions.RegisterView(scope, new ResourceRevisionView(exact, view, binary.Payload.Sha256,
                    metadata, ResourceCoverage.Whole(), new[] { binary.Payload }));
            }
            var expectedMime = raw ? "application/octet-stream" : request.Representation == ResourceRepresentations.Image
                ? ArtifactViewerService.NormalizeMimeType(descriptor.MimeType) : "image/jpeg";
            if (binary?.Payload == null || binary.Payload.ByteLength < 0 || !raw && binary.Payload.ByteLength == 0 ||
                binary.Payload.ByteLength > capability.MaxPayloadBytes.Value || binary.Payload.ContentType != expectedMime ||
                raw && (binary.Payload.ByteLength != descriptor.ByteLength ||
                    !string.Equals(binary.Payload.Sha256, descriptor.ContentSha256, StringComparison.OrdinalIgnoreCase)))
                throw new ResourceRequestException("The exact binary payload is unavailable.", "RESOURCE_SNAPSHOT_UNAVAILABLE", false);
            var coverage = paged ? new ResourceCoverage(ResourceCoverageKinds.PageRange, start: page, end: page + 1) : ResourceCoverage.Whole();
            var result = new ResourceReadSelection { Result = new ResourceReadResult {
                Resource = descriptor, Representation = request.Representation, Binary = binary,
                ContentSha256 = binary.Payload.Sha256, Payload = binary.Payload, Coverage = coverage, Complete = true
            }, ResourceRefs = new[] { exact.Copy() } };
            // Publication sees only durable bytes and metadata, not a renderer's provisional result.
            return _authority.PublishRead(session, result, request, false);
        }
    }
}
