using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void TrajectoryExportUsesExactDownload()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Word", "trajectory-download", "Trace.docx", "Export");
                store.AppendTrace(session, SessionEventTypes.LlmRequest, new { Text = "CAPTURED" }, "payload", "text/plain", "run-export", "turn-export", "step-export");
                var head = store.ReadCompleteEvents(session.Host, session.DocumentKey, session.Id).Last().Sequence;
                var blobsBefore = Directory.GetFiles(paths.ChatBlobDirectory, "*.blob", SearchOption.AllDirectories).Length;
                var exporter = new TrajectoryExportService(paths, null, new EventStreamTrajectoryQuery());
                var captures = 0;
                using (var data = new ResourceDataPlaneService(new ResourceGatewayService()))
                {
                    var download = new TrajectoryExportDownloadService(exporter, data).Open(session, () => {
                        captures++; return store.ReadCompleteEvents(session.Host, session.DocumentKey, session.Id);
                    }, new TrajectoryExportRequest(), CancellationToken.None);
                    var wire = JsonConvert.SerializeObject(download);
                    AssertTrue(!wire.Contains("base64") && !wire.Contains("CAPTURED"), "bridge metadata has no ZIP or event body");
                    store.AppendTrace(session, SessionEventTypes.LlmRequest, new { Text = "LATER" }, null, null, "run-later", "turn-later", "step-later");
                    AssertEqual("RESOURCE_ACCESS_DENIED", RuntimeThrows<ResourceRequestException>(() => data.Close("foreign", TrajectoryExportDownloadService.Owner,
                        download.Data.LeaseId)).ErrorCode, "another chat cannot revoke the download");
                    var router = new ResourceDataRouter(data);
                    using (var bytes = new MemoryStream())
                    {
                        for (var offset = 0; offset < download.ByteLength;)
                        {
                            var count = Math.Min(127, (int)download.ByteLength - offset);
                            var response = router.Handle("GET", download.Data.Url + "?offset=" + offset + "&count=" + count, CancellationToken.None);
                            AssertEqual(200, response.StatusCode, "bounded binary GET uses the same router");
                            AssertEqual("application/zip", response.ContentType, "ZIP MIME remains explicit");
                            using (response.Body) { AssertEqual((long)count, response.Body.Length, "exact batch byte bound"); response.Body.CopyTo(bytes); }
                            offset += count;
                        }
                        var bundle = bytes.ToArray();
                        using (var sha = SHA256.Create())
                            AssertEqual(download.BundleSha256, BitConverter.ToString(sha.ComputeHash(bundle)).Replace("-", string.Empty).ToLowerInvariant(),
                                "download metadata identifies the full exact ZIP");
                        var manifest = JObject.Parse(ZipEntryText(bundle, "manifest.json"));
                        AssertEqual(head, (long)manifest["source"]["lastSequence"], "delivery cannot drift to a later stream head");
                        AssertTrue(!ZipEntryText(bundle, "events.jsonl").Contains("CAPTURED"), "metadata redaction survives the transport cutover");
                    }
                    AssertEqual(1, captures, "a slow consumer does not rebuild or reread the source");
                    AssertEqual(blobsBefore, Directory.GetFiles(paths.ChatBlobDirectory, "*.blob", SearchOption.AllDirectories).Length,
                        "a disposable ZIP is not written as another durable resource/store");
                    data.Close(session.Id, TrajectoryExportDownloadService.Owner, download.Data.LeaseId);
                    AssertEqual("RESOURCE_LEASE_EXPIRED", (string)UploadResponse(router.Handle("GET", download.Data.Url + "?offset=0&count=1", CancellationToken.None), 409)["code"],
                        "close releases the one-off payload without affecting the chat stream");
                }
            });
        }

        private static void TrajectoryDownloadBoundsAndLifecycle()
        {
            var session = new ChatSession();
            var now = DateTime.UtcNow;
            var ownerActive = true;
            Action onOwnerCheck = null;
            using (var data = new ResourceDataPlaneService(new ResourceGatewayService(), (_, __) => { onOwnerCheck?.Invoke(); return ownerActive; }, () => now))
            {
                const string owner = "export";
                Func<CancellationToken, ResourceDownloadContent> content = token => new ResourceDownloadContent { Bytes = new byte[] { 65, 66 }, ContentType = "application/zip" };
                data.OpenUpload(session, UploadRequest(session, AttachmentStore.MaxFileBytes));
                data.OpenUpload(session, UploadRequest(session, AttachmentStore.MaxFileBytes));
                var captured = false;
                AssertEqual("RESOURCE_BACKPRESSURE", RuntimeThrows<ResourceRequestException>(() => data.OpenDownload(session, owner,
                    TrajectoryExportService.MaximumBundleBytes, token => { captured = true; return content(token); })).ErrorCode, "uploads and downloads share the byte reservation budget");
                AssertTrue(!captured, "capacity rejection precedes stream/CAS validation and ZIP production");
                data.CloseTransfers();
                data.OpenDownload(session, owner, 2, content);
                data.OpenDownload(session, owner, 2, content);
                AssertEqual("RESOURCE_LEASE_LIMIT", RuntimeThrows<ResourceRequestException>(() => data.OpenDownload(session, owner, 2, content)).ErrorCode,
                    "at most two download buffers can be retained");
                data.CloseWorkspace(session.Id, owner);
                RuntimeThrows<InvalidOperationException>(() => data.OpenDownload(session, owner, 2, token => { throw new InvalidOperationException("capture failed"); }));
                RuntimeThrows<ResourceRequestException>(() => data.OpenDownload(session, owner, 1, content));
                RuntimeThrows<OperationCanceledException>(() => data.OpenDownload(session, owner, 2, content, new CancellationToken(true)));
                RuntimeThrows<ResourceRequestException>(() => data.OpenDownload(session, owner, 24 * 1024 * 1024, token => {
                    data.CloseWorkspace(session.Id, owner);
                    AssertEqual("RESOURCE_BACKPRESSURE", RuntimeThrows<ResourceRequestException>(() => data.OpenDownload(session, owner, 27 * 1024 * 1024, content)).ErrorCode,
                        "a cancelled capture keeps its reservation until it exits");
                    return content(token);
                }));
                var largeReservation = data.OpenDownload(session, owner, AttachmentStore.MaxMessageBytes, content);
                data.Close(session.Id, owner, largeReservation.LeaseId);
                var lease = data.OpenDownload(session, owner, 2, content);
                var router = new ResourceDataRouter(data);
                foreach (var suffix in new[] { "?offset=0&offset=0", "?offset=0&count=262145", "?offset=0&count=1&extra=1" })
                    UploadResponse(router.Handle("GET", lease.Url + suffix, CancellationToken.None), 400);
                UploadResponse(router.Handle("POST", lease.Url + "?offset=0&count=1", CancellationToken.None), 405);
                UploadResponse(router.Handle("GET", lease.Url.Replace("rnassistant.local-resource", "example.com") + "?offset=0&count=1", CancellationToken.None), 403);
                string mime;
                onOwnerCheck = () => {
                    onOwnerCheck = null;
                    AssertEqual("RESOURCE_BACKPRESSURE", RuntimeThrows<ResourceRequestException>(() => data.ReadDownload(lease.LeaseId, 0, 1, CancellationToken.None, out mime)).ErrorCode,
                        "a second in-flight read is rejected without producing another chunk");
                    data.Close(session.Id, owner, lease.LeaseId);
                    AssertEqual("RESOURCE_BACKPRESSURE", RuntimeThrows<ResourceRequestException>(() => data.OpenDownload(session, owner, AttachmentStore.MaxMessageBytes, content)).ErrorCode,
                        "close racing a read retains its occupied buffer budget");
                };
                RuntimeThrows<ResourceRequestException>(() => data.ReadDownload(lease.LeaseId, 0, 1, CancellationToken.None, out mime));
                lease = data.OpenDownload(session, owner, 2, content);
                data.ReadDownload(lease.LeaseId, 0, 1, CancellationToken.None, out mime);
                AssertEqual("RESOURCE_CURSOR_INVALID", RuntimeThrows<ResourceRequestException>(() => data.ReadDownload(lease.LeaseId, 0, 1, CancellationToken.None, out mime)).ErrorCode,
                    "duplicate or reordered byte requests cannot be replayed");
                lease = data.OpenDownload(session, owner, 2, content);
                RuntimeThrows<ResourceRequestException>(() => data.ReadDownload(lease.LeaseId, 0, 3, CancellationToken.None, out mime));
                lease = data.OpenDownload(session, owner, 2, content);
                RuntimeThrows<OperationCanceledException>(() => data.ReadDownload(lease.LeaseId, 0, 1, new CancellationToken(true), out mime));
                lease = data.OpenDownload(session, owner, 2, content);
                ownerActive = false;
                RuntimeThrows<ResourceRequestException>(() => data.ReadDownload(lease.LeaseId, 0, 1, CancellationToken.None, out mime));
                ownerActive = true;
                lease = data.OpenDownload(session, owner, 2, content);
                now = now.AddMinutes(11);
                RuntimeThrows<ResourceRequestException>(() => data.ReadDownload(lease.LeaseId, 0, 1, CancellationToken.None, out mime));
                lease = data.OpenDownload(session, owner, 2, content);
                data.Dispose();
                RuntimeThrows<ObjectDisposedException>(() => data.ReadDownload(lease.LeaseId, 0, 1, CancellationToken.None, out mime));
            }
        }
    }
}
