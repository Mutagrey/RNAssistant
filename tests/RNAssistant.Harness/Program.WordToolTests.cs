using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Services;
using RNAssistant.Office.Services;
using RNAssistant.Office.Domains.Word;
using System.Threading;
using RNAssistant.Core.Tools;
using RNAssistant.Office;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void WordToolsUseExactNativeOwnership()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var session = NewSession(adapter);
                    var tools = OfficeToolCatalog.ForHost(adapter.HostName)
                        .Concat(executor.GetControllerTools()).ToList();
                    var runtime = executor.CreateNativeRuntime(
                        session,
                        tools.Where(tool => WordToolIds.Owns(tool.Id) || tool.Id == ResourceToolCatalog.ReadToolId),
                        new AppSettings(), "agent", false);
                    var ids = new[]
                    {
                        WordToolIds.FindText,
                        WordToolIds.Inspect,
                        WordToolIds.WriteText,
                        WordToolIds.ReplaceText,
                        WordToolIds.FormatText,
                        WordToolIds.AddTable,
                        WordToolIds.InsertPageBreak,
                        WordToolIds.AddComment
                    };
                    foreach (var id in ids)
                    {
                        var call = new ToolCall(
                            "word-policy-" + id, id, WordArguments(id));
                        var policy = runtime.Describe(call);
                        AssertTrue(policy != null,
                            "Word family has one exact native registration: " + id);
                        AssertEqual(
                            WordToolIds.IsMutation(id),
                            policy.MayHaveSideEffects,
                            "Word effect policy is source-owned: " + id);
                        if (WordToolIds.IsMutation(id))
                            AssertEqual(
                                ToolVerification.Tool,
                                policy.Policy.Verification,
                                "Word mutation requires tool verification: " + id);
                    }

                    var readCall = new ToolCall(
                        "word-read", ResourceToolCatalog.ReadToolId,
                        "{\"target\":\"Word range: 0:24\",\"representation\":\"text\"}");
                    var read = ExecuteNative(
                        runtime, readCall, runtime.Describe(readCall));
                    AssertEqual(ToolExecutionOutcome.Ok, read.Outcome,
                        "Word read uses the typed backend");
                    AssertTrue(((string)JObject.Parse(read.Result.DataJson)["text"])
                        .StartsWith("Quarterly revenue", StringComparison.Ordinal),
                        "Word read keeps the existing text result shape");

                    var htmlReads = adapter.WordBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.WordReadTextOperation);
                    var bound = executor.ExecuteManual(Command(
                        HtmlWorkspaceToolCatalog.BindDataToolId,
                        "name", "word_text", "target", "Word range: 0:24", "view", "text"), tools, new AppSettings(), false, false, session);
                    AssertTrue(bound.Success,
                        "Word HTML binding shares the typed read route");
                    AssertEqual(htmlReads + 1,
                        adapter.WordBackendCalls.Count(operation =>
                            operation == FakeOfficeAdapter.WordReadTextOperation),
                        "Word HTML binding captures via Gateway, not copied accepted result JSON");

                    var writes = adapter.WordBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.WordWriteOperation);
                    var dryRun = executor.ExecuteManual(Command(
                        WordToolIds.WriteText,
                        "mode", "insert", "text", "dry"),
                        tools, new AppSettings(), true, true, session);
                    AssertTrue(dryRun.Success,
                        "Word native mutation supports non-mutating dry-run");
                    AssertEqual(writes, adapter.WordBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.WordWriteOperation),
                        "Word dry-run never enters its backend mutation");
                    AssertTrue(runtime.Describe(new ToolCall(
                        "word-case", "WORD.WRITE_TEXT", "{}")) == null,
                        "Word native ownership has no case alias");
                });
        }

        private static void WordResourceReadsRetainExactEvidence()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), (executor, adapter) =>
            {
                var source = "\uFEFFЗаголовок\r\n" + new string('w', 70000) + "\r\nКонец 😀";
                adapter.Write(new WordWriteRequest { Mode = "replaceselection", Text = source }, () => { });
                var session = NewSession(adapter);
                executor.BindResourceAuthority(session);
                var tools = OfficeToolCatalog.ForHost("Word").Concat(executor.GetControllerTools()).ToList();
                var runtime = executor.CreateNativeRuntime(session, tools, new AppSettings(), "agent", false);
                var document = executor.ResourceGateway.List(session, LiveDocumentResourceProvider.ProviderName,
                    LiveDocumentResourceProvider.DocumentKind, null, 10).Items.Single();
                var target = ResourceGatewayService.IntentTarget(document);
                var legacyReads = adapter.DocumentSnapshotReadCount;
                var materializations = adapter.WordTextMaterializationCount;
                var first = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = target, ["representation"] = "text" });
                AssertEqual(ToolExecutionOutcome.Ok, first.Outcome, "document uses generic native reader");
                AssertEqual(source, (string)JObject.Parse(first.Result.DataJson)["text"], "whole source preserves BOM/CRLF/Unicode");
                AssertEqual(materializations + 1, adapter.WordTextMaterializationCount, "internal pages share one bounded capture");
                AssertEqual(legacyReads, adapter.DocumentSnapshotReadCount, "Word source never calls generic adapter snapshot fallback");
                var evidence = first.ResourceEvidence.Single();
                AssertTrue(evidence.Resource.IsExact && evidence.Payload != null && evidence.Complete, "source has complete exact CAS evidence");
                var selection = executor.ResourceGateway.List(session, LiveDocumentResourceProvider.ProviderName,
                    LiveDocumentResourceProvider.SelectionKind, null, 10).Items.Single();
                var selected = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = ResourceGatewayService.IntentTarget(selection) });
                AssertEqual(source, (string)JObject.Parse(selected.Result.DataJson)["text"], "selection uses same typed source capture");
                var range = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = "Word range: 0:10" });
                AssertEqual(source.Substring(0, 10), (string)JObject.Parse(range.Result.DataJson)["text"], "range coordinates are exact");
                adapter.Write(new WordWriteRequest { Mode = "replaceselection", Text = "changed" }, () => { });
                var fresh = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId, new JObject { ["target"] = target });
                AssertEqual(ToolExecutionOutcome.Ok, fresh.Outcome, "fresh read observes external change");
                AssertEqual(EvidenceState.Superseded, new EvidenceStateReducer().Reduce(evidence,
                    executor.ResourceAuthority.CaptureMany(new[] { evidence.ScopeId })).State, "old source loses current status");
                var count = adapter.WordTextMaterializationCount;
                var old = executor.ResourceGateway.Read(session, new ResourceReadRequest {
                    Reference = evidence.Resource, Representation = "text", MaxChars = 32000 }).Result;
                AssertEqual(source.Substring(0, 32000), old.Text, "historical source remains exact after drift");
                var continuation = executor.ResourceGateway.Read(session, new ResourceReadRequest {
                    Reference = evidence.Resource, Representation = "text", Cursor = old.NextCursor, MaxChars = 32000 }).Result;
                AssertEqual(source.Substring(32000, 32000), continuation.Text, "historical continuation stays pinned");
                AssertEqual(count, adapter.WordTextMaterializationCount, "historical pages perform no Word reads");
                AssertTrue(!tools.Any(tool => tool.Id == "word.read_text") && DirectToolBindingCatalog.Resolve("word.read_text") == null,
                    "legacy catalog and native binding are gone");
                string error;
                AssertTrue(!ModelToolResultProjection.ValidateAcceptedCall(new ToolCall("old", "word.read_text", "{}"), out error),
                    "old calls cannot be translated or replayed");
                var removed = executor.ExecuteManual(Command("word.read_text"), tools, new AppSettings(), false, false, session);
                AssertEqual("unknown_tool", removed.ErrorCode, "manual legacy reader has no fallback");
            });
        }

        private static void WordResourceBoundsRejectBeforeMaterialization()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                executor.BindResourceAuthority(session);
                var tools = OfficeToolCatalog.ForHost("Word").Concat(executor.GetControllerTools()).ToList();
                var discovered = executor.ResourceGateway.Find(session, "Word range: 0:10", "document");
                AssertTrue(discovered.Items.Any(item => item.Target == "Word range: 0:10"), "explicit character range can be discovered");
                foreach (var target in new[] { "Word range: 0:1000001", "Word range: 10:1", "Word range: 00:1", "Word range: 0-1", "Word range: 0:99999" })
                {
                    var count = adapter.WordTextMaterializationCount;
                    var invalid = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId, "target", target), tools, new AppSettings(), false, false, session);
                    AssertTrue(!invalid.Success, "invalid or oversized range fails: " + target);
                    AssertEqual(count, adapter.WordTextMaterializationCount, "rejected range never materializes text");
                }
                var empty = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId, "target", "Word range: 0:0"), tools, new AppSettings(), false, false, session);
                AssertTrue(empty.Success && (string)JObject.Parse(empty.DataJson)["text"] == "", "explicit empty range is complete");
                adapter.Write(new WordWriteRequest { Mode = "replaceselection", Text = new string('x', WordService.MaximumTextCharacters + 1) }, () => { });
                var document = executor.ResourceGateway.List(session, LiveDocumentResourceProvider.ProviderName,
                    LiveDocumentResourceProvider.DocumentKind, null, 10).Items.Single();
                var before = adapter.WordTextMaterializationCount;
                var oversized = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId, "target", ResourceGatewayService.IntentTarget(document)),
                    tools, new AppSettings(), false, false, session);
                AssertEqual("RESOURCE_SNAPSHOT_TOO_LARGE", oversized.ErrorCode, "oversized document requires explicit narrower range");
                AssertEqual(before, adapter.WordTextMaterializationCount, "document limit precedes source materialization");
            });
        }

        private static void WordToolsPreserveFamilySemantics()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var runtime = WordRuntime(executor, adapter);
                    var findCall = new ToolCall(
                        "word-find", WordToolIds.FindText,
                        "{\"query\":\"revenue\",\"scope\":\"main\"}");
                    var found = ExecuteNative(
                        runtime, findCall, runtime.Describe(findCall));
                    AssertEqual(1,
                        (int)JObject.Parse(found.Result.DataJson)["matchCount"],
                        "Word find preserves literal scope matching");

                    var replaceCall = new ToolCall(
                        "word-replace", WordToolIds.ReplaceText,
                        "{\"find\":\"revenue\",\"replace\":\"sales\",\"scope\":\"main\"}");
                    var replaced = ExecuteNativeConfirmed(
                        runtime, replaceCall, runtime.Describe(replaceCall));
                    AssertEqual(ToolExecutionOutcome.Ok, replaced.Outcome,
                        "Word replacement succeeds through typed service");
                    AssertEqual(ToolEffectEvidence.VerifiedChange,
                        replaced.Evidence.Effect,
                        "Word replacement exposes verified change evidence");
                    AssertTrue(adapter.WordText.IndexOf(
                        "sales", StringComparison.Ordinal) >= 0,
                        "Word replacement updates exact text");

                    var noChange = ExecuteNativeConfirmed(
                        runtime, replaceCall, runtime.Describe(replaceCall));
                    AssertEqual(ToolEffectEvidence.VerifiedNoChange,
                        noChange.Evidence.Effect,
                        "a replacement with no remaining match is verified no-change");

                    var formatCall = new ToolCall(
                        "word-format", WordToolIds.FormatText,
                        "{\"kind\":\"font\",\"bold\":true,\"fontSize\":12}");
                    var formatted = ExecuteNativeConfirmed(
                        runtime, formatCall, runtime.Describe(formatCall));
                    AssertEqual(ToolEffectEvidence.VerifiedChange,
                        formatted.Evidence.Effect,
                        "Word font formatting is verified");
                    var sameFormat = ExecuteNativeConfirmed(
                        runtime, formatCall, runtime.Describe(formatCall));
                    AssertEqual(ToolEffectEvidence.VerifiedNoChange,
                        sameFormat.Evidence.Effect,
                        "matching Word formatting skips dispatch");

                    var tableCall = new ToolCall(
                        "word-table", WordToolIds.AddTable,
                        "{\"values\":[[\"A\",\"B\"],[\"1\",\"2\"]],\"location\":\"end\"}");
                    var table = ExecuteNativeConfirmed(
                        runtime, tableCall, runtime.Describe(tableCall));
                    var tableData = JObject.Parse(table.Result.DataJson);
                    AssertTrue(table.Outcome == ToolExecutionOutcome.Ok &&
                        (int)tableData["rows"] == 2 &&
                        (int)tableData["columns"] == 2,
                        "Word table dimensions are inferred from native values");

                    var commentCall = new ToolCall(
                        "word-comment", WordToolIds.AddComment,
                        "{\"text\":\"Review this\"}");
                    var comment = ExecuteNativeConfirmed(
                        runtime, commentCall, runtime.Describe(commentCall));
                    AssertTrue(comment.Outcome == ToolExecutionOutcome.Ok &&
                        adapter.WordCommentCount == 1,
                        "Word comment mutation keeps its public behavior");

                    var breakCall = new ToolCall(
                        "word-break", WordToolIds.InsertPageBreak, "{}");
                    var pageBreak = ExecuteNativeConfirmed(
                        runtime, breakCall, runtime.Describe(breakCall));
                    AssertTrue(pageBreak.Outcome == ToolExecutionOutcome.Ok &&
                        adapter.WordText.EndsWith("\f", StringComparison.Ordinal),
                        "Word page break is a verified direct mutation");

                    var inspectCall = new ToolCall(
                        "word-inspect", WordToolIds.Inspect,
                        "{\"kind\":\"stats\"}");
                    var inspected = ExecuteNative(
                        runtime, inspectCall, runtime.Describe(inspectCall));
                    AssertEqual(1,
                        (int)JObject.Parse(inspected.Result.DataJson)["tables"],
                        "Word inspect reads the same typed backend state");
                });
        }

        private static void WordToolsClassifyDispatchFaults()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var runtime = WordRuntime(executor, adapter);
                    adapter.WordThrowAfterMutation = true;
                    var call = new ToolCall(
                        "word-fault", WordToolIds.WriteText,
                        "{\"mode\":\"insert\",\"text\":\" dispatched\"}");
                    var result = ExecuteNativeConfirmed(
                        runtime, call, runtime.Describe(call));
                    AssertEqual(ToolExecutionOutcome.Unknown, result.Outcome,
                        "Word failure after dispatch is unknown");
                    AssertEqual(ToolEffectEvidence.Unknown,
                        result.Evidence.Effect,
                        "Word post-dispatch fault never fabricates verification");
                    AssertTrue(adapter.WordText.EndsWith(
                        " dispatched", StringComparison.Ordinal),
                        "fault fixture proves the Word mutation happened");

                    var calls = adapter.WordBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.WordTableOperation);
                    var oversized = new ToolCall(
                        "word-large-table", WordToolIds.AddTable,
                        "{\"rows\":101,\"columns\":100}");
                    var rejected = ExecuteNativeConfirmed(
                        runtime, oversized, runtime.Describe(oversized));
                    AssertEqual(ToolExecutionOutcome.Error, rejected.Outcome,
                        "oversized Word table is rejected before dispatch");
                    AssertEqual(calls, adapter.WordBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.WordTableOperation),
                        "oversized Word table never reaches COM backend");
                });
        }

        private static void WordToolsUseBoundDocumentScope()
        {
            WithTempPaths(paths =>
            {
                using (var dispatcher = new OfficeStaDispatcher())
                {
                    var document = new BoundTestDocument
                    {
                        StableId = "bound-word-document",
                        IsAlive = true
                    };
                    var sessionPort = new BoundTestOfficeSession(
                        dispatcher, document, "bound-word-runtime",
                        new object(), "Word");
                    var inner = FakeOfficeAdapter.ForHost("Word");
                    var host = new BoundTestOfficeAdapter(sessionPort, inner);
                    var ownerSta = false;
                    var sourceOwnerSta = false;
                    host.BeforeRead = operation =>
                    {
                        if (operation == FakeOfficeAdapter.WordWriteOperation)
                            ownerSta = dispatcher.CheckAccess;
                        if (operation == FakeOfficeAdapter.WordReadTextOperation)
                            sourceOwnerSta = dispatcher.CheckAccess;
                    };
                    var executor = new OfficeToolExecutor(
                        host, new VbaJournalStore(paths),
                        new SkillStore(paths), new ToolStore(paths),
                        paths: paths);
                    var chat = new ChatSession
                    {
                        Host = "Word",
                        DocumentKey = "bound-word-document",
                        DocumentTitle = "Bound.docx"
                    };
                    var tools = OfficeToolCatalog.ForHost(host.HostName)
                        .Concat(executor.GetControllerTools()).ToList();
                    var result = executor.ExecuteManual(Command(
                        WordToolIds.WriteText,
                        "mode", "insert", "text", " bound"),
                        tools, new AppSettings(), false, true, chat);
                    AssertTrue(result.Success && ownerSta,
                        "Word mutation stays on the bound document owner STA");
                    var sourceRead = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId,
                        "target", "Word range: 0:8"), tools, new AppSettings(), false, false, chat);
                    AssertTrue(sourceRead.Success && sourceOwnerSta, "resource source uses the same bound owner STA");
                    var sourceReads = inner.WordTextMaterializationCount;

                    var dispatched = inner.WordBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.WordWriteOperation);
                    dispatcher.Invoke(() => document.IsAlive = false);
                    var closed = executor.ExecuteManual(Command(
                        WordToolIds.WriteText,
                        "mode", "insert", "text", " stale"),
                        tools, new AppSettings(), false, true, chat);
                    AssertEqual("active_document_changed", closed.ErrorCode,
                        "closed bound Word document is rejected before mutation");
                    AssertEqual(dispatched,
                        inner.WordBackendCalls.Count(operation =>
                            operation == FakeOfficeAdapter.WordWriteOperation),
                        "closed Word document never reaches direct backend");
                    var closedRead = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId,
                        "target", "Word range: 0:8"), tools, new AppSettings(), false, false, chat);
                    AssertEqual("active_document_changed", closedRead.ErrorCode, "closed bound resource target cannot read Office");
                    AssertEqual(sourceReads, inner.WordTextMaterializationCount, "closed source never materializes");
                }
            });
        }

        private static NativeToolRuntimeAdapter WordRuntime(
            OfficeToolExecutor executor, FakeOfficeAdapter adapter)
        {
            return executor.CreateNativeRuntime(
                NewSession(adapter),
                OfficeToolCatalog.ForHost(adapter.HostName).Where(tool =>
                    WordToolIds.Owns(tool.Id)),
                new AppSettings(), "agent", false);
        }

        private static string WordArguments(string toolId)
        {
            if (toolId == WordToolIds.FindText)
                return "{\"query\":\"revenue\"}";
            if (toolId == WordToolIds.Inspect)
                return "{\"kind\":\"stats\"}";
            if (toolId == WordToolIds.WriteText)
                return "{\"mode\":\"insert\",\"text\":\"x\"}";
            if (toolId == WordToolIds.ReplaceText)
                return "{\"find\":\"revenue\"}";
            if (toolId == WordToolIds.FormatText)
                return "{\"kind\":\"font\",\"bold\":true}";
            if (toolId == WordToolIds.AddComment)
                return "{\"text\":\"x\"}";
            return "{}";
        }
    }
}
