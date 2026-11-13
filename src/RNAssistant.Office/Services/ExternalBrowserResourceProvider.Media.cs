using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Office.Services
{
    internal sealed partial class ExternalBrowserResourceProvider
    {
        private ResourceReadSelection CaptureMedia(ChatSession session, ResourceReadRequest request,
            IBrowserReadSource source, BrowserPageHandle target, CancellationToken token)
        {
            ResourceReadCursor.RejectCursor(request);
            var native = source as IBrowserMediaSource;
            if (native == null || _authority?.Payloads == null)
                throw Error("This page has no qualified native screenshot provider.", "browser_media_unavailable");
            CheckDocument(session);
            var capture = native.ReadMediaAsync(target, token).GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested();
            if (capture == null || !target.Matches(capture.Page) || source != _source() ||
                !source.TargetsFor(session.Id).Any(target.Matches) || !native.IsCaptureCurrent(capture))
                throw Error("The page or viewport changed during capture.", "browser_document_stale");
            CheckDocument(session);
            var png = capture.Png;
            if (!capture.PrivacyVerified || capture.Title == null || capture.Title.Length > MaximumName ||
                capture.CapturedUtc.Kind != DateTimeKind.Utc || capture.CapturedUtc == default(DateTime) ||
                capture.SourceAddress != SourceAddress(capture.SourceAddress) ||
                string.IsNullOrEmpty(capture.CaptureRevision) || capture.CaptureRevision.Length > 64 ||
                capture.CssWidth < 1 || capture.CssHeight < 1 || capture.CssWidth > 4096 || capture.CssHeight > 4096 ||
                capture.ViewportRevision < 1 || capture.ControlRevision < 1 ||
                !(capture.DeviceScale > 0 && capture.DeviceScale <= 8) || !(capture.Zoom > 0 && capture.Zoom <= 8) ||
                png == null || png.Length < 33 || png.Length > 4 * 1024 * 1024 ||
                !png.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                !png.Skip(12).Take(4).SequenceEqual(new byte[] { 73, 72, 68, 82 }) ||
                capture.Width < 1 || capture.Height < 1 || capture.Width > 4096 || capture.Height > 4096 ||
                (long)capture.Width * capture.Height > 4 * 1024 * 1024 ||
                PngSize(png, 16) != capture.Width || PngSize(png, 20) != capture.Height)
                throw Error("Native screenshot bounds or privacy could not be verified.", "browser_capture_invalid");

            var media = PayloadRef.FromBlob(_authority.Payloads.StoreBytes(png, "image/png"));
            var exact = new ResourceRef(ResourceUri.Create("browser", session.Id, "observations",
                capture.CapturedUtc.Ticks.ToString("D19", CultureInfo.InvariantCulture), Guid.NewGuid().ToString("N")), media.Sha256);
            var scope = _authority.Scope(session, false);
            var text = "Untrusted captured page viewport. Historical pixels do not authorize current input.\nSource: " +
                capture.SourceAddress + "\nSize: " + capture.Width + " × " + capture.Height + " pixels; CSS viewport: " + capture.CssWidth + " × " + capture.CssHeight + ".";
            var dependency = new ResourceDependency(new ResourceRef(ResourceUri.Create("browser", target.ChatId, target.SessionId), capture.CaptureRevision),
                "media", ResourceCoverage.Whole(), "immutable-snapshot");
            var body = PayloadRef.FromBlob(_authority.Payloads.StoreText(text, "text/plain; charset=utf-8"));
            _authority.Revisions.RegisterRevision(scope, new ResourceRevisionMetadata(exact, media.Sha256, body,
                dependencies: new[] { dependency }, createdUtc: capture.CapturedUtc));
            _authority.Revisions.RegisterView(scope, new ResourceRevisionView(exact, "media", media.Sha256, media, ResourceCoverage.Whole()));
            var metadata = PayloadRef.FromBlob(_authority.Payloads.StoreText(JsonConvert.SerializeObject(new {
                Title = capture.Title, Url = capture.SourceAddress, capture.Page, capture.ViewportRevision,
                capture.ControlRevision, capture.Width, capture.Height, capture.CssWidth, capture.CssHeight, capture.DeviceScale, capture.Zoom
            }), "application/json"));
            _authority.Revisions.RegisterView(scope, new ResourceRevisionView(exact, "browser-source", metadata.Sha256,
                metadata, ResourceCoverage.Whole()));
            var descriptor = ObservationDescriptor(exact, capture.CapturedUtc, capture.Title, capture.SourceAddress);
            descriptor.Kind = Kind;
            descriptor.Representations.Add("media"); descriptor.ContentSha256 = media.Sha256;
            descriptor.Payload = body;
            descriptor.Dependencies.Add(dependency);
            return MediaSelection(descriptor, media, "Viewport " + capture.CssWidth + " × " + capture.CssHeight + " CSS pixels (image " + capture.Width + " × " + capture.Height +
                "). Prefer semantic name-based actions; point input uses CSS coordinates from this fresh image only.");
        }

        private ResourceReadSelection ReadRetainedMedia(ChatSession session, ResourceReadRequest request)
        {
            ResourceReadCursor.RejectCursor(request);
            var descriptor = DescribeObservation(session, request.Reference.Uri);
            descriptor.Dependencies.RemoveAll(item => item.Kind == "immutable-snapshot");
            if (request.Reference.IsExact && request.Reference.Revision != descriptor.Reference.Revision)
                throw Error("The exact media observation is unavailable.", "RESOURCE_SNAPSHOT_UNAVAILABLE");
            var payload = _authority.Revisions.GetView(_authority.Scope(session, false), descriptor.Reference, "media")?.Payload;
            if (payload == null) throw Error("This observation has no captured pixels.", "RESOURCE_VIEW_UNSUPPORTED");
            return MediaSelection(descriptor, payload);
        }

        private static ResourceReadSelection MediaSelection(ResourceDescriptor descriptor, PayloadRef media, string caption = null)
        {
            return new ResourceReadSelection { Result = new ResourceReadResult { Resource = descriptor,
                Representation = "media", MediaDescription = caption, Payload = media, ContentSha256 = media.Sha256, Complete = true, SourceComplete = false, Coverage = ResourceCoverage.Whole(),
                HydratedForNextModelStep = true, RawContentIncluded = false },
                ModelAttachments = new[] { new ChatAttachment { Kind = "image", ContentType = "image/png",
                    FileName = "browser-viewport.png", ContentSha256 = media.Sha256, ContentByteLength = media.ByteLength,
                    Size = media.ByteLength } }, ResourceRefs = new[] { descriptor.Reference.Copy() } };
        }

        private static long PngSize(byte[] png, int offset)
        { return ((long)png[offset] << 24) | ((long)png[offset + 1] << 16) | ((long)png[offset + 2] << 8) | png[offset + 3]; }
    }
}
