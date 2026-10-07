using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Models;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Runtime
{
    public sealed partial class WorkspaceWebVerifier
    {
        // Foreground development viewer. Cancellation closes this lifetime only;
        // preview never starts a run or writes a verification record.
        public async Task<WorkspacePreviewReport> PreviewAsync(WorkspacePreviewRequest request,
            Action<WorkspacePreviewReport> opened, CancellationToken cancellationToken)
        {
            if (request == null || request.LifetimeSeconds < 1 || request.LifetimeSeconds > 3600 ||
                request.EntryPath != null && request.SnapshotId != null)
                throw new ArgumentException("Preview requires one entry or snapshot and a lifetime from 1 to 3600 seconds.");
            cancellationToken.ThrowIfCancellationRequested();
            var retained = request.SnapshotId == null ? null : _snapshots.ReadSnapshot(_workspace, request.SnapshotId);
            var entry = retained?.EntryPath ?? NormalizeDependency("", request.EntryPath ?? "index.html");
            if (!entry.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && !entry.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Static preview entry must be an HTML file.");
            var errors = new List<string>();
            var snapshot = retained == null ? CaptureSnapshot(entry, errors, cancellationToken) : OpenSnapshot(retained, cancellationToken);
            var report = new WorkspacePreviewReport { EntryPath = entry, Historical = retained != null,
                Headless = request.Headless, Files = snapshot.Keys.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
                SnapshotSha256 = SnapshotHash(snapshot), Diagnostics = BoundedErrors(errors) };
            if (errors.Count != 0) { report.Status = WorkspacePreviewStatus.Failed; return report; }
            retained = retained ?? _snapshots.PublishSnapshot(_workspace, entry,
                snapshot.Values.Select(file => new WebSnapshotFile(file.Path, file.Reference, file.Evidence.Payload)));
            report.SnapshotId = retained.Id;
            var browser = FindBrowserExecutable();
            if (browser == null)
            {
                report.Status = WorkspacePreviewStatus.NotAvailable;
                report.Diagnostics = new[] { "No isolated Chromium executable is available (RNA_BROWSER_EXECUTABLE)." };
                return report;
            }
            report.Browser = Path.GetFileName(browser);
            var didOpen = false;
            try
            {
                using (var server = new SnapshotServer(snapshot, preview: true))
                {
                    await server.StartAsync().ConfigureAwait(false);
                    var page = new Uri(server.BaseUrl, EscapePath(entry));
                    report.Url = page.AbsoluteUri;
                    report.Diagnostics = await RunBrowserAsync(browser, page, server.BaseUrl, null,
                        new List<WebCheckResult>(), cancellationToken,
                        request, () =>
                        {
                            didOpen = true;
                            // A separate projection prevents a retained Opened notice changing later.
                            opened?.Invoke(new WorkspacePreviewReport { Status = WorkspacePreviewStatus.Opened,
                                EntryPath = entry, SnapshotId = report.SnapshotId, SnapshotSha256 = report.SnapshotSha256,
                                Historical = report.Historical, Headless = report.Headless, Files = report.Files,
                                Browser = report.Browser, Url = report.Url, Diagnostics = new string[0] });
                        }).ConfigureAwait(false);
                }
                report.Status = didOpen ? WorkspacePreviewStatus.Closed : WorkspacePreviewStatus.Failed;
                report.CloseReason = didOpen ? (WorkspacePreviewCloseReason?)WorkspacePreviewCloseReason.LifetimeExpired : null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { report.Status = WorkspacePreviewStatus.Failed; report.Diagnostics = new[] { "Preview startup timed out." }; }
            catch (Exception ex) when (ex is IOException || ex is HttpListenerException ||
                ex is WebSocketException || ex is HttpRequestException || ex is InvalidOperationException)
            { report.Status = WorkspacePreviewStatus.Failed; report.Diagnostics = BoundedErrors(new[] { "Preview interrupted: " + ex.Message }); }
            return report;
        }
    }
}
