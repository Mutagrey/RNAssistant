using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void CasPrefixVerifiesFullSource()
        {
            foreach (var encrypted in new[] { false, true })
            foreach (var mime in new[] { "application/octet-stream", "application/json" })
            WithTempPaths(paths =>
            {
                var protector = encrypted ? new StorageProtector(HistoryIntegrityModes.Sha256,
                    HistoryEncryptionModes.Aes256CbcHmacSha256, "prefix secret",
                    Enumerable.Range(31, 32).Select(value => (byte)value).ToArray()) : StorageProtector.None;
                var blobs = new ChatBlobStore(paths, () => protector);
                var bytes = Encoding.UTF8.GetBytes(new string('ж', 256 * 1024));
                var reference = blobs.StoreBytes(bytes, mime);
                AssertTrue(bytes.Take(31).SequenceEqual(blobs.ReadPrefix(reference, 31)),
                    "prefix is bounded but exact across raw/compressed/encrypted storage");
                RuntimeThrows<OperationCanceledException>(() => blobs.ReadPrefix(reference, 31, new CancellationToken(true)));
                RuntimeThrows<ArgumentOutOfRangeException>(() => blobs.ReadPrefix(reference, 0));
                var stored = File.ReadAllBytes(blobs.PathFor(reference.Sha256));
                stored[stored.Length - 1] ^= 1;
                File.WriteAllBytes(blobs.PathFor(reference.Sha256), stored);
                AssertTrue(blobs.ReadPrefix(reference, 31) == null, "corruption outside the retained prefix rejects the whole preview");
            });
            WithTempPaths(paths =>
            {
                var blobs = new ChatBlobStore(paths);
                var empty = blobs.StoreBytes(new byte[0], "text/plain");
                AssertEqual(0, blobs.ReadPrefix(empty, 1).Length, "verified empty CAS is not missing");
                var prefix = Encoding.UTF8.GetBytes("RNACAS01" + new string('x', 64));
                AssertTrue(prefix.Take(8).SequenceEqual(blobs.ReadPrefix(blobs.StoreBytes(prefix, "text/plain"), 8)),
                    "raw envelope-prefix bytes use the existing codec identity rules");
            });
        }

        private static void TrajectoryPayloadExactPreview()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Word", "trajectory-payload", "Trace.docx", "Preview");
                var blobs = new ChatBlobStore(paths);
                using (var data = new ResourceDataPlaneService(new ResourceGatewayService()))
                {
                    var service = new TrajectoryPayloadService(new ChatEventStoreAdapter(store), blobs, data);
                    string lastEventId = null;
                    foreach (var text in new[] { "\uFEFF{\"dup\":9007199254740993123456789,\"dup\":\"<script>\"}",
                        new string('x', TrajectoryPayloadService.MaximumCharacters - 1) + "😀tail",
                        "x" + new string('語', 800000) })
                    {
                        var source = store.AppendTrace(session, SessionEventTypes.LlmRequest, new { }, text,
                            "application/json", "run-preview", "turn-preview", "step-preview");
                        lastEventId = source.EventId;
                        var blobCount = Directory.GetFiles(paths.ChatBlobDirectory, "*.blob", SearchOption.AllDirectories).Length;
                        var preview = service.Open(session, source.EventId, CancellationToken.None);
                        AssertEqual(source.Payload.Sha256, preview.Sha256, "source identity is the exact validated event payload");
                        AssertTrue(JObject.FromObject(preview)["text"] == null, "bridge contains no payload text");
                        AssertEqual(text.Length > TrajectoryPayloadService.MaximumCharacters, preview.TextTruncated, "explicit preview extent");
                        AssertEqual(!preview.TextTruncated, preview.Sha256 == preview.Data.Payload.Sha256, "source hash and preview hash are distinct when truncated");
                        AssertEqual("RESOURCE_ACCESS_DENIED", RuntimeThrows<ResourceRequestException>(() =>
                            data.Close("foreign-chat", TrajectoryPayloadService.Owner, preview.Data.LeaseId)).ErrorCode, "foreign close denied");
                        store.AppendTrace(session, SessionEventTypes.LlmResponse, new { }, "later", "text/plain", "later", "later", "later");
                        var router = new ResourceDataRouter(data);
                        using (var result = new MemoryStream())
                        {
                            for (var offset = 0; offset < preview.Data.Payload.ByteLength;)
                            {
                                var count = (int)Math.Min(65536, preview.Data.Payload.ByteLength - offset);
                                var response = router.Handle("GET", preview.Data.Url + "?offset=" + offset + "&count=" + count, CancellationToken.None);
                                AssertEqual(200, response.StatusCode, "sequential shared data-plane read");
                                AssertEqual("text/plain; charset=utf-8", response.ContentType, "preview is transported as inert text");
                                using (response.Body) response.Body.CopyTo(result);
                                offset += count;
                            }
                            var expectedCount = Math.Min(text.Length, TrajectoryPayloadService.MaximumCharacters);
                            if (expectedCount > 0 && char.IsHighSurrogate(text[expectedCount - 1])) expectedCount--;
                            var expected = text.Substring(0, expectedCount);
                            AssertEqual(expected, new UTF8Encoding(false, true).GetString(result.ToArray()), "exact lexical bytes with no split surrogate or head drift");
                            AssertEqual(expected.Length, preview.ReturnedCharacters, "UTF-16 preview extent");
                        }
                        // Only the explicitly appended later payload may add a durable blob.
                        AssertTrue(Directory.GetFiles(paths.ChatBlobDirectory, "*.blob", SearchOption.AllDirectories).Length <= blobCount + 1,
                            "diagnostic preview is not a durable CAS publication");
                        data.Close(session.Id, TrajectoryPayloadService.Owner, preview.Data.LeaseId);
                    }
                    File.AppendAllText(SessionEventFile(paths, session), "{incomplete");
                    RuntimeThrows<ChatConcurrencyException>(() => service.Open(session, lastEventId, CancellationToken.None));
                }
            });
        }

        private static void TrajectoryPayloadSourceFailures()
        {
            WithTempPaths(paths =>
            {
                var session = new ChatSession();
                var blobs = new ChatBlobStore(paths);
                var source = new SessionEvent { SessionId = session.Id, EventId = "payload-event", Payload = blobs.StoreText("", "text/plain") };
                var events = new PayloadPreviewEvents { Items = new[] { source } };
                using (var data = new ResourceDataPlaneService(new ResourceGatewayService()))
                {
                    var service = new TrajectoryPayloadService(events, blobs, data);
                    var empty = service.Open(session, source.EventId, CancellationToken.None);
                    AssertEqual(0L, empty.Data.Payload.ByteLength, "empty source gets an exact empty download");
                    AssertTrue(!empty.TextTruncated, "empty is a full payload, not absent evidence");
                    AssertEqual(SessionEventReadMode.RequireComplete, events.Mode, "preview cannot accept an incomplete journal prefix");
                    data.Close(session.Id, TrajectoryPayloadService.Owner, empty.Data.LeaseId);
                    for (var index = 0; index < 2; index++)
                        data.OpenDownload(session, "other-download", 1, _ => new ResourceDownloadContent {
                            Bytes = new byte[] { 1 }, ContentType = "application/octet-stream" });
                    var reads = events.Reads;
                    RuntimeThrows<ResourceRequestException>(() => service.Open(session, source.EventId, CancellationToken.None));
                    AssertEqual(reads, events.Reads, "shared reservation happens before journal/CAS reads");
                    data.CloseTransfers();
                    RuntimeThrows<OperationCanceledException>(() => service.Open(session, source.EventId, new CancellationToken(true)));
                    AssertEqual(reads, events.Reads, "cancelled capture does not touch the source");
                    RuntimeThrows<InvalidOperationException>(() => service.Open(session, "missing", CancellationToken.None));
                    source.SessionId = "foreign";
                    RuntimeThrows<InvalidOperationException>(() => service.Open(session, source.EventId, CancellationToken.None));
                    source.SessionId = session.Id;
                    events.Items = new[] { source, source };
                    RuntimeThrows<InvalidOperationException>(() => service.Open(session, source.EventId, CancellationToken.None));
                    events.Items = new[] { source };
                    source.Payload.ByteLength = TrajectoryPayloadService.MaximumSourceBytes + 1;
                    AssertContains(RuntimeThrows<InvalidOperationException>(() => service.Open(session, source.EventId, CancellationToken.None)).Message,
                        "RESOURCE_BATCH_TOO_LARGE", "oversized verification source fails explicitly before CAS reading");
                    source.Payload = blobs.StoreBytes(new byte[] { 255 }, "text/plain");
                    RuntimeThrows<DecoderFallbackException>(() => service.Open(session, source.EventId, CancellationToken.None));
                    source.Payload = blobs.StoreText("valid", "text/plain");
                    File.WriteAllText(blobs.PathFor(source.Payload.Sha256), "wrong");
                    RuntimeThrows<InvalidOperationException>(() => service.Open(session, source.EventId, CancellationToken.None));
                    source.Payload = blobs.StoreText("still usable", "text/plain");
                    var final = service.Open(session, source.EventId, CancellationToken.None);
                    data.Close(session.Id, TrajectoryPayloadService.Owner, final.Data.LeaseId);
                }
            });
        }

        private sealed class PayloadPreviewEvents : IEventStore
        {
            internal IReadOnlyList<SessionEvent> Items;
            internal SessionEventReadMode Mode;
            internal int Reads;
            public IReadOnlyList<SessionEvent> Read(ChatSession session, SessionEventReadMode mode) { Reads++; Mode = mode; return Items; }
            public string ReadPayload(ChatSession session, SessionEvent item) { throw new Exception("Whole payload reads are forbidden in this contour."); }
            public SessionEvent Append(ChatSession session, SessionEventWrite write) { throw new NotSupportedException(); }
        }
    }
}
