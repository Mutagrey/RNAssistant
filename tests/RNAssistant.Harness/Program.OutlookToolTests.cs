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
        private static void OutlookToolsUseExactNativeOwnership()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Outlook"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var session = NewSession(adapter);
                    var tools = adapter.GetBuiltInTools()
                        .Concat(executor.GetControllerTools()).ToList();
                    var runtime = OutlookRuntime(executor, adapter);
                    var ids = new[]
                    {
                        OutlookToolIds.ReadMail,
                        OutlookToolIds.SearchMail,
                        OutlookToolIds.CreateDraft,
                        OutlookToolIds.UpdateMail,
                        OutlookToolIds.CollectMail
                    };
                    foreach (var id in ids)
                    {
                        var call = new ToolCall(
                            "outlook-policy-" + id, id,
                            OutlookArguments(id));
                        var policy = runtime.Describe(call);
                        AssertTrue(policy != null,
                            "Outlook family has one exact native registration: " + id);
                        AssertEqual(OutlookToolIds.IsMutation(id),
                            policy.MayHaveSideEffects,
                            "Outlook effect policy is source-owned: " + id);
                        if (OutlookToolIds.IsMutation(id))
                            AssertEqual(ToolVerification.Tool,
                                policy.Policy.Verification,
                                "Outlook mutation requires tool verification: " + id);
                    }

                    var readCall = new ToolCall(
                        "outlook-read", OutlookToolIds.ReadMail,
                        "{\"content\":\"both\",\"maxChars\":12000}");
                    var read = ExecuteNative(
                        runtime, readCall, runtime.Describe(readCall));
                    AssertEqual(ToolExecutionOutcome.Ok, read.Outcome,
                        "Outlook read uses the typed backend");
                    var readData = JObject.Parse(read.Result.DataJson);
                    AssertEqual("Renewal follow-up",
                        (string)readData["message"]["subject"],
                        "Outlook read keeps the message shape");
                    AssertEqual(1,
                        ((JArray)readData["attachments"]).Count,
                        "Outlook attachment projection is preserved");
                    AssertEqual(0, adapter.Executed.Count(command =>
                        OutlookToolIds.Owns(command.ToolId)),
                        "Outlook public ids never reach generic host dispatch");

                    var reads = adapter.OutlookBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.OutlookReadMailOperation);
                    var bound = executor.Execute(Command(
                        HtmlWorkspaceToolCatalog.BindDataToolId,
                        "dataName", "outlook_mail",
                        "sourceTool", OutlookToolIds.ReadMail,
                        "sourceArguments", new JObject
                        {
                            ["content"] = "message",
                            ["maxChars"] = 12000
                        }), tools, new AppSettings(), false, false, session);
                    AssertTrue(bound.Success,
                        "Outlook HTML binding shares the typed read route: " +
                        (bound.Message ?? bound.ErrorCode ?? "no error"));
                    AssertEqual(reads + 1,
                        adapter.OutlookBackendCalls.Count(operation =>
                            operation == FakeOfficeAdapter.OutlookReadMailOperation),
                        "Outlook HTML binding enters direct backend once");

                    var drafts = adapter.OutlookBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.OutlookCreateDraftOperation);
                    var dryRun = executor.Execute(Command(
                        OutlookToolIds.CreateDraft,
                        "kind", "new", "body", "Dry"),
                        tools, new AppSettings(), true, true, session);
                    AssertTrue(dryRun.Success,
                        "Outlook mutation supports non-mutating dry-run");
                    AssertEqual(drafts,
                        adapter.OutlookBackendCalls.Count(operation =>
                            operation == FakeOfficeAdapter.OutlookCreateDraftOperation),
                        "Outlook dry-run never enters backend mutation");
                    AssertTrue(runtime.Describe(new ToolCall(
                        "outlook-case", "OUTLOOK.READ_MAIL", "{}")) == null,
                        "Outlook native ownership has no case alias");
                });
        }

        private static void OutlookToolsPreserveFamilySemantics()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Outlook"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var runtime = OutlookRuntime(executor, adapter);
                    var searchCall = new ToolCall(
                        "outlook-search", OutlookToolIds.SearchMail,
                        "{\"query\":\"quarterly\",\"fields\":\"subject,body\"}");
                    var search = ExecuteNative(
                        runtime, searchCall, runtime.Describe(searchCall));
                    AssertTrue((int)JObject.Parse(
                        search.Result.DataJson)["matchCount"] >= 2,
                        "Outlook search preserves field matching");

                    var collectCall = new ToolCall(
                        "outlook-collect", OutlookToolIds.CollectMail,
                        "{\"groupBy\":\"month\"}");
                    var collected = ExecuteNative(
                        runtime, collectCall, runtime.Describe(collectCall));
                    AssertEqual(2,
                        ((JObject)JObject.Parse(
                            collected.Result.DataJson)["months"]).Count,
                        "Outlook collection preserves monthly grouping");

                    var exactRead = new ToolCall(
                        "outlook-exact", OutlookToolIds.ReadMail,
                        "{\"entryId\":\"mail-2\",\"content\":\"message\"}");
                    var exact = ExecuteNative(
                        runtime, exactRead, runtime.Describe(exactRead));
                    AssertEqual("Quarterly plan",
                        (string)JObject.Parse(exact.Result.DataJson)["subject"],
                        "Outlook explicit EntryID remains exact");

                    var draft = new ToolCall(
                        "outlook-draft", OutlookToolIds.CreateDraft,
                        "{\"kind\":\"new\",\"to\":\"team@example.com\",\"subject\":\"Plan\",\"body\":\"Ready\"}");
                    var drafted = ExecuteNativeConfirmed(
                        runtime, draft, runtime.Describe(draft));
                    AssertEqual(ToolEffectEvidence.VerifiedChange,
                        drafted.Evidence.Effect,
                        "Outlook draft creation exposes verified change");
                    AssertEqual("Ready", adapter.OutlookDraft,
                        "Outlook draft body reaches the direct backend");

                    var categories = new ToolCall(
                        "outlook-categories", OutlookToolIds.UpdateMail,
                        "{\"kind\":\"categories\",\"categories\":\"Customer\"}");
                    AssertEqual(ToolEffectEvidence.VerifiedChange,
                        ExecuteNativeConfirmed(runtime, categories,
                            runtime.Describe(categories)).Evidence.Effect,
                        "Outlook categories update is verified");
                    AssertEqual("Customer", adapter.OutlookSelectedCategories,
                        "Outlook categories read-back is exact");
                    AssertEqual(ToolEffectEvidence.VerifiedNoChange,
                        ExecuteNativeConfirmed(runtime, categories,
                            runtime.Describe(categories)).Evidence.Effect,
                        "Outlook repeated categories update is verified no-change");

                    var markRead = new ToolCall(
                        "outlook-read-state", OutlookToolIds.UpdateMail,
                        "{\"kind\":\"markRead\"}");
                    AssertEqual(ToolEffectEvidence.VerifiedChange,
                        ExecuteNativeConfirmed(runtime, markRead,
                            runtime.Describe(markRead)).Evidence.Effect,
                        "Outlook read-state update is verified");
                    AssertTrue(!adapter.OutlookSelectedUnread,
                        "Outlook markRead read-back is exact");
                });
        }

        private static void OutlookToolsClassifyDispatchFaults()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Outlook"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var runtime = OutlookRuntime(executor, adapter);
                    adapter.OutlookThrowAfterMutation = true;
                    var call = new ToolCall(
                        "outlook-fault", OutlookToolIds.CreateDraft,
                        "{\"kind\":\"new\",\"body\":\"Dispatched\"}");
                    var result = ExecuteNativeConfirmed(
                        runtime, call, runtime.Describe(call));
                    AssertEqual(ToolExecutionOutcome.Unknown, result.Outcome,
                        "Outlook failure after dispatch is unknown");
                    AssertEqual("Dispatched", adapter.OutlookDraft,
                        "fault fixture proves Outlook effect started");

                    var updates = adapter.OutlookBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.OutlookUpdateMailOperation);
                    var oversized = new ToolCall(
                        "outlook-large-categories", OutlookToolIds.UpdateMail,
                        new JObject
                        {
                            ["kind"] = "categories",
                            ["categories"] = new string('x', 65537)
                        }.ToString());
                    var rejected = ExecuteNativeConfirmed(
                        runtime, oversized, runtime.Describe(oversized));
                    AssertEqual(ToolExecutionOutcome.Error, rejected.Outcome,
                        "oversized Outlook categories are rejected before dispatch");
                    AssertEqual(updates,
                        adapter.OutlookBackendCalls.Count(operation =>
                            operation == FakeOfficeAdapter.OutlookUpdateMailOperation),
                        "oversized Outlook update never reaches backend");
                });
        }

        private static void OutlookToolsUseBoundWindowScope()
        {
            WithTempPaths(paths =>
            {
                using (var dispatcher = new OfficeStaDispatcher())
                {
                    var document = new BoundTestDocument
                    {
                        StableId = "bound-outlook-window",
                        IsAlive = true
                    };
                    var sessionPort = new BoundTestOfficeSession(
                        dispatcher, document, "bound-outlook-runtime",
                        new object(), "Outlook");
                    var inner = FakeOfficeAdapter.ForHost("Outlook");
                    var host = new BoundTestOfficeAdapter(sessionPort, inner);
                    var ownerSta = false;
                    host.BeforeRead = operation =>
                    {
                        if (operation ==
                            FakeOfficeAdapter.OutlookCreateDraftOperation)
                            ownerSta = dispatcher.CheckAccess;
                    };
                    var executor = new OfficeToolExecutor(
                        host, new VbaJournalStore(paths),
                        new SkillStore(paths), new ToolStore(paths),
                        paths: paths);
                    var chat = new ChatSession
                    {
                        Host = "Outlook",
                        DocumentKey = "bound-outlook-window",
                        DocumentTitle = "Inbox"
                    };
                    var tools = host.GetBuiltInTools()
                        .Concat(executor.GetControllerTools()).ToList();
                    var result = executor.Execute(Command(
                        OutlookToolIds.CreateDraft,
                        "kind", "new", "body", "Bound"),
                        tools, new AppSettings(), false, true, chat);
                    AssertTrue(result.Success && ownerSta,
                        "Outlook mutation stays on bound owner STA");

                    var dispatched = inner.OutlookBackendCalls.Count(
                        operation => operation ==
                            FakeOfficeAdapter.OutlookCreateDraftOperation);
                    dispatcher.Invoke(() => document.IsAlive = false);
                    var closed = executor.Execute(Command(
                        OutlookToolIds.CreateDraft,
                        "kind", "new", "body", "Stale"),
                        tools, new AppSettings(), false, true, chat);
                    AssertEqual("active_document_changed", closed.ErrorCode,
                        "closed bound Outlook window is rejected");
                    AssertEqual(dispatched,
                        inner.OutlookBackendCalls.Count(operation => operation ==
                            FakeOfficeAdapter.OutlookCreateDraftOperation),
                        "closed Outlook target never reaches direct backend");
                }
            });
        }

        private static NativeToolRuntimeAdapter OutlookRuntime(
            OfficeToolExecutor executor, FakeOfficeAdapter adapter)
        {
            return executor.CreateNativeRuntime(
                NewSession(adapter),
                adapter.GetBuiltInTools().Where(tool =>
                    OutlookToolIds.Owns(tool.Id)),
                new AppSettings(), "agent", false);
        }

        private static string OutlookArguments(string toolId)
        {
            if (toolId == OutlookToolIds.SearchMail)
                return "{\"query\":\"Renewal\"}";
            if (toolId == OutlookToolIds.CreateDraft)
                return "{\"kind\":\"new\"}";
            if (toolId == OutlookToolIds.UpdateMail)
                return "{\"kind\":\"markRead\"}";
            return "{}";
        }
    }
}
