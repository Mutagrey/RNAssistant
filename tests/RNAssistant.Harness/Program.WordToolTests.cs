using RNAssistant.Office.Domains;
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
                var smallContext = new AppSettings { ContextWindowOverrideTokens = 4096 };
                var limited = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId,
                    "target", target, "representation", "text"), tools,
                    smallContext, false, false, session);
                AssertTrue(limited.Success, "Word default read follows the configured model context");
                var limitedText = JObject.Parse(limited.DataJson);
                AssertTrue(((string)limitedText["text"]).Length < source.Length &&
                    (int)limitedText["sourceCoverage"]["start"] == 0 &&
                    !(bool)limitedText["sourceComplete"],
                    "oversized model delivery returns one exact first fragment");
                var evidence = first.ResourceEvidence.Single();
                AssertTrue(evidence.Resource.IsExact && evidence.Payload != null && evidence.Complete, "source has complete exact CAS evidence");
                AssertTrue(evidence.ReadMetadata.RequestedCoverage.Kind == ResourceCoverageKinds.Whole &&
                    evidence.ReadMetadata.RepresentationComplete == true, "Office whole read retains its diagnostic coverage");
                var tail = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = target, ["representation"] = "text",
                        ["startChar"] = source.Length - 3, ["charCount"] = 10 });
                AssertEqual(ToolExecutionOutcome.Ok, tail.Outcome, "native Word tail window succeeds");
                var tailMetadata = tail.ResourceEvidence.Single().ReadMetadata;
                AssertTrue(tailMetadata.RequestedCoverage.Start == source.Length - 3 &&
                    tailMetadata.RequestedCoverage.End == source.Length + 7 &&
                    tailMetadata.SourceCoverage.End == source.Length && tailMetadata.SelectionComplete == true &&
                    tailMetadata.RepresentationComplete == true && tailMetadata.SourceComplete == false,
                    "complete selected Word range preserves requested and actual parent coordinates without claiming a whole document");
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
                AssertTrue(!tools.Any(tool => tool.Id == "word.read_text") && !NativeToolRuntimeAdapter.Owns("word.read_text"),
                    "legacy catalog and native binding are gone");
                string error;
                AssertTrue(!ModelToolResultProjection.ValidateAcceptedCall(new ToolCall("old", "word.read_text", "{}"), out error),
                    "old calls cannot be translated or replayed");
                var removed = executor.ExecuteManual(Command("word.read_text"), tools, new AppSettings(), false, false, session);
                AssertEqual("unknown_tool", removed.ErrorCode, "manual legacy reader has no fallback");
            });
        }

        private static void WordSearchRetainsExactSnapshots()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), (executor, adapter) =>
            {
                var body = new string('x', 35000) + "\nneedle NEEDLE\n" + new string('y', 35000);
                adapter.WordStoriesFactory = request => new[] { new WordStorySnapshot {
                    Id = "private-story-key", Kind = request.Scope == "selection" ? "selection" : "main",
                    Start = 100, End = 100 + body.Length, Text = body } };
                var session = NewSession(adapter);
                executor.BindResourceAuthority(session);
                var tools = OfficeToolCatalog.ForHost("Word").Concat(executor.GetControllerTools()).ToList();
                var runtime = executor.CreateNativeRuntime(session, tools, new AppSettings(), "agent", false);
                var discovered = executor.ResourceGateway.Find(session, "", "document");
                AssertEqual(3, discovered.Items.Count(item => item.Type == "Word search scope"), "search scopes are metadata-only resources");
                AssertEqual(0, adapter.WordStoryMaterializationCount, "discovery does not read stories");
                var args = new JObject { ["query"] = "n(e{2})dle", ["mode"] = "regex", ["scope"] = "selection",
                    ["wholeWord"] = true, ["maxResults"] = 1, ["contextChars"] = 10 };
                var found = ExecuteHtmlNative(runtime, WordToolIds.FindText, args);
                AssertEqual(ToolExecutionOutcome.Ok, found.Outcome, "regex search uses exact capture");
                var data = JObject.Parse(found.Result.DataJson);
                AssertEqual(2, (int)data["matchCount"], "case-insensitive regex and whole-word matching preserved");
                AssertTrue((bool)data["truncated"] && (int)data["returnedCount"] == 1, "result bound preserves total matches");
                AssertEqual(35101, (int)data["matches"][0]["start"], "selection coordinates retain absolute start");
                var evidence = found.ResourceEvidence.Single();
                AssertTrue(evidence.Complete && evidence.Payload != null && evidence.Resource.IsExact, "search carries whole exact CAS evidence");
                AssertEqual(1, adapter.WordStoryMaterializationCount, "search hydrates the complete source once");
                var old = executor.ResourceGateway.Read(session, new ResourceReadRequest {
                    Reference = evidence.Resource, Representation = "text", MaxChars = 32000 }).Result;
                AssertTrue(!old.Text.Contains("private-story-key"), "runtime story ids are not source body content");
                body = "changed without matches";
                var negative = ExecuteHtmlNative(runtime, WordToolIds.FindText, args);
                AssertEqual(ToolExecutionOutcome.Ok, negative.Outcome, "negative search is a real observation");
                AssertEqual(0, (int)JObject.Parse(negative.Result.DataJson)["matchCount"], "no matches after drift");
                AssertTrue(negative.ResourceEvidence.Single().Complete, "negative search retains exact source evidence");
                AssertEqual(EvidenceState.Superseded, new EvidenceStateReducer().Reduce(evidence,
                    executor.ResourceAuthority.CaptureMany(new[] { evidence.ScopeId })).State, "zero-match search supersedes old evidence");
                var count = adapter.WordStoryMaterializationCount;
                var next = executor.ResourceGateway.Read(session, new ResourceReadRequest { Reference = evidence.Resource,
                    Representation = "text", Cursor = old.NextCursor, MaxChars = 32000 }).Result;
                AssertTrue(next.Text.Contains("needle NEEDLE"), "historical continuation retains searched bytes");
                AssertEqual(count, adapter.WordStoryMaterializationCount, "historical pages do no Office reads");
                adapter.WordStoriesFactory = request => new[] {
                    new WordStorySnapshot { Kind = "main", Start = 0, End = 6, Text = "needle" },
                    new WordStorySnapshot { Kind = "footnote", Start = 20, End = 26, Text = "needle" } };
                var all = ExecuteHtmlNative(runtime, WordToolIds.FindText, new JObject { ["query"] = "needle", ["scope"] = "all" });
                var allMatches = (JArray)JObject.Parse(all.Result.DataJson)["matches"];
                AssertEqual("footnote", (string)allMatches[1]["story"], "all-story search preserves domain story kinds");
                AssertEqual(20, (int)allMatches[1]["start"], "all-story coordinates remain local to the named story");
                System.IO.File.Delete(executor.Payloads.PathFor(evidence.Payload.Sha256));
                var denied = false;
                try { executor.ResourceGateway.Read(session, new ResourceReadRequest { Reference = evidence.Resource, Representation = "text" }); }
                catch (ResourceRequestException error) { denied = error.ErrorCode == "RESOURCE_SNAPSHOT_UNAVAILABLE"; }
                AssertTrue(denied, "missing exact source never falls back to a fresh search capture");
            });
        }

        private static void WordSearchRejectsInvalidCaptures()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                var tools = OfficeToolCatalog.ForHost("Word").Concat(executor.GetControllerTools()).ToList();
                var runtime = executor.CreateNativeRuntime(
                    session, tools, new AppSettings(), "agent", false);
                var invalid = ExecuteHtmlNative(runtime, WordToolIds.FindText,
                    new JObject { ["query"] = "[", ["mode"] = "regex" });
                AssertTrue(invalid.Outcome == ToolExecutionOutcome.Error &&
                    adapter.WordStoryMaterializationCount == 0,
                    "invalid regex is refused before source capture");
                AssertEqual(ToolRetryPolicy.Replan,
                    invalid.Recovery.RetryPolicy,
                    "invalid Word search asks the model to correct its call");
                adapter.Write(new WordWriteRequest { Mode = "replaceselection", Text = new string('x', 1000001) + "TAIL" }, () => { });
                var large = executor.ExecuteManual(Command(WordToolIds.FindText, "query", "TAIL"), tools, new AppSettings(), false, false, session);
                AssertTrue(large.Success && (int)JObject.Parse(large.DataJson)["matchCount"] == 1,
                    "search reaches text beyond the former source ceiling");
                AssertEqual(1, adapter.WordStoryMaterializationCount, "search uses one exact source capture");
                adapter.Write(new WordWriteRequest { Mode = "replaceselection", Text = "" }, () => { });
                adapter.WordStoriesFactory = request => new[] { new WordStorySnapshot { Kind = "main", Start = 0, End = 1, Text = null } };
                var missing = executor.ExecuteManual(Command(WordToolIds.FindText, "query", "x"), tools, new AppSettings(), false, false, session);
                AssertEqual("word_story_snapshot_invalid", missing.ErrorCode, "missing story text cannot become a successful empty search");
                adapter.WordStoriesFactory = null;
                var empty = executor.ExecuteManual(Command(WordToolIds.FindText, "query", "x"), tools, new AppSettings(), false, false, session);
                AssertTrue(empty.Success && (int)JObject.Parse(empty.DataJson)["matchCount"] == 0, "genuine empty source is searchable");
                var oldAdapter = new WordToolAdapter(adapter).Execute(WordToolIds.FindText,
                    new System.Collections.Generic.Dictionary<string, object> { { "query", "x" } }, null, CancellationToken.None);
                AssertEqual(DomainOutcomeStatus.Error, oldAdapter.Status, "direct Word adapter no longer dispatches search");
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
                adapter.Write(new WordWriteRequest { Mode = "replaceselection", Text = new string('x', 1000001) }, () => { });
                var document = executor.ResourceGateway.List(session, LiveDocumentResourceProvider.ProviderName,
                    LiveDocumentResourceProvider.DocumentKind, null, 10).Items.Single();
                var before = adapter.WordTextMaterializationCount;
                var oversized = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId, "target", ResourceGatewayService.IntentTarget(document)),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(oversized.Success, "large document returns a bounded first window");
                var firstWindow = JObject.Parse(oversized.DataJson);
                AssertEqual(new string('x', ResourceReadRequest.MaximumCharacters),
                    (string)firstWindow["text"], "first window contains exact Word text");
                AssertTrue(!(bool)firstWindow["sourceComplete"] &&
                    (int)firstWindow["sourceCoverage"]["start"] == 0 &&
                    (int)firstWindow["sourceCoverage"]["end"] == ResourceReadRequest.MaximumCharacters,
                    "ordinary read advertises exact partial source coverage");
                AssertEqual(1000001, (int)firstWindow["sourceEndChar"],
                    "first window reports the document end for continuation");
                AssertEqual(before + 1, adapter.WordTextMaterializationCount,
                    "large document materializes only the selected window");
                before = adapter.WordTextMaterializationCount;
                var structure = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId,
                    "target", ResourceGatewayService.IntentTarget(document), "representation", "structure"),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(structure.Success, "large Word document exposes bounded structure");
                var bounds = JObject.Parse((string)JObject.Parse(structure.DataJson)["text"]);
                AssertEqual(0, (int)bounds["start"], "main story starts at zero in the fake host");
                AssertEqual(1000001, (int)bounds["end"],
                    "structure reports the full main-story end");
                AssertEqual(before, adapter.WordTextMaterializationCount,
                    "bounds do not materialize the large document body");
                var rangeStart = (int)bounds["end"] - 24;
                var range = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId,
                    "target", "Word range: " + rangeStart + ":" + bounds["end"]),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(range.Success, "tail of large Word document is readable by exact range");
                var visible = JObject.Parse(range.DataJson);
                AssertEqual(new string('x', 24), (string)visible["text"], "range returns exact tail bytes");
                AssertEqual("character-range", (string)visible["sourceCoverage"]["kind"],
                    "model receives document-relative selected coverage");
                AssertEqual(rangeStart, (int)visible["sourceCoverage"]["start"], "selected range starts at the requested character");
                AssertEqual((int)bounds["end"], (int)visible["sourceCoverage"]["end"], "selected range reaches the document end");
                AssertTrue((bool)visible["selectionComplete"] && (bool)visible["complete"] &&
                    !(bool)visible["sourceComplete"],
                    "complete selected range is not complete document source");
                var commonWindow = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId,
                    "target", ResourceGatewayService.IntentTarget(document), "representation", "text",
                    "startChar", rangeStart, "charCount", 24),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(commonWindow.Success, "the common character selector reaches a large Word tail");
                var commonData = JObject.Parse(commonWindow.DataJson);
                AssertEqual(new string('x', 24), (string)commonData["text"],
                    "provider pushdown returns only the selected Word window");
                AssertTrue((bool)commonData["selectionComplete"] &&
                    (int)commonData["sourceCoverage"]["start"] == rangeStart &&
                    !(bool)commonData["sourceComplete"],
                    "shared projection retains document-relative partial coverage");
                adapter.Write(new WordWriteRequest { Mode = "replaceselection", Text = "short" }, () => { });
                var materialized = adapter.WordTextMaterializationCount;
                var stale = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId,
                    "target", "Word range: " + rangeStart + ":" + bounds["end"]),
                    tools, new AppSettings(), false, false, session);
                AssertEqual("RESOURCE_TARGET_INVALID", stale.ErrorCode,
                    "a range from old structure bounds cannot be read after document shrink");
                AssertEqual(materialized, adapter.WordTextMaterializationCount,
                    "invalid stale range is refused before text materialization");
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
                    var searchEvidence = found.ResourceEvidence.Single();
                    AssertTrue(new EvidenceStateReducer().Reduce(searchEvidence,
                        executor.ResourceAuthority.CaptureMany(new[] { searchEvidence.ScopeId })).State != EvidenceState.Current,
                        "verified Word mutation invalidates prior search evidence through shared authority");

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
                    var searchOwnerSta = false;
                    host.BeforeRead = operation =>
                    {
                        if (operation == FakeOfficeAdapter.WordWriteOperation)
                            ownerSta = dispatcher.CheckAccess;
                        if (operation == FakeOfficeAdapter.WordReadTextOperation)
                            sourceOwnerSta = dispatcher.CheckAccess;
                        if (operation == FakeOfficeAdapter.WordReadStoriesOperation)
                            searchOwnerSta = dispatcher.CheckAccess;
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
                    var searchRead = executor.ExecuteManual(Command(WordToolIds.FindText,
                        "query", "bound"), tools, new AppSettings(), false, false, chat);
                    AssertTrue(searchRead.Success && searchOwnerSta, "search resource capture uses the bound owner STA");
                    var storyReads = inner.WordStoryMaterializationCount;
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
                    var closedSearch = executor.ExecuteManual(Command(WordToolIds.FindText,
                        "query", "bound"), tools, new AppSettings(), false, false, chat);
                    AssertEqual("active_document_changed", closedSearch.ErrorCode, "closed bound search cannot read Office");
                    AssertEqual(storyReads, inner.WordStoryMaterializationCount, "closed search never materializes stories");
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
