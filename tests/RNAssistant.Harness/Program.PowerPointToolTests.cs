using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Services;
using RNAssistant.Office.Services;
using RNAssistant.Office.Domains.PowerPoint;
using System.Threading;
using RNAssistant.Office;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void PowerPointToolsUseExactNativeOwnership()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("PowerPoint"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var session = NewSession(adapter);
                    var tools = OfficeToolCatalog.ForHost(adapter.HostName)
                        .Concat(executor.GetControllerTools()).ToList();
                    var runtime = PowerPointRuntime(executor, adapter);
                    var ids = new[]
                    {
                        PowerPointToolIds.ListObjects,
                        PowerPointToolIds.SearchText,
                        PowerPointToolIds.AddSlide,
                        PowerPointToolIds.SetText,
                        PowerPointToolIds.ReplaceText,
                        PowerPointToolIds.AddObject,
                        PowerPointToolIds.DuplicateSlide,
                        PowerPointToolIds.MoveSlide
                    };
                    foreach (var id in ids)
                    {
                        var call = new ToolCall(
                            "powerpoint-policy-" + id, id,
                            PowerPointArguments(id));
                        var policy = runtime.Describe(call);
                        AssertTrue(policy != null,
                            "PowerPoint family has one exact native registration: " + id);
                        AssertEqual(PowerPointToolIds.IsMutation(id),
                            policy.MayHaveSideEffects,
                            "PowerPoint effect policy is source-owned: " + id);
                        if (PowerPointToolIds.IsMutation(id))
                            AssertEqual(ToolVerification.Tool,
                                policy.Policy.Verification,
                                "PowerPoint mutation requires tool verification: " + id);
                    }

                    var read = executor.ExecuteManual(Command(
                        ResourceToolCatalog.ReadToolId, "target", "PowerPoint slide: 1", "representation", "source"),
                        tools, new AppSettings(), false, false, session);
                    AssertTrue(read.Success, "PowerPoint source uses the typed resource backend");
                    AssertContains(JObject.Parse(read.DataJson).Value<string>("text"), "Revenue grew",
                        "PowerPoint resource source includes slide text");
                    AssertTrue(!tools.Any(tool => tool.Id == "powerpoint.read_slides"),
                        "PowerPoint direct reader is removed");

                    var reads = adapter.PowerPointBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.PowerPointReadSlidesOperation);
                    var bound = executor.ExecuteManual(Command(
                        HtmlWorkspaceToolCatalog.BindDataToolId,
                        "name", "powerpoint_slides", "target", "PowerPoint slide: 1", "view", "source"),
                        tools, new AppSettings(), false, false, session);
                    AssertTrue(bound.Success, "PowerPoint HTML binding shares the resource capture");
                    AssertEqual(reads + 1,
                        adapter.PowerPointBackendCalls.Count(operation =>
                            operation == FakeOfficeAdapter.PowerPointReadSlidesOperation),
                        "PowerPoint HTML binding reads through Gateway");

                    var writes = adapter.PowerPointBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.PowerPointAddSlideOperation);
                    var dryRun = executor.ExecuteManual(Command(
                        PowerPointToolIds.AddSlide, "title", "Dry"),
                        tools, new AppSettings(), true, true, session);
                    AssertTrue(dryRun.Success,
                        "PowerPoint mutation supports non-mutating dry-run");
                    AssertEqual(writes,
                        adapter.PowerPointBackendCalls.Count(operation =>
                            operation == FakeOfficeAdapter.PowerPointAddSlideOperation),
                        "PowerPoint dry-run never enters backend mutation");
                    AssertTrue(runtime.Describe(new ToolCall(
                        "powerpoint-case", "POWERPOINT.ADD_SLIDE", "{}")) == null,
                        "PowerPoint native ownership has no case alias");
                });
        }

        private static void PowerPointResourcesRetainExactSources()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("PowerPoint"), (executor, adapter) =>
            {
                var source = "\uFEFFСлайд\r\n" + new string('p', 70000) + "\r\nКонец 😀";
                var notes = "Заметки\r\nс пробелами  ";
                adapter.SetText(new PowerPointSetTextRequest { Target = "body", HasSlideIndex = true, SlideIndex = 1, Text = source }, () => { });
                adapter.SetText(new PowerPointSetTextRequest { Target = "notes", HasSlideIndex = true, SlideIndex = 1, Text = notes }, () => { });
                var session = NewSession(adapter);
                executor.BindResourceAuthority(session);
                var tools = OfficeToolCatalog.ForHost("PowerPoint").Concat(executor.GetControllerTools()).ToList();
                var runtime = executor.CreateNativeRuntime(session, tools, new AppSettings(), "agent", false);
                var legacyReads = adapter.DocumentSnapshotReadCount;
                var before = adapter.PowerPointSourceMaterializationCount;
                var first = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = "PowerPoint slide: 1", ["representation"] = "source" });
                AssertEqual(ToolExecutionOutcome.Ok, first.Outcome, "PowerPoint source read succeeds");
                var text = (string)JObject.Parse(first.Result.DataJson)["text"];
                var slide = (JObject)JArray.Parse(text).Single();
                AssertContains((string)slide["text"], source, "source preserves Unicode and line endings");
                AssertEqual(notes, (string)slide["notes"], "source preserves speaker notes");
                AssertEqual(before + 1, adapter.PowerPointSourceMaterializationCount, "all internal pages use one capture");
                AssertEqual(legacyReads, adapter.DocumentSnapshotReadCount, "source bypasses clipped adapter snapshot");
                var evidence = first.ResourceEvidence.Single();
                AssertTrue(evidence.Resource.IsExact && evidence.Complete && evidence.Payload != null, "complete exact CAS evidence");

                var document = executor.ResourceGateway.List(session, LiveDocumentResourceProvider.ProviderName,
                    LiveDocumentResourceProvider.DocumentKind, null, 10).Items.Single();
                var deck = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = ResourceGatewayService.IntentTarget(document), ["representation"] = "text" });
                AssertEqual(ToolExecutionOutcome.Ok, deck.Outcome, "document text uses typed capture");
                AssertContains((string)JObject.Parse(deck.Result.DataJson)["text"], source, "deck text is complete");
                AssertEqual(legacyReads, adapter.DocumentSnapshotReadCount, "document also bypasses adapter snapshot");

                adapter.SetText(new PowerPointSetTextRequest { Target = "notes", HasSlideIndex = true, SlideIndex = 1, Text = "changed" }, () => { });
                var fresh = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = "PowerPoint slide: 1", ["representation"] = "source" });
                AssertEqual(ToolExecutionOutcome.Ok, fresh.Outcome, "fresh source observes note change");
                AssertEqual(EvidenceState.Superseded, new EvidenceStateReducer().Reduce(evidence,
                    executor.ResourceAuthority.CaptureMany(new[] { evidence.ScopeId })).State, "changed notes supersede old source");
                before = adapter.PowerPointSourceMaterializationCount;
                var retained = executor.ResourceGateway.Read(session, new ResourceReadRequest
                    { Reference = evidence.Resource, Representation = "source", MaxChars = 32000 }).Result;
                var continuation = executor.ResourceGateway.Read(session, new ResourceReadRequest
                    { Reference = evidence.Resource, Representation = "source", Cursor = retained.NextCursor, MaxChars = 32000 }).Result;
                AssertEqual(text.Substring(0, 32000), retained.Text, "historical source stays exact");
                AssertEqual(text.Substring(32000, 32000), continuation.Text, "historical continuation stays exact");
                AssertEqual(before, adapter.PowerPointSourceMaterializationCount, "retained source never reads Office");
                string error;
                AssertTrue(!ModelToolResultProjection.ValidateAcceptedCall(new ToolCall("old", "powerpoint.read_slides", "{}"), out error) &&
                    DirectToolBindingCatalog.Resolve("powerpoint.read_slides") == null, "old reader has no replay or native alias");
                var removed = executor.ExecuteManual(Command("powerpoint.read_slides"), tools, new AppSettings(), false, false, session);
                AssertEqual("unknown_tool", removed.ErrorCode, "old manual reader has no fallback");
            });
        }

        private static void PowerPointResourcesRejectIncompleteCaptures()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("PowerPoint"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                executor.BindResourceAuthority(session);
                var tools = OfficeToolCatalog.ForHost("PowerPoint").Concat(executor.GetControllerTools()).ToList();
                AssertTrue(executor.ResourceGateway.Find(session, "PowerPoint slide: 1", "document").Items
                    .Any(item => item.Target == "PowerPoint slide: 1"), "explicit slide target is discoverable");
                foreach (var target in new[] { "PowerPoint slide: 0", "PowerPoint slide: -1", "PowerPoint slide: 01",
                    "PowerPoint slide: 1:2", "PowerPoint slide: 2147483648", "PowerPoint slide: 999" })
                {
                    var before = adapter.PowerPointSourceMaterializationCount;
                    var invalid = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId, "target", target),
                        tools, new AppSettings(), false, false, session);
                    AssertTrue(!invalid.Success, "invalid slide is rejected: " + target);
                    AssertEqual(before, adapter.PowerPointSourceMaterializationCount, "invalid target never materializes source");
                }
                adapter.AddSlide(new PowerPointAddSlideRequest { Title = "Second", Body = "end" }, () => { });
                var service = new PowerPointService(adapter);
                var count = adapter.PowerPointSourceMaterializationCount;
                var rejected = false;
                try { service.CaptureSlides(new PowerPointReadSlidesRequest { MaxSlides = 1, MaxShapesPerSlide = 1000, MaxCharacters = 1000000 }, CancellationToken.None); }
                catch (PowerPointBackendException error) { rejected = error.ErrorCode == "RESOURCE_SNAPSHOT_TOO_LARGE"; }
                AssertTrue(rejected, "deck bound rejects instead of taking first slides");
                AssertEqual(count, adapter.PowerPointSourceMaterializationCount, "deck bound precedes materialization");

                adapter.SetText(new PowerPointSetTextRequest { Target = "body", HasSlideIndex = true, SlideIndex = 1,
                    Text = new string('x', PowerPointService.MaximumTextCharacters + 1) }, () => { });
                var large = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId, "target", "PowerPoint slide: 1"),
                    tools, new AppSettings(), false, false, session);
                AssertEqual("RESOURCE_SNAPSHOT_TOO_LARGE", large.ErrorCode, "oversized slide never returns clipped success");
                AssertEqual(count, adapter.PowerPointSourceMaterializationCount, "character bound precedes materialization");
                var small = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId, "target", "PowerPoint slide: 2"),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(small.Success, "explicit small slide is still readable");
            });
        }

        private static void PowerPointToolsPreserveFamilySemantics()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("PowerPoint"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var runtime = PowerPointRuntime(executor, adapter);
                    var searchCall = new ToolCall(
                        "powerpoint-search", PowerPointToolIds.SearchText,
                        "{\"query\":\"Revenue\",\"scope\":\"deck\"}");
                    var found = ExecuteNative(
                        runtime, searchCall, runtime.Describe(searchCall));
                    AssertEqual(1,
                        (int)JObject.Parse(found.Result.DataJson)["matchCount"],
                        "PowerPoint search preserves literal matching");

                    var replaceCall = new ToolCall(
                        "powerpoint-replace", PowerPointToolIds.ReplaceText,
                        "{\"find\":\"Revenue\",\"replace\":\"Sales\",\"scope\":\"deck\"}");
                    var replaced = ExecuteNativeConfirmed(
                        runtime, replaceCall, runtime.Describe(replaceCall));
                    AssertEqual(ToolEffectEvidence.VerifiedChange,
                        replaced.Evidence.Effect,
                        "PowerPoint replacement exposes verified change");
                    var noChange = ExecuteNativeConfirmed(
                        runtime, replaceCall, runtime.Describe(replaceCall));
                    AssertEqual(ToolEffectEvidence.VerifiedNoChange,
                        noChange.Evidence.Effect,
                        "PowerPoint exhausted replacement is verified no-change");

                    var add = new ToolCall(
                        "powerpoint-add", PowerPointToolIds.AddSlide,
                        "{\"title\":\"Plan\",\"body\":\"Next steps\"}");
                    AssertEqual(ToolEffectEvidence.VerifiedChange,
                        ExecuteNativeConfirmed(runtime, add,
                            runtime.Describe(add)).Evidence.Effect,
                        "PowerPoint slide addition is verified");

                    var notes = new ToolCall(
                        "powerpoint-notes", PowerPointToolIds.SetText,
                        "{\"target\":\"notes\",\"slideIndex\":2,\"text\":\"Talk track\"}");
                    AssertEqual(ToolExecutionOutcome.Ok,
                        ExecuteNativeConfirmed(runtime, notes,
                            runtime.Describe(notes)).Outcome,
                        "PowerPoint notes preserve public semantics");

                    var table = new ToolCall(
                        "powerpoint-table", PowerPointToolIds.AddObject,
                        "{\"kind\":\"table\",\"slideIndex\":2,\"values\":[[\"A\",\"B\"],[1,2]]}");
                    var tableResult = ExecuteNativeConfirmed(
                        runtime, table, runtime.Describe(table));
                    var tableData = JObject.Parse(tableResult.Result.DataJson);
                    AssertTrue((int)tableData["rows"] == 2 &&
                        (int)tableData["columns"] == 2,
                        "PowerPoint table dimensions are inferred from values");

                    var duplicate = new ToolCall(
                        "powerpoint-duplicate", PowerPointToolIds.DuplicateSlide,
                        "{\"slideIndex\":2}");
                    ExecuteNativeConfirmed(
                        runtime, duplicate, runtime.Describe(duplicate));
                    var move = new ToolCall(
                        "powerpoint-move", PowerPointToolIds.MoveSlide,
                        "{\"slideIndex\":3,\"toIndex\":1}");
                    ExecuteNativeConfirmed(runtime, move, runtime.Describe(move));
                    AssertEqual(3, adapter.SlideCount,
                        "PowerPoint duplicate and move share direct state");

                    var list = new ToolCall(
                        "powerpoint-list", PowerPointToolIds.ListObjects,
                        "{\"kind\":\"slides\"}");
                    var listed = ExecuteNative(
                        runtime, list, runtime.Describe(list));
                    AssertEqual(3, JArray.Parse(listed.Result.DataJson).Count,
                        "PowerPoint list sees all typed mutations");
                });
        }

        private static void PowerPointToolsClassifyDispatchFaults()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("PowerPoint"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var runtime = PowerPointRuntime(executor, adapter);
                    adapter.PowerPointThrowAfterMutation = true;
                    var call = new ToolCall(
                        "powerpoint-fault", PowerPointToolIds.AddSlide,
                        "{\"title\":\"Dispatched\"}");
                    var result = ExecuteNativeConfirmed(
                        runtime, call, runtime.Describe(call));
                    AssertEqual(ToolExecutionOutcome.Unknown, result.Outcome,
                        "PowerPoint failure after dispatch is unknown");
                    AssertEqual(2, adapter.SlideCount,
                        "fault fixture proves PowerPoint mutation happened");

                    var calls = adapter.PowerPointBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.PowerPointAddObjectOperation);
                    var oversized = new ToolCall(
                        "powerpoint-large-table", PowerPointToolIds.AddObject,
                        "{\"kind\":\"table\",\"rows\":101,\"columns\":100}");
                    var rejected = ExecuteNativeConfirmed(
                        runtime, oversized, runtime.Describe(oversized));
                    AssertEqual(ToolExecutionOutcome.Error, rejected.Outcome,
                        "oversized PowerPoint table is rejected before dispatch");
                    AssertEqual(calls,
                        adapter.PowerPointBackendCalls.Count(operation =>
                            operation == FakeOfficeAdapter.PowerPointAddObjectOperation),
                        "oversized PowerPoint table never reaches backend");
                });
        }

        private static void PowerPointToolsUseBoundPresentationScope()
        {
            WithTempPaths(paths =>
            {
                using (var dispatcher = new OfficeStaDispatcher())
                {
                    var document = new BoundTestDocument
                    {
                        StableId = "bound-powerpoint-presentation",
                        IsAlive = true
                    };
                    var sessionPort = new BoundTestOfficeSession(
                        dispatcher, document, "bound-powerpoint-runtime",
                        new object(), "PowerPoint");
                    var inner = FakeOfficeAdapter.ForHost("PowerPoint");
                    var host = new BoundTestOfficeAdapter(sessionPort, inner);
                    var ownerSta = false;
                    var sourceOwnerSta = false;
                    host.BeforeRead = operation =>
                    {
                        if (operation ==
                            FakeOfficeAdapter.PowerPointAddSlideOperation)
                            ownerSta = dispatcher.CheckAccess;
                        if (operation == FakeOfficeAdapter.PowerPointReadSlidesOperation)
                            sourceOwnerSta = dispatcher.CheckAccess;
                    };
                    var executor = new OfficeToolExecutor(
                        host, new VbaJournalStore(paths),
                        new SkillStore(paths), new ToolStore(paths),
                        paths: paths);
                    var chat = new ChatSession
                    {
                        Host = "PowerPoint",
                        DocumentKey = "bound-powerpoint-presentation",
                        DocumentTitle = "Bound.pptx"
                    };
                    var tools = OfficeToolCatalog.ForHost(host.HostName)
                        .Concat(executor.GetControllerTools()).ToList();
                    var result = executor.ExecuteManual(Command(
                        PowerPointToolIds.AddSlide, "title", "Bound"),
                        tools, new AppSettings(), false, true, chat);
                    AssertTrue(result.Success && ownerSta,
                        "PowerPoint mutation stays on bound owner STA");

                    var sourceRead = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId,
                        "target", "PowerPoint slide: 1", "representation", "source"), tools, new AppSettings(), false, false, chat);
                    AssertTrue(sourceRead.Success && sourceOwnerSta, "resource source uses the bound presentation STA");
                    var sourceReads = inner.PowerPointSourceMaterializationCount;

                    var dispatched = inner.PowerPointBackendCalls.Count(
                        operation => operation ==
                            FakeOfficeAdapter.PowerPointAddSlideOperation);
                    dispatcher.Invoke(() => document.IsAlive = false);
                    var closedSource = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId,
                        "target", "PowerPoint slide: 1"), tools, new AppSettings(), false, false, chat);
                    AssertTrue(!closedSource.Success, "closed presentation blocks fresh resource capture");
                    AssertEqual(sourceReads, inner.PowerPointSourceMaterializationCount, "closed source never reaches backend");
                    var closed = executor.ExecuteManual(Command(
                        PowerPointToolIds.AddSlide, "title", "Stale"),
                        tools, new AppSettings(), false, true, chat);
                    AssertEqual("active_document_changed", closed.ErrorCode,
                        "closed bound PowerPoint presentation is rejected");
                    AssertEqual(dispatched,
                        inner.PowerPointBackendCalls.Count(operation =>
                            operation == FakeOfficeAdapter.PowerPointAddSlideOperation),
                        "closed presentation never reaches direct backend");
                }
            });
        }

        private static NativeToolRuntimeAdapter PowerPointRuntime(
            OfficeToolExecutor executor, FakeOfficeAdapter adapter)
        {
            return executor.CreateNativeRuntime(
                NewSession(adapter),
                OfficeToolCatalog.ForHost(adapter.HostName).Where(tool =>
                    PowerPointToolIds.Owns(tool.Id)),
                new AppSettings(), "agent", false);
        }

        private static string PowerPointArguments(string toolId)
        {
            if (toolId == PowerPointToolIds.ListObjects)
                return "{\"kind\":\"slides\"}";
            if (toolId == PowerPointToolIds.SearchText)
                return "{\"query\":\"Revenue\"}";
            if (toolId == PowerPointToolIds.SetText)
                return "{\"target\":\"notes\",\"text\":\"x\"}";
            if (toolId == PowerPointToolIds.ReplaceText)
                return "{\"find\":\"Revenue\"}";
            if (toolId == PowerPointToolIds.AddObject)
                return "{\"kind\":\"textBox\",\"text\":\"x\"}";
            if (toolId == PowerPointToolIds.DuplicateSlide)
                return "{\"slideIndex\":1}";
            if (toolId == PowerPointToolIds.MoveSlide)
                return "{\"slideIndex\":1,\"toIndex\":1}";
            return "{}";
        }
    }
}
