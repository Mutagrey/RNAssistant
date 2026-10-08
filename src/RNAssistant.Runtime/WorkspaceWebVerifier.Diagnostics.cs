using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Runtime
{
    public sealed partial class WorkspaceWebVerifier
    {
        private sealed class BrowserDiagnostics
        {
            private static readonly JsonSerializerSettings TraceJson = new JsonSerializerSettings
                { StringEscapeHandling = StringEscapeHandling.EscapeNonAscii };
            private readonly string _verificationId;
            private readonly Stopwatch _elapsed = new Stopwatch();
            private readonly List<WebBrowserTraceEntry> _entries = new List<WebBrowserTraceEntry>();
            private RetainedWebSnapshot _snapshot;
            private int _bytes, _dropped;
            public byte[] Screenshot { get; set; }
            public string ScreenshotError { get; set; } = "Screenshot was not captured.";

            public BrowserDiagnostics(string verificationId) { _verificationId = verificationId; }
            public void Bind(RetainedWebSnapshot snapshot) { _snapshot = snapshot; }
            public void Start() { _elapsed.Start(); Add(WebBrowserTraceEventKind.Lifecycle, "browser-start", null); }

            public void Add(WebBrowserTraceEventKind kind, string name, string detail)
            {
                if (!_elapsed.IsRunning) return;
                var entry = new WebBrowserTraceEntry { Milliseconds = _elapsed.ElapsedMilliseconds,
                    Kind = kind, Name = Bound(name, 128), Detail = Bound(detail, 700) };
                var length = Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(entry, TraceJson)) + 1;
                if (_entries.Count >= WorkspaceWebSnapshotStore.MaximumTraceEntries ||
                    _bytes + length > WorkspaceWebSnapshotStore.MaximumTraceBytes - 4096)
                { if (_dropped < int.MaxValue) _dropped++; return; }
                _entries.Add(entry); _bytes += length;
            }

            public IReadOnlyList<WebDiagnosticCapture> Finish(WebVerificationState outcome)
            {
                Add(WebBrowserTraceEventKind.Lifecycle, "verification-ended", outcome.ToString());
                var trace = _snapshot == null || !_elapsed.IsRunning ? null : StrictUtf8.GetBytes(JsonConvert.SerializeObject(
                    new WebBrowserTrace { VerificationId = _verificationId, SnapshotId = _snapshot.Id,
                        SnapshotSha256 = _snapshot.SnapshotSha256, Outcome = outcome,
                        DurationMilliseconds = _elapsed.ElapsedMilliseconds, Truncated = _dropped > 0,
                        DroppedEntries = _dropped, Entries = _entries.ToArray() }, TraceJson));
                _elapsed.Stop();
                return new[] {
                    new WebDiagnosticCapture(WebDiagnosticKind.Screenshot, Screenshot,
                        Screenshot == null ? ScreenshotError : null),
                    new WebDiagnosticCapture(WebDiagnosticKind.Trace, trace,
                        trace == null ? "Browser did not start; no observation trace was recorded." : null)
                };
            }

            private static string Bound(string value, int count)
            {
                if (value == null || value.Length <= count) return value;
                if (char.IsHighSurrogate(value[count - 1])) count--;
                return value.Substring(0, count);
            }
        }

        private static async Task CaptureScreenshotAsync(DevToolsClient client, BrowserDiagnostics diagnostics, CancellationToken token)
        {
            if (diagnostics == null) return;
            try
            {
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(3));
                    var response = await client.CommandAsync("Page.captureScreenshot", new JObject {
                        ["format"] = "png", ["fromSurface"] = true, ["captureBeyondViewport"] = false }, deadline.Token).ConfigureAwait(false);
                    var encoded = (string)response["result"]?["data"];
                    if (string.IsNullOrEmpty(encoded) || encoded.Length > 4 * ((WorkspaceWebSnapshotStore.MaximumScreenshotBytes + 2) / 3))
                        throw new IOException("Screenshot exceeds the 512 KiB bound or is empty.");
                    var bytes = Convert.FromBase64String(encoded);
                    if (!WorkspaceWebSnapshotStore.IsBoundedScreenshot(bytes))
                        throw new IOException("Screenshot exceeds the PNG byte or dimension bounds.");
                    diagnostics.Screenshot = bytes;
                    diagnostics.ScreenshotError = null;
                    diagnostics.Add(WebBrowserTraceEventKind.Diagnostic, "screenshot", "viewport PNG captured");
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { diagnostics.ScreenshotError = "Screenshot capture timed out."; }
            catch (Exception ex) when (ex is IOException || ex is WebSocketException || ex is FormatException ||
                ex is JsonException || ex is InvalidOperationException)
            { diagnostics.ScreenshotError = "Screenshot capture failed or exceeded its bound."; }
            if (diagnostics.Screenshot == null)
                diagnostics.Add(WebBrowserTraceEventKind.Diagnostic, "screenshot-unavailable", diagnostics.ScreenshotError);
        }
    }
}
