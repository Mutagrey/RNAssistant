using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static ResourceUploadOpenRequest UploadRequest(ChatSession session, long length)
        {
            return new ResourceUploadOpenRequest { ChatId = session.Id, FileName = "notes.txt", ContentType = "text/plain", ByteLength = length };
        }

        private static JObject UploadResponse(ResourceStreamResponse response, int status)
        {
            AssertEqual(status, response.StatusCode, "resource route HTTP status");
            using (var reader = new StreamReader(response.Body)) return JObject.Parse(reader.ReadToEnd());
        }

        private static void ResourceUploadStagesThroughDataPlane()
        {
            WithTempPaths(paths =>
            {
                var session = new ChatSession();
                var store = new AttachmentStore(paths);
                var ingestion = new ChatResourceIngestionService(store);
                var bytes = Encoding.UTF8.GetBytes(new string('x', ResourceDataPlaneService.MaximumUploadChunkBytes + 17));
                using (var data = new ResourceDataPlaneService(new ResourceGatewayService(), (chat, owner) => chat == session.Id && owner == ResourceDataPlaneService.UploadOwner))
                {
                    var lease = data.OpenUpload(session, UploadRequest(session, bytes.Length));
                    var router = new ResourceDataRouter(data);
                    var url = lease.Url + "?offset=0&count=" + lease.MaxChunkBytes;
                    var preflight = router.Handle("OPTIONS", url, CancellationToken.None);
                    AssertEqual(204, preflight.StatusCode, "binary POST has an explicit CORS preflight");
                    AssertContains(preflight.Headers, "Access-Control-Allow-Origin: null", "only opaque local origin");
                    AssertContains(preflight.Headers, "Access-Control-Allow-Headers: Content-Type", "only binary MIME request header is admitted");
                    preflight.Body.Dispose();
                    AssertTrue(!JsonConvert.SerializeObject(lease).Contains("base64"), "upload control plane carries metadata only");
                    using (var first = new MemoryStream(bytes, 0, lease.MaxChunkBytes, false))
                    {
                        var ack = UploadResponse(router.Handle("POST", url, CancellationToken.None, first), 200);
                        AssertEqual(lease.LeaseId, (string)ack["leaseId"], "ack is correlated to the exact capability");
                        AssertEqual(lease.MaxChunkBytes, (int)ack["nextOffset"], "first byte offset acknowledged");
                    }
                    using (var last = new MemoryStream(bytes, lease.MaxChunkBytes, 17, false))
                        UploadResponse(router.Handle("POST", lease.Url + "?offset=" + lease.MaxChunkBytes + "&count=17", CancellationToken.None, last), 200);
                    AssertEqual(0, Directory.GetFiles(paths.ChatBlobDirectory, "*.blob", SearchOption.AllDirectories).Length, "chunks do not publish CAS");
                    AssertEqual("RESOURCE_ACCESS_DENIED", RuntimeThrows<ResourceRequestException>(() =>
                        data.CompleteUpload(new ChatSession(), lease.LeaseId, ingestion)).ErrorCode, "another chat cannot consume this upload");
                    var draft = data.CompleteUpload(session, lease.LeaseId, ingestion).Resource;
                    AssertEqual((long)bytes.Length, draft.Size, "exact raw byte length survives staging");
                    AssertTrue(store.ReadBytes(draft).SequenceEqual(bytes), "no base64 or text transcoding");
                    AssertEqual(0, Directory.GetFiles(paths.ChatBlobDirectory, "*.blob", SearchOption.AllDirectories).Length, "draft still waits for user send before CAS promotion");
                    var message = new ChatMessage { Role = "user", Attachments = ingestion.LoadDrafts(session, new[] { draft.Id }).ToList() };
                    session.Messages.Add(message);
                    ingestion.CommitAndLink(session, message, 0);
                    AssertTrue(!string.IsNullOrEmpty(message.Attachments[0].ContentSha256), "existing ingestion owns CAS publication");
                    AssertTrue(session.Artifacts.Count > 0, "existing ingestion links resource before dispatch");
                    ingestion.DeleteDrafts(message);
                    AssertEqual("RESOURCE_LEASE_EXPIRED", RuntimeThrows<ResourceRequestException>(() => data.CompleteUpload(session, lease.LeaseId, ingestion)).ErrorCode,
                        "completion cannot replay a consumed upload");
                    AssertEqual("RESOURCE_LEASE_EXPIRED", (string)UploadResponse(router.Handle("POST", url, CancellationToken.None, new MemoryStream(bytes)), 409)["code"],
                        "a consumed capability cannot accept more bytes");
                }
            });
        }

        private static void ResourceUploadRejectsInvalidChunks()
        {
            WithTempPaths(paths =>
            {
                var session = new ChatSession();
                var ingestion = new ChatResourceIngestionService(new AttachmentStore(paths));
                using (var data = new ResourceDataPlaneService(new ResourceGatewayService()))
                {
                    var router = new ResourceDataRouter(data);
                    foreach (var size in new[] { 0L, AttachmentStore.MaxFileBytes + 1 })
                        AssertEqual("RESOURCE_BATCH_TOO_LARGE", RuntimeThrows<ResourceRequestException>(() => data.OpenUpload(session, UploadRequest(session, size))).ErrorCode,
                            "invalid total length rejected before allocation");
                    var partial = data.OpenUpload(session, UploadRequest(session, 2));
                    AssertEqual("RESOURCE_UPLOAD_INCOMPLETE", RuntimeThrows<ResourceRequestException>(() => data.CompleteUpload(session, partial.LeaseId, ingestion)).ErrorCode,
                        "partial uploads cannot become a draft");
                    foreach (var suffix in new[] { "?offset=0&offset=0", "?offset=0&count=0", "?offset=0&count=262145", "?offset=0&count=1&unknown=0" })
                    {
                        var lease = data.OpenUpload(session, UploadRequest(session, 2));
                        using (var body = new UploadProbeStream(new byte[] { 1 }, () => { throw new Exception("invalid route read a body"); }))
                            UploadResponse(router.Handle("POST", lease.Url + suffix, CancellationToken.None, body), 400);
                        data.CloseUpload(session.Id, lease.LeaseId);
                    }
                    var cases = new[] {
                        new { Offset = 1, Count = 1, Bytes = new byte[] { 1 }, Code = "RESOURCE_CURSOR_INVALID" },
                        new { Offset = 0, Count = 3, Bytes = new byte[] { 1 }, Code = "RESOURCE_BATCH_TOO_LARGE" },
                        new { Offset = 0, Count = 2, Bytes = new byte[] { 1 }, Code = "RESOURCE_UPLOAD_INVALID" },
                        new { Offset = 0, Count = 1, Bytes = new byte[] { 1, 2 }, Code = "RESOURCE_BATCH_TOO_LARGE" }
                    };
                    foreach (var item in cases)
                    {
                        var lease = data.OpenUpload(session, UploadRequest(session, 2));
                        using (var body = new MemoryStream(item.Bytes))
                            AssertEqual(item.Code, (string)UploadResponse(router.Handle("POST", lease.Url + "?offset=" + item.Offset + "&count=" + item.Count,
                                CancellationToken.None, body), 409)["code"], "bounded invalid chunk rejection");
                        AssertEqual("RESOURCE_LEASE_EXPIRED", RuntimeThrows<ResourceRequestException>(() => data.CompleteUpload(session, lease.LeaseId, ingestion)).ErrorCode,
                            "a failed chunk cannot be completed or silently retried");
                    }
                    var repeated = data.OpenUpload(session, UploadRequest(session, 2));
                    data.WriteUpload(repeated.LeaseId, 0, 1, new MemoryStream(new byte[] { 65 }), CancellationToken.None);
                    AssertEqual("RESOURCE_CURSOR_INVALID", RuntimeThrows<ResourceRequestException>(() => data.WriteUpload(repeated.LeaseId, 0, 1,
                        new MemoryStream(new byte[] { 65 }), CancellationToken.None)).ErrorCode, "duplicate chunk is not replayed");
                    var unsupported = data.OpenUpload(session, UploadRequest(session, 2));
                    data.WriteUpload(unsupported.LeaseId, 0, 2, new MemoryStream(new byte[] { 77, 90 }), CancellationToken.None);
                    RuntimeThrows<InvalidOperationException>(() => data.CompleteUpload(session, unsupported.LeaseId, ingestion));
                    var scoped = data.OpenUpload(session, UploadRequest(session, 1));
                    UploadResponse(router.Handle("GET", scoped.Url + "?offset=0&count=1", CancellationToken.None), 405);
                    UploadResponse(router.Handle("POST", ResourceDataPlaneService.Origin + "/v1/" + scoped.LeaseId, CancellationToken.None), 405);
                    UploadResponse(router.Handle("POST", "https://example.com/v1/upload/" + scoped.LeaseId + "?offset=0&count=1", CancellationToken.None), 403);
                    AssertTrue(!Directory.Exists(Path.Combine(paths.AttachmentDirectory, "staging")) ||
                        Directory.GetFiles(Path.Combine(paths.AttachmentDirectory, "staging")).Length == 0, "failed uploads create no staging files");
                    AssertEqual(0, session.Artifacts.Count, "failed uploads do not change lineage");
                }
            });
        }

        private static void ResourceUploadLeaseLifecycle()
        {
            var session = new ChatSession();
            var now = DateTime.UtcNow;
            var ownerActive = true;
            using (var data = new ResourceDataPlaneService(new ResourceGatewayService(), (_, __) => ownerActive, () => now))
            {
                for (var index = 0; index < 4; index++) data.OpenUpload(session, UploadRequest(session, 1));
                AssertEqual("RESOURCE_LEASE_LIMIT", RuntimeThrows<ResourceRequestException>(() => data.OpenUpload(session, UploadRequest(session, 1))).ErrorCode,
                    "four uploads maximum");
                data.CloseTransfers();
                var first = data.OpenUpload(session, UploadRequest(session, AttachmentStore.MaxFileBytes));
                data.OpenUpload(session, UploadRequest(session, AttachmentStore.MaxFileBytes));
                data.OpenUpload(session, UploadRequest(session, 10 * 1024 * 1024));
                AssertEqual("RESOURCE_BACKPRESSURE", RuntimeThrows<ResourceRequestException>(() => data.OpenUpload(session, UploadRequest(session, 1))).ErrorCode,
                    "50 MiB aggregate reservation precedes reading body bytes");
                AssertEqual("RESOURCE_ACCESS_DENIED", RuntimeThrows<ResourceRequestException>(() => data.CloseUpload("another-chat", first.LeaseId)).ErrorCode,
                    "foreign chat cannot revoke uploads");
                using (var body = new UploadProbeStream(new byte[] { 65 }, () => {
                    AssertEqual("RESOURCE_BACKPRESSURE", RuntimeThrows<ResourceRequestException>(() => data.WriteUpload(first.LeaseId, 0, 1,
                        new MemoryStream(new byte[] { 65 }), CancellationToken.None)).ErrorCode, "one operation in flight per capability");
                    data.CloseUpload(session.Id, first.LeaseId);
                    AssertEqual("RESOURCE_BACKPRESSURE", RuntimeThrows<ResourceRequestException>(() => data.OpenUpload(session, UploadRequest(session, 1))).ErrorCode,
                        "closing a busy lease does not release its occupied memory budget");
                }))
                    AssertEqual("RESOURCE_LEASE_EXPIRED", RuntimeThrows<ResourceRequestException>(() => data.WriteUpload(first.LeaseId, 0, 1, body, CancellationToken.None)).ErrorCode,
                        "owner close racing a chunk cannot acknowledge it");
                data.OpenUpload(session, UploadRequest(session, AttachmentStore.MaxFileBytes));
                data.CloseTransfers(session.Id);
                var cancelled = data.OpenUpload(session, UploadRequest(session, 1));
                RuntimeThrows<OperationCanceledException>(() => data.WriteUpload(cancelled.LeaseId, 0, 1, new MemoryStream(new byte[] { 65 }), new CancellationToken(true)));
                RuntimeThrows<ResourceRequestException>(() => data.ValidateUpload(cancelled.LeaseId));
                var expired = data.OpenUpload(session, UploadRequest(session, 1));
                now = now.AddMinutes(11);
                RuntimeThrows<ResourceRequestException>(() => data.ValidateUpload(expired.LeaseId));
                var orphan = data.OpenUpload(session, UploadRequest(session, 1));
                ownerActive = false;
                RuntimeThrows<ResourceRequestException>(() => data.WriteUpload(orphan.LeaseId, 0, 1, new MemoryStream(new byte[] { 65 }), CancellationToken.None));
                ownerActive = true;
                RuntimeThrows<ResourceRequestException>(() => data.ValidateUpload(orphan.LeaseId));
                var disposed = data.OpenUpload(session, UploadRequest(session, 1));
                data.Dispose();
                RuntimeThrows<ObjectDisposedException>(() => data.ValidateUpload(disposed.LeaseId));
            }
        }

        private static void ResourceUploadDiscardsCancelledCompletion()
        {
            WithTempPaths(paths =>
            {
                var session = new ChatSession();
                var ingestion = new ChatResourceIngestionService(new AttachmentStore(paths));
                var staging = Path.Combine(paths.AttachmentDirectory, "staging");
                using (var data = new ResourceDataPlaneService(new ResourceGatewayService(), (_, __) =>
                    !Directory.Exists(staging) || !Directory.GetFiles(staging, "*.meta.json").Any()))
                {
                    var lease = data.OpenUpload(session, UploadRequest(session, 1));
                    data.WriteUpload(lease.LeaseId, 0, 1, new MemoryStream(new byte[] { 65 }), CancellationToken.None);
                    AssertEqual("RESOURCE_LEASE_EXPIRED", RuntimeThrows<ResourceRequestException>(() => data.CompleteUpload(session, lease.LeaseId, ingestion)).ErrorCode,
                        "owner recheck after extraction cannot claim a cancelled draft");
                    AssertEqual(0, Directory.GetFiles(staging).Length, "late staged draft and sidecar are discarded");
                    AssertEqual(0, Directory.GetFiles(paths.ChatBlobDirectory, "*.blob", SearchOption.AllDirectories).Length, "cancellation never publishes CAS");
                }
            });
        }

        private sealed class UploadProbeStream : MemoryStream
        {
            private Action _onRead;
            internal UploadProbeStream(byte[] bytes, Action onRead) : base(bytes, false) { _onRead = onRead; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                var callback = _onRead; _onRead = null; callback?.Invoke();
                return base.Read(buffer, offset, count);
            }
        }
    }
}
