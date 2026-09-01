using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
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
                        PowerPointToolIds.ReadSlides,
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

                    var readCall = new ToolCall(
                        "powerpoint-read", PowerPointToolIds.ReadSlides,
                        "{\"slideIndex\":1,\"content\":\"both\"}");
                    var read = ExecuteNative(
                        runtime, readCall, runtime.Describe(readCall));
                    AssertEqual(ToolExecutionOutcome.Ok, read.Outcome,
                        "PowerPoint read uses the typed backend");
                    AssertTrue(((string)JObject.Parse(read.Result.DataJson)["text"])
                        .IndexOf("Revenue grew", StringComparison.Ordinal) >= 0,
                        "PowerPoint read keeps the existing result shape");

                    var reads = adapter.PowerPointBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.PowerPointReadSlidesOperation);
                    var bound = executor.ExecuteManual(Command(
                        HtmlWorkspaceToolCatalog.BindDataToolId,
                        "dataName", "powerpoint_slides",
                        "sourceTool", PowerPointToolIds.ReadSlides,
                        "sourceArguments", new JObject
                        {
                            ["content"] = "both",
                            ["maxSlides"] = 20
                        }), tools, new AppSettings(), false, false, session);
                    AssertTrue(bound.Success,
                        "PowerPoint HTML binding shares the typed read route");
                    AssertEqual(reads + 1,
                        adapter.PowerPointBackendCalls.Count(operation =>
                            operation == FakeOfficeAdapter.PowerPointReadSlidesOperation),
                        "PowerPoint HTML binding enters direct backend once");

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
                    host.BeforeRead = operation =>
                    {
                        if (operation ==
                            FakeOfficeAdapter.PowerPointAddSlideOperation)
                            ownerSta = dispatcher.CheckAccess;
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

                    var dispatched = inner.PowerPointBackendCalls.Count(
                        operation => operation ==
                            FakeOfficeAdapter.PowerPointAddSlideOperation);
                    dispatcher.Invoke(() => document.IsAlive = false);
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
