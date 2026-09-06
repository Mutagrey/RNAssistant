using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Services;
using RNAssistant.Office.Services;
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
                        OutlookToolIds.SearchMail,
                        OutlookToolIds.CreateDraft,
                        OutlookToolIds.UpdateMail
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

                    var target = executor.ResourceGateway.Find(session, "", "document").Items.First(item => item.Type == "Outlook mail" && item.Title.StartsWith("Renewal")).Target;
                    var read = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId, "target", target, "representation", "source"),
                        tools, new AppSettings(), false, false, session);
                    AssertTrue(read.Success, "Outlook source uses Gateway");
                    var readData = JObject.Parse((string)JObject.Parse(read.DataJson)["text"]);
                    AssertEqual("Renewal follow-up", (string)readData["message"]["subject"], "source contains message metadata");
                    AssertEqual(1, ((JArray)readData["attachments"]).Count, "source contains attachment metadata");
                    var reads = adapter.OutlookBodyMaterializationCount;
                    var bound = executor.ExecuteManual(Command(HtmlWorkspaceToolCatalog.BindDataToolId,
                        "name", "outlook_mail", "target", target, "view", "source"), tools, new AppSettings(), false, false, session);
                    AssertTrue(bound.Success, "HTML uses the same exact Outlook resource");
                    AssertEqual(reads + 1, adapter.OutlookBodyMaterializationCount, "HTML captures once via Gateway");

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

        private static void OutlookResourcesRetainExactMail()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Outlook"), (executor, adapter) =>
            {
                var body = "\uFEFFПисьмо\r\n" + new string('m', 70000) + "\r\nКонец 😀";
                adapter.OutlookSelectedBody = body;
                var session = NewSession(adapter);
                executor.BindResourceAuthority(session);
                var tools = OfficeToolCatalog.ForHost("Outlook").Concat(executor.GetControllerTools()).ToList();
                var runtime = executor.CreateNativeRuntime(session, tools, new AppSettings(), "agent", false);
                var count = adapter.OutlookBodyMaterializationCount;
                var mail = executor.ResourceGateway.List(session, "document", LiveDocumentResourceProvider.OutlookMailKind, null, 10)
                    .Items.First(item => item.Title.StartsWith("Renewal"));
                AssertEqual(count, adapter.OutlookBodyMaterializationCount, "mail discovery reads headers, not body");
                var target = ResourceGatewayService.IntentTarget(mail);
                var legacy = adapter.DocumentSnapshotReadCount;
                var first = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = target, ["representation"] = "source" });
                AssertEqual(ToolExecutionOutcome.Ok, first.Outcome, "generic source read succeeds");
                var source = (string)JObject.Parse(first.Result.DataJson)["text"];
                AssertEqual(body, (string)JObject.Parse(source)["message"]["body"], "exact mail body preserves Unicode/BOM/CRLF");
                AssertEqual(count + 1, adapter.OutlookBodyMaterializationCount, "all internal source pages share one capture");
                AssertEqual(legacy, adapter.DocumentSnapshotReadCount, "mail source bypasses old adapter snapshot");
                var evidence = first.ResourceEvidence.Single();
                AssertTrue(evidence.Resource.IsExact && evidence.Complete && evidence.Payload != null, "exact complete CAS evidence");

                var metadata = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = target, ["representation"] = "structure" });
                AssertEqual(ToolExecutionOutcome.Ok, metadata.Outcome, "attachment/header view succeeds");
                AssertEqual(count + 1, adapter.OutlookBodyMaterializationCount, "structure does not recapture body");
                AssertTrue(!(bool)JObject.Parse((string)JObject.Parse(metadata.Result.DataJson)["text"])["bodyCaptured"],
                    "metadata is not an empty body capture");

                adapter.OutlookSelectedBody = "changed";
                var fresh = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = target, ["representation"] = "source" });
                AssertEqual(ToolExecutionOutcome.Ok, fresh.Outcome, "fresh source observes drift");
                AssertEqual(EvidenceState.Superseded, new EvidenceStateReducer().Reduce(evidence,
                    executor.ResourceAuthority.CaptureMany(new[] { evidence.ScopeId })).State, "old mail source is superseded");
                count = adapter.OutlookBodyMaterializationCount;
                var retained = executor.ResourceGateway.Read(session, new ResourceReadRequest {
                    Reference = evidence.Resource, Representation = "source", MaxChars = 32000 }).Result;
                var next = executor.ResourceGateway.Read(session, new ResourceReadRequest {
                    Reference = evidence.Resource, Representation = "source", Cursor = retained.NextCursor, MaxChars = 32000 }).Result;
                AssertEqual(source.Substring(0, 32000), retained.Text, "retained mail remains exact");
                AssertEqual(source.Substring(32000, 32000), next.Text, "retained continuation remains exact");
                AssertEqual(count, adapter.OutlookBodyMaterializationCount, "historical mail performs no body reads");
                string error;
                AssertTrue(!tools.Any(tool => tool.Id == "outlook.read_mail") && DirectToolBindingCatalog.Resolve("outlook.read_mail") == null &&
                    !ModelToolResultProjection.ValidateAcceptedCall(new ToolCall("old", "outlook.read_mail", "{}"), out error),
                    "old reader has no catalog, binding or replay alias");
                var removed = executor.ExecuteManual(Command("outlook.read_mail"), tools, new AppSettings(), false, false, session);
                AssertEqual("unknown_tool", removed.ErrorCode, "old manual reader has no fallback");
            });
        }

        private static void OutlookResourcesRetainCollection()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Outlook"), (executor, adapter) =>
            {
                adapter.OutlookSelectedBody = new string('a', 999) + "😀";
                var session = NewSession(adapter);
                executor.BindResourceAuthority(session);
                var tools = OfficeToolCatalog.ForHost("Outlook").Concat(executor.GetControllerTools()).ToList();
                var runtime = executor.CreateNativeRuntime(session, tools, new AppSettings(), "agent", false);
                var candidate = executor.ResourceGateway.Find(session, "", "document").Items.Single(item => item.Type == "Outlook collection");
                AssertEqual(0, adapter.OutlookCollectionCaptureCount, "discovery is body-free");
                var sourceRead = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = candidate.Target, ["representation"] = "text" });
                AssertEqual(ToolExecutionOutcome.Ok, sourceRead.Outcome, "collection source uses common reader");
                var source = JObject.Parse((string)JObject.Parse(sourceRead.Result.DataJson)["text"]);
                var messages = (JArray)source["messages"];
                AssertEqual(2, messages.Count, "bounded collection retains both fixture mails");
                AssertEqual(2, messages.Select(item => (string)item["month"]).Distinct().Count(), "month is a semantic grouping field");
                var preview = messages.Single(item => ((string)item["subject"]).StartsWith("Renewal"));
                AssertEqual(999, ((string)preview["bodyPreview"]).Length, "preview does not split surrogate pairs");
                AssertTrue((bool)preview["bodyTruncated"] && !(bool)source["collectionTruncated"], "body and folder coverage remain separate");
                AssertTrue(source.ToString().IndexOf("entryId", StringComparison.OrdinalIgnoreCase) < 0 && source["folder"] == null,
                    "no folder locator or mail runtime identity in source body");
                var evidence = sourceRead.ResourceEvidence.Single();
                AssertTrue(evidence.Complete && evidence.Payload != null, "complete exact collection is retained in CAS");
                var count = adapter.OutlookCollectionCaptureCount;
                var first = executor.ResourceGateway.Read(session, new ResourceReadRequest {
                    Reference = evidence.Resource, Representation = "records", ViewPath = "$.messages", MaxRows = 1 }).Result;
                AssertEqual(count, adapter.OutlookCollectionCaptureCount, "records derive from the same retained source snapshot");
                AssertTrue(!first.Complete && first.Table.Rows.Count == 1, "records have bounded exact coverage");
                adapter.OutlookExcludeSecondMail = true;
                var fresh = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = candidate.Target, ["representation"] = "text" });
                AssertEqual(ToolExecutionOutcome.Ok, fresh.Outcome, "fresh head captures changed membership");
                AssertEqual(EvidenceState.Superseded, new EvidenceStateReducer().Reduce(evidence,
                    executor.ResourceAuthority.CaptureMany(new[] { evidence.ScopeId })).State, "membership drift supersedes collection evidence");
                count = adapter.OutlookCollectionCaptureCount;
                var next = executor.ResourceGateway.Read(session, new ResourceReadRequest {
                    Reference = first.Resource.Reference, Representation = "records", ViewPath = "$.messages",
                    Cursor = first.NextCursor, MaxRows = 1 }).Result;
                AssertTrue(next.Complete && next.Table.Rows.Count == 1, "old collection continuation retains removed member");
                AssertEqual(count, adapter.OutlookCollectionCaptureCount, "old records never fall forward to live folder");
                var bound = executor.ExecuteManual(Command(HtmlWorkspaceToolCatalog.BindDataToolId,
                    "name", "mail_collection", "target", candidate.Target, "view", "records", "path", "$.messages"),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(bound.Success, "HTML binds the same resource records");
                string error;
                AssertTrue(!tools.Any(tool => tool.Id == "outlook.collect_mail") && DirectToolBindingCatalog.Resolve("outlook.collect_mail") == null &&
                    !ModelToolResultProjection.ValidateAcceptedCall(new ToolCall("old-collection", "outlook.collect_mail", "{}"), out error),
                    "removed collection has no catalog, binding or replay alias");
                AssertEqual("unknown_tool", executor.ExecuteManual(Command("outlook.collect_mail"), tools,
                    new AppSettings(), false, false, session).ErrorCode, "removed manual tool has no fallback");
            });
        }

        private static void OutlookCollectionBounds()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Outlook"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                executor.BindResourceAuthority(session);
                var reference = executor.ResourceGateway.List(session, "document", LiveDocumentResourceProvider.OutlookCollectionKind, null, 10).Items.Single().Reference;
                adapter.OutlookFolderSnapshotTransform = snapshot => {
                    snapshot.TotalItems = OutlookService.MaxItems + 1; snapshot.Truncated = true; return snapshot; };
                var captured = executor.ResourceGateway.Read(session, new ResourceReadRequest { Reference = reference, Representation = "text" }).Result;
                AssertTrue((bool)JObject.Parse(captured.Text)["collectionTruncated"], "bounded source never claims complete folder coverage");
                adapter.OutlookFolderSnapshotTransform = snapshot => { snapshot.Messages[0].Body = new string('x', 1001); return snapshot; };
                var denied = false;
                try { executor.ResourceGateway.Read(session, new ResourceReadRequest { Reference = reference, Representation = "text" }); }
                catch (ResourceRequestException error) { denied = error.ErrorCode == "outlook_collection_invalid"; }
                AssertTrue(denied, "oversized preview fails before publication");
                adapter.OutlookFolderSnapshotTransform = snapshot => {
                    snapshot.Messages = Enumerable.Range(0, 100).Select(index => new OutlookMailSnapshot {
                        EntryId = "budget-" + index, Subject = new string('s', 4096), Sender = new string('r', 4096),
                        Body = new string('b', 1000) }).ToArray();
                    snapshot.TotalItems = 100; return snapshot; };
                denied = false;
                try { executor.ResourceGateway.Read(session, new ResourceReadRequest { Reference = reference, Representation = "text" }); }
                catch (ResourceRequestException error) { denied = error.ErrorCode == "RESOURCE_SNAPSHOT_TOO_LARGE"; }
                AssertTrue(denied, "aggregate collection budget refuses individually valid oversized rows");
                adapter.OutlookFolderSnapshotTransform = snapshot => {
                    snapshot.Messages[0].Body = ""; snapshot.Messages[1].Body = ""; return snapshot; };
                var emptyBodies = new OutlookService(adapter).CaptureCollection(CancellationToken.None);
                AssertEqual("", emptyBodies.Messages[0].Body, "empty preview is legal");
                adapter.OutlookFolderSnapshotTransform = snapshot => { snapshot.Messages = new OutlookMailSnapshot[0]; snapshot.TotalItems = 0; return snapshot; };
                var empty = executor.ResourceGateway.Read(session, new ResourceReadRequest { Reference = reference, Representation = "records", ViewPath = "$.messages" }).Result;
                AssertTrue(empty.Complete && empty.Table.Rows.Count == 0, "empty folder is a complete empty records snapshot");
                adapter.OutlookFolderSnapshotTransform = null;
                adapter.OutlookIsMailTarget = true;
                denied = false;
                try { executor.ResourceGateway.Read(session, new ResourceReadRequest { Reference = reference, Representation = "text" }); }
                catch (ResourceRequestException error) { denied = error.ErrorCode == "outlook_folder_target_missing"; }
                AssertTrue(denied, "Inspector resource cannot read parent folder");
                using (var cancellation = new CancellationTokenSource())
                {
                    cancellation.Cancel(); var count = adapter.OutlookCollectionCaptureCount;
                    try { new OutlookService(adapter).CaptureCollection(cancellation.Token); throw new InvalidOperationException("Cancellation required"); }
                    catch (OperationCanceledException) { }
                    AssertEqual(count, adapter.OutlookCollectionCaptureCount, "cancelled collection never enters backend");
                }
            });
        }

        private static void OutlookResourcesRespectMailScope()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Outlook"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                executor.BindResourceAuthority(session);
                var tools = OfficeToolCatalog.ForHost("Outlook").Concat(executor.GetControllerTools()).ToList();
                var mails = executor.ResourceGateway.List(session, "document", LiveDocumentResourceProvider.OutlookMailKind, null, 10).Items;
                var second = mails.First(item => item.Title.StartsWith("Quarterly"));
                foreach (var inspector in new[] { false, true })
                {
                    adapter.OutlookIsMailTarget = inspector;
                    adapter.OutlookExcludeSecondMail = !inspector;
                    var count = adapter.OutlookBodyMaterializationCount;
                    var listed = executor.ResourceGateway.List(session, "document", LiveDocumentResourceProvider.OutlookMailKind, null, 10);
                    AssertEqual(1, listed.Items.Count, "discovery stays in the bound Inspector/folder");
                    var denied = false;
                    try { executor.ResourceGateway.Read(session, new ResourceReadRequest { Reference = second.Reference, Representation = "source" }); }
                    catch (ResourceRequestException) { denied = true; }
                    AssertTrue(denied, "old URI cannot bypass current bound mail membership");
                    AssertEqual(count, adapter.OutlookBodyMaterializationCount, "out-of-scope mail is rejected before body access");
                    var allowed = executor.ResourceGateway.Read(session, new ResourceReadRequest {
                        Reference = listed.Items.Single().Reference, Representation = "text" }).Result;
                    AssertTrue(allowed.Complete, "the admitted Inspector/folder mail remains readable");
                }
                adapter.OutlookIsMailTarget = false;
                adapter.OutlookExcludeSecondMail = false;
                var boundAlias = second.Reference.Uri.Substring(0, second.Reference.Uri.LastIndexOf('/') + 1) + "mail-bound";
                var aliasDenied = false;
                try { executor.ResourceGateway.Read(session, new ResourceReadRequest { Reference = new ResourceRef(boundAlias), Representation = "text" }); }
                catch (ResourceRequestException) { aliasDenied = true; }
                AssertTrue(aliasDenied, "Inspector-only unsaved-mail alias cannot resolve a folder selection");
                adapter.OutlookDiscoveryTransform = snapshot => {
                    var first = snapshot.Items[0];
                    foreach (var item in snapshot.Items) { item.Subject = first.Subject; item.Sender = first.Sender; item.Received = first.Received; }
                    return snapshot;
                };
                var duplicate = executor.ResourceGateway.List(session, "document", LiveDocumentResourceProvider.OutlookMailKind, null, 10).Items[0];
                var before = adapter.OutlookBodyMaterializationCount;
                var ambiguous = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId,
                    "target", ResourceGatewayService.IntentTarget(duplicate)), tools, new AppSettings(), false, false, session);
                AssertTrue(!ambiguous.Success, "duplicate semantic targets are not silently selected");
                AssertEqual(before, adapter.OutlookBodyMaterializationCount, "ambiguity never materializes mail body");
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

                AssertEqual("RESOURCE_SNAPSHOT_TOO_LARGE", OutlookCaptureError(service,
                    new OutlookReadMailRequest { Content = "message", MaxChars = 10 }), "oversize is explicit, not clipped success");
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
                    AssertTrue(!string.IsNullOrEmpty(OutlookCaptureError(service, request)),
                        "incomplete or mismatched capture fails closed");
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

                    var exact = new OutlookService(adapter).CaptureMail(new OutlookReadMailRequest {
                        EntryId = "mail-2", Content = "message", MaxChars = OutlookService.MaxBodyChars }, CancellationToken.None);
                    AssertEqual("Quarterly plan", exact.Mail.Subject, "runtime-owned EntryID stays exact");

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

                    var source = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId, "target", "selection: Current Office selection", "representation", "source"),
                        tools, new AppSettings(), false, false, chat);
                    AssertTrue(source.Success && readOwnerSta, "exact mail capture stays on bound owner STA");
                    var sourceReads = inner.OutlookBodyMaterializationCount;

                    var dispatched = inner.OutlookBackendCalls.Count(
                        operation => operation ==
                            FakeOfficeAdapter.OutlookCreateDraftOperation);
                    dispatcher.Invoke(() => document.IsAlive = false);
                    var closedSource = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId, "target", "selection: Current Office selection"),
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

        private static string OutlookCaptureError(OutlookService service, OutlookReadMailRequest request)
        {
            try { service.CaptureMail(request, CancellationToken.None); return null; }
            catch (OutlookBackendException error) { return error.ErrorCode; }
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
