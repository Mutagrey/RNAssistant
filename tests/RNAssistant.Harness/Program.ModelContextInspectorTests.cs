using System;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ModelContextExactExchange()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Word", "model-context", "Context.docx", "Context");
                var blobs = new ChatBlobStore(paths);
                var source = "{\"number\":9007199254740993123456789,\"message\":\"" + new string('語', 600000) + "😀\"}";
                var request = store.AppendTrace(session, SessionEventTypes.LlmRequest,
                    new LlmTraceRecord { RequestId = "one", ModelAttemptId = "attempt-1", Streaming = true }, source,
                    "application/json", "run", "turn", "one");
                store.AppendTrace(session, SessionEventTypes.AssistantChunk, new LlmTraceRecord { RequestId = "one", ChunkIndex = 0 },
                    "[\"data frame\"]", "application/json", "run", "turn", "one");
                var response = store.AppendTrace(session, SessionEventTypes.LlmResponse, new LlmTraceRecord { RequestId = "one" },
                    "{\"Content\":\"done\"}", "application/json", "run", "turn", "one");
                var retry = store.AppendTrace(session, SessionEventTypes.LlmRequest, new LlmTraceRecord { RequestId = "two", ModelAttemptId = "attempt-2" },
                    "{}", "application/json", "run", "turn", "two");
                store.AppendTrace(session, SessionEventTypes.LlmFailure, new LlmTraceRecord { RequestId = "two", Error = "timeout" },
                    null, "application/json", "run", "turn", "two");
                using (var data = new ResourceDataPlaneService(new ResourceGatewayService()))
                {
                    var service = new ModelContextInspectorService(new ChatEventStoreAdapter(store), blobs, data);
                    var page = service.Query(session, new ModelContextQuery(), CancellationToken.None);
                    AssertEqual(retry.EventId, page.Events[0].EventId, "latest prepared request first");
                    AssertEqual(2, page.Events.Count, "attempts are not collapsed");
                    var detail = service.Query(session, new ModelContextQuery { RequestEventId = request.EventId }, CancellationToken.None);
                    AssertEqual(3, detail.Events.Count, "request, matching chunks and response only");
                    AssertEqual(response.EventId, detail.Events.Last().EventId, "exact matching response");
                    AssertTrue(service.Query(session, new ModelContextQuery { KnownRevision = session.Revision }, CancellationToken.None).Unchanged,
                        "unchanged revision avoids re-reading the event stream");
                    var download = service.Open(session, new ModelContextPayloadQuery { EventId = request.EventId }, CancellationToken.None);
                    var bytes = new byte[download.Data.Payload.ByteLength];
                    for (var offset = 0; offset < bytes.Length;)
                    {
                        string mime;
                        var chunk = data.ReadDownload(download.Data.LeaseId, offset, Math.Min(65536, bytes.Length - offset), CancellationToken.None, out mime);
                        Array.Copy(chunk, 0, bytes, offset, chunk.Length); offset += chunk.Length;
                    }
                    AssertEqual(source, Encoding.UTF8.GetString(bytes), "full payload above 512 KiB, Unicode and number spelling preserved");
                    AssertEqual(request.Payload.Sha256, download.Data.Payload.Sha256, "download retains stored byte identity");
                    data.Close(session.Id, ModelContextInspectorService.Owner, download.Data.LeaseId);
                    RuntimeThrows<InvalidOperationException>(() => service.Open(session, new ModelContextPayloadQuery { EventId = "foreign-event" }, CancellationToken.None));
                    RuntimeThrows<OperationCanceledException>(() => service.Query(session, new ModelContextQuery(), new CancellationToken(true)));
                    System.IO.File.Delete(blobs.PathFor(request.Payload.Sha256));
                    RuntimeThrows<InvalidOperationException>(() => service.Open(session, new ModelContextPayloadQuery { EventId = request.EventId }, CancellationToken.None));
                }
            });
        }

        private static void ModelContextSourceAndPagination()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Word", "context-source", "Source.docx", "Source");
                var blobs = new ChatBlobStore(paths);
                var original = PayloadRef.FromBlob(blobs.StoreText("original source", "text/plain"));
                var options = new LlmRequestOptions { TraceSession = session, TraceStepId = "step", TraceModelAttemptId = "attempt" };
                new ModelTracePersistenceService(new ChatEventStoreAdapter(store)).Configure(options);
                options.TraceSink(new LlmTraceRecord { Type = "request", RequestId = "first", PayloadJson = "{}",
                    PayloadContentType = "application/json", ContextMessages = new System.Collections.Generic.List<ContextMessagePresentation> {
                        new ContextMessagePresentation { MessageIndex = 0, Presentation = ContextPresentationKind.Summary, OriginalPayload = original } } });
                var request = store.ReadEvents(session.Host, session.DocumentKey, session.Id).Single(e => e.Type == SessionEventTypes.LlmRequest);
                for (var i = 0; i < 51; i++) store.AppendTrace(session, SessionEventTypes.LlmRequest,
                    new LlmTraceRecord { RequestId = "request-" + i }, "{}", "application/json", null, null, null);
                using (var data = new ResourceDataPlaneService(new ResourceGatewayService()))
                {
                    var service = new ModelContextInspectorService(new ChatEventStoreAdapter(store), blobs, data);
                    var page = service.Query(session, new ModelContextQuery(), CancellationToken.None);
                    AssertEqual(50, page.Events.Count, "bounded attempt page");
                    AssertTrue(page.Events.All(e => e.Trace.ContextMessages == null), "list omits full provenance");
                    var older = service.Query(session, new ModelContextQuery { BeforeSequence = page.NextBeforeSequence }, CancellationToken.None);
                    AssertEqual(2, older.Events.Count, "older attempts retained");
                    var detail = service.Query(session, new ModelContextQuery { RequestEventId = request.EventId }, CancellationToken.None);
                    AssertEqual(ContextPresentationKind.Summary, detail.Events[0].Trace.ContextMessages[0].Presentation, "typed provenance survives trace storage");
                    var download = service.Open(session, new ModelContextPayloadQuery { EventId = request.EventId, OriginalIndex = 0 }, CancellationToken.None);
                    AssertEqual(original.Sha256, download.Data.Payload.Sha256, "original source is bound to the exact request");
                    data.Close(session.Id, ModelContextInspectorService.Owner, download.Data.LeaseId);
                    RuntimeThrows<InvalidOperationException>(() => service.Open(session, new ModelContextPayloadQuery { EventId = request.EventId, OriginalIndex = 1 }, CancellationToken.None));
                }
            });
        }

        private static void ModelContextCompilerPresentation()
        {
            WithTempPaths(paths =>
            {
                var blobs = new ChatBlobStore(paths);
                var scope = new ResourceAuthorityScopeId("document", "context-inspector");
                var reference = new ResourceRef("rna://vba/context-inspector/module", "r1");
                var authority = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new[] {
                    new ResourceAuthoritySnapshot(scope, 1, null, 0, new[] { ResourceHeadState.Known(reference, 1) }) }),
                    "pack", new SkillCatalogSnapshot(null), null, 1);
                var full = new ChatMessage { Role = "user", Content = "exact user text" };
                var excluded = new ChatMessage { Role = "assistant", Content = "local only", ExcludeFromModelContext = true };
                var source = PayloadRef.FromBlob(blobs.StoreText("selected lines", "text/plain"));
                var fragment = new ChatMessage { Role = "assistant", ProtocolMessage = true, SyntheticResourceObservation = true,
                    Content = "{\"target\":\"Module1\"}", ResultPayload = source,
                    ResourceEvidence = new System.Collections.Generic.List<ResourceEvidence> { new ResourceEvidence("fragment", scope,
                        reference, "source", new ResourceCoverage(ResourceCoverageKinds.LineRange, start: 1, end: 2), true, 1, source) } };
                var compiler = new ModelContextCompiler(blobs);
                var snapshot = compiler.Compile(authority, new ChatMessage[0], new[] { full, excluded, fragment }, null,
                    new RNAssistant.Core.Tools.ToolCatalogEntry[0], new AppSettings(), 10000);
                AssertEqual(ContextPresentationKind.Full, snapshot.Receipt.Messages.Single(e => e.SourceMessageId == full.Id).Presentation, "unchanged text is full");
                AssertEqual(ContextPresentationKind.Excluded, snapshot.Receipt.Messages.Single(e => e.SourceMessageId == excluded.Id).Presentation, "excluded is outside wire positions");
                var entry = snapshot.Receipt.Messages.Single(e => e.SourceMessageId == fragment.Id);
                AssertEqual(ContextPresentationKind.Fragment, entry.Presentation, "range coverage determines fragment without text heuristics");
                AssertEqual(source.Sha256, entry.OriginalPayload.Sha256, "original CAS binding remains diagnostic");
                var mutation = ContinuityResult("saved", "common.vba_write_module", RNAssistant.Core.Tools.Contracts.ToolResult.Ok("Saved", "{\"changed\":true}"));
                mutation.ResourceEffect = new ResourceEffect("effect", "write", ResourceEffectOutcome.VerifiedChanged, verification: "read-back");
                var folded = compiler.Compile(authority, new ChatMessage[0], new[] { ContinuityCall(mutation), mutation }, null,
                    new RNAssistant.Core.Tools.ToolCatalogEntry[0], new AppSettings(), 10000);
                AssertEqual(ContextPresentationKind.Summary, folded.Receipt.Messages.First(e => e.MessageIndex.HasValue).Presentation, "folded operation is summary");
            });
        }

        private static void ModelContextFullPreview()
        {
            bool truncated;
            var content = new string('ж', 600000) + "😀";
            var messages = new[] { new ChatMessage { Role = "user", Content = content } };
            var raw = PromptContextInspectorService.BuildRawRequest("chat", "test", messages, new LlmRequestOptions(), out truncated, true);
            AssertTrue(!truncated, "full preview has no 512K clipping");
            AssertEqual(content, (string)JObject.Parse(raw)["messages"][0]["content"], "full preview content roundtrip");
        }
    }
}
