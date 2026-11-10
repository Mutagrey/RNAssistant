using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using System.Threading;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ModelDiagnosticsManifestAndRetention()
        {
            WithTempPaths(paths => {
                var store = new ChatStore(paths);
                var session = store.Create("Word", "diagnostic-manifest", "Manifest.docx", "Manifest");
                session.LastRun = new ChatRunRecord { RunId = "run", TurnId = "turn", Status = "running", StartedUtc = DateTime.UtcNow };
                store.Save(session);
                var events = new InboxFailingEvents(new ChatEventStoreAdapter(store));
                var now = DateTime.UtcNow;
                var buffer = new ModelDiagnosticBuffer(256, 200, 2, TimeSpan.FromSeconds(10), () => now);
                var service = new ModelTracePersistenceService(events, buffer);
                var bytes = Encoding.UTF8.GetBytes("{\"messages\":[{\"role\":\"user\",\"content\":\"exact Ω😀\"}]}");
                var source = new ContextMessagePresentation { MessageIndex = 0, SourceMessageId = "selected", Presentation = ContextPresentationKind.Full };
                var contexts = new List<ContextMessagePresentation> { source };
                contexts.AddRange(Enumerable.Range(0, 1000).Select(index => new ContextMessagePresentation {
                    SourceMessageId = "old-" + index, Presentation = ContextPresentationKind.Excluded, ExclusionKind = ContextExclusionKind.Budget }));
                var options = new LlmRequestOptions { TraceSession = session, TraceContextReceipt = new ContextReceipt {
                    SnapshotId = "snapshot", ToolGeneration = "tools", SkillGeneration = "skills", SchemaGeneration = "schema",
                    OmittedContext = new Dictionary<string, int> { ["Budget"] = 7, ["Explicit"] = 321 } }, ResponseSchemaJson = "{}" };
                service.Configure(options);
                var request = new LlmTraceRecord { Type = "request", RequestId = "off", MessageCount = 1, ContextMessages = contexts,
                    PayloadUtf8Bytes = bytes, PayloadContentType = "application/json" };
                options.TraceSink(request);
                for (var i = 0; i < 100; i++) options.TraceSink(new LlmTraceRecord { Type = "chunk", RequestId = "off", PayloadJson = "[\"large transient chunk\"]" });
                options.TraceSink(new LlmTraceRecord { Type = "response", RequestId = "off", PayloadJson = "{\"large\":\"response\"}", PromptTokens = 12, CompletionTokens = 4, Completed = true });
                AssertTrue(!request.PayloadTextMaterialized, "mandatory manifest reuses HTTP bytes without UTF16 wire materialization");
                var saved = store.ReadEvents(session.Host, session.DocumentKey, session.Id);
                AssertEqual(0, saved.Count(item => item.Type == SessionEventTypes.AssistantChunk), "ordinary SSE creates no canonical events");
                var prepared = saved.Single(item => item.Type == SessionEventTypes.LlmRequest);
                var metadata = prepared.Data.ToObject<LlmTraceRecord>();
                AssertTrue(prepared.Payload == null && saved.Where(item => item.Type == SessionEventTypes.LlmResponse).All(item => item.Payload == null),
                    "ordinary request/response never create raw CAS roots");
                AssertEqual(0L, buffer.RetainedBytes, "diagnostics off retains no raw bytes");
                AssertEqual(17, metadata.ContextMessages.Count, "manifest stores selected entries and only bounded excluded candidates");
                AssertEqual(991, metadata.Manifest.OmittedContext["Budget"], "sink combines compiler omissions and additional defensive trimming");
                AssertEqual(321, metadata.Manifest.OmittedContext["Explicit"], "compiler reasons survive in the mandatory manifest");
                AssertEqual(7, options.TraceContextReceipt.OmittedContext["Budget"], "manifest aggregation cannot mutate the frozen compiler receipt");
                AssertEqual("selected", metadata.ContextMessages[0].SourceMessageId, "actual selected source identity retained");
                AssertEqual("tools", metadata.Manifest.ToolGeneration, "manifest pins captured generation");
                AssertEqual(typeof(ModelContextCompiler).Module.ModuleVersionId.ToString("D"), metadata.Manifest.CompilerBuild, "compiler build is frozen rather than substituted by Inspector");
                AssertEqual((long)bytes.Length, metadata.WireBytes.Value, "manifest describes actual serialized bytes");
                var summary = ModelContextRequestProjection.Summary(saved, prepared, new ModelContextEventDto[0], CancellationToken.None);
                AssertTrue(summary.Reason.Contains("не сохранялись"), "Inspector explicitly distinguishes manifest from exact wire");

                // A failed mandatory manifest stops dispatch; optional capture and
                // optional outcome metadata cannot change an accepted model result.
                events.Fail = true;
                RuntimeThrows<IOException>(() => options.TraceSink(new LlmTraceRecord { Type = "request", RequestId = "must-fail", PayloadUtf8Bytes = bytes }));
                options.TraceSink(new LlmTraceRecord { Type = "response", RequestId = "off", PayloadJson = "response after disk failure" });
                events.Fail = false;
                options = new LlmRequestOptions { TraceSession = session };
                service.Configure(options, true);
                buffer.Fault = () => { throw new IOException("optional capture failure"); };
                options.TraceSink(new LlmTraceRecord { Type = "request", RequestId = "capture-failure", PayloadUtf8Bytes = bytes });
                var failedCapture = store.ReadEvents(session.Host, session.DocumentKey, session.Id).Last(item => item.Type == SessionEventTypes.LlmRequest).Data.ToObject<LlmTraceRecord>();
                AssertEqual(ModelDiagnosticCaptureStatus.CaptureFailed, failedCapture.DiagnosticCapture.Value, "capture failure does not lose the mandatory manifest");
                buffer.Fault = null;
                options.TraceSink(new LlmTraceRecord { Type = "request", RequestId = "on", PayloadUtf8Bytes = bytes });
                var captured = store.ReadEvents(session.Host, session.DocumentKey, session.Id).Last(item => item.Type == SessionEventTypes.LlmRequest).Data.ToObject<LlmTraceRecord>();
                AssertTrue(bytes.SequenceEqual(buffer.Read(session.Id, captured)), "opt-in keeps exact immutable UTF8 bytes");
                bytes[0] = (byte)'X';
                AssertEqual((byte)'{', buffer.Read(session.Id, captured)[0], "caller mutation cannot alter captured wire");
                AssertTrue(buffer.Read("foreign-session", captured) == null, "raw capture remains exact-session scoped");
                options.TraceSink(new LlmTraceRecord { Type = "request", RequestId = "oversize", PayloadUtf8Bytes = new byte[201] });
                var oversized = store.ReadEvents(session.Host, session.DocumentKey, session.Id).Last(item => item.Type == SessionEventTypes.LlmRequest).Data.ToObject<LlmTraceRecord>();
                AssertEqual(ModelDiagnosticCaptureStatus.SizeLimit, oversized.DiagnosticCapture.Value, "oversized diagnostics are omitted explicitly");
                AssertEqual(1L, oversized.DiagnosticDropped.Value, "size drops are counted");
                for (var i = 0; i < 10; i++) options.TraceSink(new LlmTraceRecord { Type = "request", RequestId = "fill-" + i, PayloadUtf8Bytes = new byte[180] });
                AssertTrue(buffer.RetainedBytes <= 256 && buffer.Read(session.Id, captured) == null, "byte/count saturation evicts optional bytes without blocking semantic writes");
                now = now.AddSeconds(11);
                AssertEqual(0L, buffer.RetainedBytes, "expired optional bytes leave no CAS roots");
                AssertTrue(store.ReadEvents(session.Host, session.DocumentKey, session.Id).Where(item => item.Type == SessionEventTypes.LlmRequest).All(item => item.Payload == null),
                    "opt-in captures also stay outside immutable semantic history");
            });
        }
    }
}
