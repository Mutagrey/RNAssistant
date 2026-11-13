using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using RNAssistant.Application.Contracts;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.WebView
{
    internal sealed partial class LocalBrowserPage
    {
        private sealed class CaptureStream : MemoryStream
        {
            public override void Write(byte[] buffer, int offset, int count)
            { if (Position + count > 4 * 1024 * 1024) throw new IOException("Browser capture exceeds 4 MiB."); base.Write(buffer, offset, count); }
            public override void SetLength(long value)
            { if (value > 4 * 1024 * 1024) throw new IOException("Browser capture exceeds 4 MiB."); base.SetLength(value); }
        }
        private sealed class Snapshot { public SnapshotDocument[] documents { get; set; } }
        private sealed class SnapshotDocument { public SnapshotNodes nodes { get; set; } }
        private sealed class SnapshotNodes { public SparseValues shadowRootType { get; set; } }
        private sealed class SparseValues { public int[] index { get; set; } }
        private async Task RequireInspectableTree()
        {
            var json = await Core.CallDevToolsProtocolMethodAsync("DOMSnapshot.captureSnapshot", "{\"computedStyles\":[]}");
            if (json.Length > 4 * 1024 * 1024) throw new InvalidOperationException("The native DOM snapshot exceeds privacy inspection bounds.");
            var snapshot = Newtonsoft.Json.JsonConvert.DeserializeObject<Snapshot>(json);
            if (snapshot?.documents == null || snapshot.documents.Length != 1 || snapshot.documents[0].nodes?.shadowRootType?.index?.Length > 0)
                throw new InvalidOperationException("The page contains a shadow or embedded tree; media capture is refused.");
        }
        private sealed class PrivacyView { public bool safe { get; set; } public double width { get; set; } public double height { get; set; } public double scale { get; set; } public long epoch { get; set; } }
        internal async Task<BrowserMediaCapture> ReadMediaAsync(CancellationToken token)
        {
            if (!Ready || !Control.Visible) throw new InvalidOperationException("Only the visible native page viewport can be captured.");
            Content.Validate(); token.ThrowIfCancellationRequested();
            var handle = Handle; var viewport = State.ViewportRevision; var control = State.ControlRevision; var zoom = Controller.ZoomFactor;
            await Evaluate<bool>("__rnaActionView.hide();true");
            var privacy = await Evaluate<PrivacyView>("__rnaLocalBrowser.privacy()");
            if (privacy?.safe != true) throw new InvalidOperationException("The page contains private, input or uninspectable regions; media capture is refused.");
            if (privacy.width * privacy.scale > 4096 || privacy.height * privacy.scale > 4096 || privacy.width * privacy.height * privacy.scale * privacy.scale > 4 * 1024 * 1024)
                throw new InvalidOperationException("The visible Browser viewport exceeds capture bounds.");
            await RequireInspectableTree();
            byte[] png;
            using (var stream = new CaptureStream()) { await Core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream); png = stream.ToArray(); }
            token.ThrowIfCancellationRequested();
            await RequireInspectableTree();
            var after = await Evaluate<PrivacyView>("__rnaLocalBrowser.privacy()");
            if (after?.safe != true || !Handle.Matches(handle) || viewport != State.ViewportRevision || control != State.ControlRevision || zoom != Controller.ZoomFactor ||
                after.epoch != privacy.epoch || after.width != privacy.width || after.height != privacy.height || after.scale != privacy.scale || !Control.Visible)
                throw new InvalidOperationException("The exact native viewport changed during capture.");
            if (png.Length > 4 * 1024 * 1024) throw new InvalidOperationException("Browser capture exceeds 4 MiB.");
            int width, height;
            using (var stream = new MemoryStream(png, false)) using (var bitmap = Image.FromStream(stream)) { width = bitmap.Width; height = bitmap.Height; }
            if (width < 1 || height < 1 || width > 4096 || height > 4096 || (long)width * height > 4 * 1024 * 1024 ||
                Math.Abs(width - privacy.width * privacy.scale) > 2 || Math.Abs(height - privacy.height * privacy.scale) > 2)
                throw new InvalidOperationException("Browser pixels do not match the observed viewport.");
            var revision = Guid.NewGuid().ToString("N");
            if (!await Evaluate<bool>("__rnaLocalBrowser.captureMedia(" + Newtonsoft.Json.JsonConvert.SerializeObject(revision) + "," + after.epoch + ")"))
                throw new InvalidOperationException("The viewport changed before media admission.");
            _mediaCapture = new BrowserMediaCapture { CaptureRevision = revision, Page = handle, Png = png, CapturedUtc = DateTime.UtcNow, Title = State.Title,
                SourceAddress = ExternalBrowserResourceProvider.SourceAddress(State.Url), Width = width, Height = height,
                CssWidth = (int)privacy.width, CssHeight = (int)privacy.height,
                ViewportRevision = viewport, ControlRevision = control, DeviceScale = privacy.scale, Zoom = zoom, PrivacyVerified = true };
            return _mediaCapture;
        }
    }
}
