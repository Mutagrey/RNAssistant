using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Outlook;
using System.Threading;
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
                    var tools = OfficeToolCatalog.ForHost(adapter.HostName)
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

                    var reads = adapter.OutlookBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.OutlookReadMailOperation);
                    AppendAcceptedHtmlSource(session, "outlook_html_run",
                        "outlook_html_source", OutlookToolIds.ReadMail,
                        new JObject
                        {
                            ["content"] = "both",
                            ["maxChars"] = 12000
                        }, read.Result);
                    var bound = executor.ExecuteManual(Command(
                        HtmlWorkspaceToolCatalog.BindDataToolId,
                        "name", "outlook_mail"), tools, new AppSettings(), false, false, session);
                    AssertTrue(!bound.Success,
                        "HTML binding rejects removed accepted-result fallback without a resource target");
                    AssertContains(bound.Message, "target", "implicit HTML bind fails for the missing semantic resource target");
                    AssertEqual(reads,
                        adapter.OutlookBackendCalls.Count(operation =>
                            operation == FakeOfficeAdapter.OutlookReadMailOperation),
                        "Rejected implicit HTML binding never recaptures Outlook");

                    var drafts = adapter.OutlookBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.OutlookCreateDraftOperation);
                    var dryRun = executor.ExecuteManual(Command(
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

        private static void OutlookCapturesPreserveCompleteBodies()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Outlook"), (executor, adapter) =>
            {
                var service = new OutlookService(adapter);
                var body = "\uFEFFТело\r\n" + new string('x', 20000) + "\r\nКонец 😀";
                adapter.OutlookSelectedBody = body;
                var request = new OutlookReadMailRequest { Content = "both", MaxChars = OutlookService.MaxBodyChars };
                var count = adapter.OutlookBodyMaterializationCount;
                var exact = service.CaptureMail(request, CancellationToken.None);
                AssertTrue(exact.BodyCaptured, "complete body is explicitly captured");
                AssertEqual(body, exact.Mail.Body, "complete body preserves Unicode/BOM/CRLF");
                AssertEqual(count + 1, adapter.OutlookBodyMaterializationCount, "capture reads body once");
                AssertEqual(1, exact.Attachments.Count, "capture includes attachment metadata");

                var rejected = service.ReadMail(new OutlookReadMailRequest { Content = "message", MaxChars = 10 }, CancellationToken.None);
                AssertEqual(OutlookOutcomeStatus.Error, rejected.Status, "short requested limit is not clipped success");
                AssertEqual("RESOURCE_SNAPSHOT_TOO_LARGE", rejected.ErrorCode, "oversize is explicit");
                adapter.OutlookSelectedBody = new string('x', OutlookService.MaxBodyChars + 1);
                count = adapter.OutlookBodyMaterializationCount;
                var metadata = service.CaptureMail(new OutlookReadMailRequest { Content = "attachments", MaxChars = 1 }, CancellationToken.None);
                AssertTrue(!metadata.BodyCaptured && metadata.Mail.Body == null && metadata.Mail.StateToken == null,
                    "attachment metadata cannot impersonate a captured empty body or mutation guard");
                AssertEqual(1, metadata.Attachments.Count, "large body does not block attachment metadata");
                AssertEqual(count, adapter.OutlookBodyMaterializationCount, "attachment read never materializes body");

                adapter.OutlookSelectedBody = string.Empty;
                exact = service.CaptureMail(request, CancellationToken.None);
                AssertTrue(exact.BodyCaptured && exact.Mail.Body == string.Empty && !string.IsNullOrEmpty(exact.Mail.StateToken),
                    "a genuinely empty body is exact and distinct from no capture");
            });
        }

        private static void OutlookCapturesRejectInvalidGuards()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Outlook"), (executor, adapter) =>
            {
                var service = new OutlookService(adapter);
                var request = new OutlookReadMailRequest { Content = "both", EntryId = "mail-1", MaxChars = OutlookService.MaxBodyChars };
                foreach (var fault in new Func<OutlookMailReadSnapshot, OutlookMailReadSnapshot>[]
                {
                    snapshot => null,
                    snapshot => { snapshot.BodyCaptured = false; return snapshot; },
                    snapshot => { snapshot.Mail.Body = null; return snapshot; },
                    snapshot => { snapshot.Mail.StateToken = null; return snapshot; },
                    snapshot => { snapshot.Mail.EntryId = "another-mail"; return snapshot; },
                    snapshot => { snapshot.Attachments = null; return snapshot; },
                    snapshot => { snapshot.Attachments[0].Size = -1; return snapshot; }
                })
                {
                    adapter.OutlookReadSnapshotTransform = fault;
                    var result = service.ReadMail(request, CancellationToken.None);
                    AssertEqual(OutlookOutcomeStatus.Error, result.Status, "incomplete or mismatched capture fails closed");
                }
                adapter.OutlookReadSnapshotTransform = snapshot => { snapshot.BodyCaptured = false; return snapshot; };
                var runtime = OutlookRuntime(executor, adapter);
                foreach (var call in new[]
                {
                    new ToolCall("guard-reply", OutlookToolIds.CreateDraft, "{\"kind\":\"reply\",\"body\":\"answer\"}"),
                    new ToolCall("guard-update", OutlookToolIds.UpdateMail, "{\"kind\":\"markRead\"}")
                })
                {
                    var writes = adapter.OutlookBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.OutlookCreateDraftOperation || operation == FakeOfficeAdapter.OutlookUpdateMailOperation);
                    var result = ExecuteNativeConfirmed(runtime, call, runtime.Describe(call));
                    AssertEqual(ToolExecutionOutcome.Error, result.Outcome, "uncaptured body cannot authorize a mutation");
                    AssertEqual(writes, adapter.OutlookBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.OutlookCreateDraftOperation || operation == FakeOfficeAdapter.OutlookUpdateMailOperation),
                        "invalid capture is rejected before the mutation backend");
                }
                using (var cancellation = new CancellationTokenSource())
                {
                    adapter.OutlookReadSnapshotTransform = snapshot => { cancellation.Cancel(); return snapshot; };
                    var cancelled = false;
                    try { service.CaptureMail(request, cancellation.Token); }
                    catch (OperationCanceledException) { cancelled = true; }
                    AssertTrue(cancelled, "cancellation after backend capture cannot publish a snapshot");
                    var reads = adapter.OutlookBodyMaterializationCount;
                    try { service.CaptureMail(request, cancellation.Token); }
                    catch (OperationCanceledException) { }
                    AssertEqual(reads, adapter.OutlookBodyMaterializationCount, "pre-cancelled capture never enters backend");
                }
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
                    var readOwnerSta = false;
                    host.BeforeRead = operation =>
                    {
                        if (operation ==
                            FakeOfficeAdapter.OutlookCreateDraftOperation)
                            ownerSta = dispatcher.CheckAccess;
                        if (operation == FakeOfficeAdapter.OutlookReadMailOperation)
                            readOwnerSta = dispatcher.CheckAccess;
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
                    var tools = OfficeToolCatalog.ForHost(host.HostName)
                        .Concat(executor.GetControllerTools()).ToList();
                    var result = executor.ExecuteManual(Command(
                        OutlookToolIds.CreateDraft,
                        "kind", "new", "body", "Bound"),
                        tools, new AppSettings(), false, true, chat);
                    AssertTrue(result.Success && ownerSta,
                        "Outlook mutation stays on bound owner STA");

                    var source = executor.ExecuteManual(Command(OutlookToolIds.ReadMail, "content", "both"),
                        tools, new AppSettings(), false, false, chat);
                    AssertTrue(source.Success && readOwnerSta, "exact mail capture stays on bound owner STA");
                    var sourceReads = inner.OutlookBodyMaterializationCount;

                    var dispatched = inner.OutlookBackendCalls.Count(
                        operation => operation ==
                            FakeOfficeAdapter.OutlookCreateDraftOperation);
                    dispatcher.Invoke(() => document.IsAlive = false);
                    var closedSource = executor.ExecuteManual(Command(OutlookToolIds.ReadMail),
                        tools, new AppSettings(), false, false, chat);
                    AssertTrue(!closedSource.Success, "closed window cannot capture mail");
                    AssertEqual(sourceReads, inner.OutlookBodyMaterializationCount, "closed capture never reaches the body getter");
                    var closed = executor.ExecuteManual(Command(
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
                OfficeToolCatalog.ForHost(adapter.HostName).Where(tool =>
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
