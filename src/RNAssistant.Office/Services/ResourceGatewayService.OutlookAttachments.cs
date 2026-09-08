using System;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Services
{
    internal sealed partial class ResourceGatewayService
    {
        private ResourceReadSelection ReadOutlookAttachment(ChatSession session, ResourceReadRequest request, LiveDocumentResourceProvider provider)
        {
            if (_authority?.Payloads == null)
                throw AttachmentViewError("Canonical attachment storage is unavailable.");
            var view = (request.Representation ?? string.Empty).Trim().ToLowerInvariant();
            var automatic = view == "auto" || view.Length == 0;
            if (automatic) view = "text";
            if (view != "metadata" && view != "text" && view != "media")
                throw AttachmentViewError("Attachment views: metadata, text, media.");
            if (request.RowOffset != 0 || request.Fields?.Count > 0 || !string.IsNullOrEmpty(request.ViewPath))
                throw AttachmentViewError("This attachment view does not accept structural selectors.");
            if (view != "text") ResourceReadCursor.RejectCursor(request);
            if (view == "metadata" && !request.Reference.IsExact)
                return WithProvider(provider, session, () => provider.Read(session, new ResourceReadRequest {
                    Reference = request.Reference, Representation = "metadata" }));
            var scope = _authority.Scope(session, true);
            var store = (IResourceRevisionStore)_authority.Store;
            var exact = request.Reference;
            if (!string.IsNullOrEmpty(request.Cursor) && !exact.IsExact)
                throw AttachmentViewError("Attachment continuation requires its exact captured revision.");
            OutlookAttachmentSource source;
            if (exact.IsExact)
            {
                _authority.RequirePublished(_authority.CaptureMany(new[] { scope }).Get(scope), exact, session);
                var retained = store.GetView(scope, exact, LiveDocumentResourceProvider.OutlookAttachmentSourceView);
                if (retained?.Payload == null) throw AttachmentViewError("The exact attachment source is unavailable; no live fallback is permitted.");
                source = JsonConvert.DeserializeObject<OutlookAttachmentSource>(ResourceSnapshotReadService.ReadPayload(_authority.Payloads, retained.Payload));
                if (source?.Original == null || !retained.Parts.Any(part => part.Sha256 == source.Original.Sha256 && part.ByteLength == source.Original.ByteLength) ||
                    source.Text != null && !retained.Parts.Any(part => part.Sha256 == source.Text.Sha256 && part.ByteLength == source.Text.ByteLength))
                    throw AttachmentViewError("The retained attachment has no matching source parts.");
            }
            else
            {
                var captured = WithProvider(provider, session, () => _authority.PublishRead(session,
                    provider.Read(session, new ResourceReadRequest { Reference = request.Reference,
                        Representation = LiveDocumentResourceProvider.OutlookAttachmentSourceView }), null, true));
                exact = captured.Result.Resource.Reference;
                source = JsonConvert.DeserializeObject<OutlookAttachmentSource>(captured.Result.Text);
            }
            if (source?.Original == null || source.Original.ByteLength < 1 || source.Original.ByteLength > AttachmentStore.MaxFileBytes)
                throw AttachmentViewError("The exact attachment source is invalid.");
            var descriptor = new ResourceDescriptor { Reference = exact.Copy(), Provider = "document", Kind = LiveDocumentResourceProvider.OutlookAttachmentKind,
                Title = source.Title, MimeType = source.MimeType, ContentSha256 = source.Original.Sha256, ByteLength = source.Original.ByteLength,
                Mutable = true, Tracking = "externally-observed" };
            descriptor.Representations.Add("metadata");
            if (source.Text != null) descriptor.Representations.Add("text");
            if (source.Kind == "image" || source.Kind == "pdf") descriptor.Representations.Add("media");
            if (automatic && (source.Kind == "image" || source.Kind == "pdf" && source.Warning != null)) view = "media";
            descriptor.Metadata["fileName"] = source.FileName;
            if (!string.IsNullOrEmpty(source.Warning)) descriptor.Metadata["warning"] = source.Warning;
            if (source.Kind == "pdf") descriptor.Metadata["pageCount"] = source.PageCount.ToString(CultureInfo.InvariantCulture);
            if (view == "metadata") return new ResourceReadSelection { Result = new ResourceReadResult {
                Resource = descriptor, Representation = view, Complete = true }, ResourceRefs = new[] { exact } };
            if (view == "text")
            {
                if (source.Text == null) throw AttachmentViewError("This image has no extracted text; read the media view with a vision model.");
                if (store.GetView(scope, exact, view) == null)
                    store.RegisterView(scope, new ResourceRevisionView(exact, view, source.Text.Sha256, source.Text, ResourceCoverage.Whole()));
                var text = new ResourceSnapshotReadService(_authority, _authority.Payloads).Read(session, scope, descriptor,
                    new ResourceReadRequest { Reference = exact, Representation = view, Cursor = request.Cursor, MaxChars = request.MaxChars });
                text.Result.RawContentIncluded = true;
                return text;
            }
            if (source.Kind != "image" && source.Kind != "pdf")
                throw AttachmentViewError("This attachment has no visual media representation.");
            try
            {
                if (_authority.Payloads.ReadPrefix(source.Original.ToBlobReference(), 1) == null)
                    throw AttachmentViewError("The exact attachment bytes are missing or corrupt.");
            }
            catch (Exception error) when (error is System.IO.IOException || error is UnauthorizedAccessException || error is System.Security.Cryptography.CryptographicException)
            { throw AttachmentViewError("The exact attachment bytes are missing or corrupt."); }
            var attachment = new ChatAttachment { Id = "outlook_" + source.Original.Sha256,
                FileName = source.FileName, Kind = source.Kind, ContentType = source.MimeType,
                ContentSha256 = source.Original.Sha256, ContentByteLength = source.Original.ByteLength, Size = source.Original.ByteLength,
                ExtractedTextSha256 = source.Text?.Sha256, ExtractedTextByteLength = source.Text?.ByteLength,
                ExtractionWarning = source.Warning, PageCount = source.PageCount,
                PageTextLengths = source.PageTextLengths, Status = "ready" };
            return new ResourceReadSelection { Result = new ResourceReadResult { Resource = descriptor, Representation = "media",
                ContentSha256 = source.Original.Sha256, Complete = true, HydratedForNextModelStep = true,
                Coverage = ResourceCoverage.Whole(), AuthorityGeneration = _authority.CaptureMany(new[] { scope }).Get(scope).Generation },
                ResourceRefs = new[] { exact.Copy() }, ModelAttachments = new[] { attachment } };
        }

        private static ResourceRequestException AttachmentViewError(string message)
        { return new ResourceRequestException(message, "RESOURCE_VIEW_UNAVAILABLE", false); }
    }
}
