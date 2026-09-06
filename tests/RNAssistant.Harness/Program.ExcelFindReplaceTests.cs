using System;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Tools;
using RNAssistant.Office.Services;
using RNAssistant.Office.Domains.Excel;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ExcelSearchRetainsExactSnapshots()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var text = new string('x', 17000) + " needle NEEDLE " + new string('y', 13000);
                adapter.SetExcelCell("Data", "D1", text);
                var session = NewSession(adapter);
                executor.BindResourceAuthority(session);
                var gateway = executor.ResourceGateway;
                AssertTrue(gateway.Find(session, "", "document").Items.Any(item => item.Type == "Excel search scope"), "search scopes discoverable");
                AssertEqual(0, adapter.ExcelSearchCellCaptureCount, "discovery never captures cell contents");
                AssertTrue(gateway.Find(session, "Excel search scope: range 'Data'!D1", "document").Items.Any(item => item.Type == "Excel search scope"),
                    "explicit search range is discoverable without treating it as a plain range target");
                AssertEqual(gateway.ResolveIntentTarget(session, "Excel search scope: range 'Data'!d1").Reference.Uri,
                    gateway.ResolveIntentTarget(session, "Excel search scope: range 'DATA'!D1").Reference.Uri,
                    "case-insensitive input spelling shares one logical search scope");
                var runtime = executor.CreateNativeRuntime(session, OfficeToolCatalog.ForHost("Excel").Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings(), "agent", false);
                var args = new JObject { ["sheet"] = "Data", ["address"] = "D1", ["query"] = "n(e{2})dle",
                    ["mode"] = "regex", ["wholeWord"] = true, ["maxResults"] = 1 };
                var found = ExecuteHtmlNative(runtime, ExcelFindReplaceToolIds.FindCells, args);
                AssertEqual(ToolExecutionOutcome.Ok, found.Outcome, "exact search succeeds: " + found.Result.DataJson);
                var data = JObject.Parse(found.Result.DataJson);
                AssertTrue((int)data["matchCount"] == 2 && (bool)data["truncated"], "matching count and result limit preserved");
                AssertTrue(data["scopeSha256"] == null && data["contentSha256"] == null && data["matches"][0]["value"] == null && data["matches"][0]["formula"] == null,
                    "public search has no hash guards or full cell copies");
                AssertEqual(17001, (int)data["matches"][0]["start"], "field-local coordinates retained");
                var evidence = found.ResourceEvidence.Single();
                AssertTrue(evidence.Complete && evidence.Payload != null && evidence.Resource.IsExact, "complete exact search evidence");
                AssertEqual(1, adapter.ExcelSearchCellCaptureCount, "one capture of the searched cell");
                var first = gateway.Read(session, new ResourceReadRequest { Reference = evidence.Resource, Representation = "text", MaxChars = 32000 }).Result;
                adapter.SetExcelCell("Data", "D1", "no matches");
                var negative = ExecuteHtmlNative(runtime, ExcelFindReplaceToolIds.FindCells, args);
                AssertEqual(0, (int)JObject.Parse(negative.Result.DataJson)["matchCount"], "zero-match search succeeds");
                AssertTrue(negative.ResourceEvidence.Single().Complete, "negative search still retains source");
                AssertEqual(EvidenceState.Superseded, new EvidenceStateReducer().Reduce(evidence,
                    executor.ResourceAuthority.CaptureMany(new[] { evidence.ScopeId })).State, "zero-match capture publishes drift");
                var captures = adapter.ExcelSearchCellCaptureCount;
                var next = gateway.Read(session, new ResourceReadRequest { Reference = evidence.Resource, Representation = "text", Cursor = first.NextCursor, MaxChars = 32000 }).Result;
                AssertTrue(next.Text.Contains("needle NEEDLE"), "historical continuation preserves captured formula/value text");
                AssertEqual(captures, adapter.ExcelSearchCellCaptureCount, "historical pages do not read Excel");
                System.IO.File.Delete(executor.Payloads.PathFor(evidence.Payload.Sha256));
                var denied = false;
                try { gateway.Read(session, new ResourceReadRequest { Reference = evidence.Resource, Representation = "text" }); }
                catch (ResourceRequestException error) { denied = error.ErrorCode == "RESOURCE_SNAPSHOT_UNAVAILABLE"; }
                AssertTrue(denied, "missing exact payload never falls forward");
            });
        }

        private static void ExcelSearchRejectsIncompleteCaptures()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var runtime = ExcelFindReplaceRuntime(executor, adapter);
                var invalid = ExecuteHtmlNative(runtime, ExcelFindReplaceToolIds.FindCells, new JObject { ["query"] = "(", ["mode"] = "regex" });
                AssertEqual(ToolExecutionOutcome.Error, invalid.Outcome, "invalid regex rejected before capture");
                var oversized = ExecuteHtmlNative(runtime, ExcelFindReplaceToolIds.FindCells, new JObject { ["query"] = "x", ["sheet"] = "Data", ["address"] = "A1:Z10000" });
                AssertEqual(ToolExecutionOutcome.Error, oversized.Outcome, "oversized range rejected");
                AssertEqual(0, adapter.ExcelSearchCellCaptureCount, "cell-count bound precedes materialization");
                var args = new JObject { ["query"] = "x", ["sheet"] = "Data", ["address"] = "A1" };
                adapter.ExcelSearchCellTransform = cell => { cell.Value = null; return cell; };
                AssertEqual(ToolExecutionOutcome.Error, ExecuteHtmlNative(runtime, ExcelFindReplaceToolIds.FindCells, args).Outcome, "null field is not a complete empty cell");
                adapter.ExcelSearchCellTransform = cell => { cell.Value = new string('x', ExcelFindReplaceService.MaximumSearchCharacters + 1); return cell; };
                AssertEqual(ToolExecutionOutcome.Error, ExecuteHtmlNative(runtime, ExcelFindReplaceToolIds.FindCells, args).Outcome, "oversized source rejected");
                adapter.ExcelSearchCellTransform = cell => { cell.Address = "A1"; return cell; };
                args["address"] = "A1:A2";
                AssertEqual(ToolExecutionOutcome.Error, ExecuteHtmlNative(runtime, ExcelFindReplaceToolIds.FindCells, args).Outcome, "duplicate captured cells rejected");
                adapter.ExcelSearchCellTransform = null;
                args["address"] = "J1";
                var empty = ExecuteHtmlNative(runtime, ExcelFindReplaceToolIds.FindCells, args);
                AssertTrue(empty.Outcome == ToolExecutionOutcome.Ok && empty.ResourceEvidence.Single().Complete, "blank cell is complete evidence");
            });
        }

        private static void ExcelFindReplaceUsesExactNativeOwnership()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var session = NewSession(adapter);
                    var tools = OfficeToolCatalog.ForHost(adapter.HostName)
                        .Concat(executor.GetControllerTools()).ToList();
                    var definitions = tools.Where(tool =>
                        ExcelFindReplaceToolIds.Owns(tool.Id)).ToArray();
                    var runtime = executor.CreateNativeRuntime(
                        session, definitions, new AppSettings(), "agent", false);
                    var findCall = new ToolCall(
                        "find-native",
                        ExcelFindReplaceToolIds.FindCells,
                        "{\"sheet\":\"Data\",\"address\":\"A1:B4\",\"query\":\"Jan\"}");
                    var findPolicy = runtime.Describe(findCall);
                    AssertTrue(findPolicy != null && !findPolicy.MayHaveSideEffects &&
                        findPolicy.Policy.IndependentLocalRead,
                        "find_cells has one exact native read policy");
                    var found = ExecuteNative(runtime, findCall, findPolicy);
                    AssertEqual(ToolExecutionOutcome.Ok, found.Outcome,
                        "typed find succeeds");
                    AssertEqual(1,
                        JObject.Parse(found.Result.DataJson)["matchCount"].Value<int>(),
                        "typed find preserves match count");
                    AssertEqual(1, adapter.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelFindScopeReadOperation),
                        "find reaches the direct backend once");

                    var replaceCall = new ToolCall(
                        "replace-native",
                        ExcelFindReplaceToolIds.ReplaceCells,
                        "{\"sheet\":\"Data\",\"address\":\"A2\",\"find\":\"Jan\",\"replace\":\"January\"}");
                    var replacePolicy = runtime.Describe(replaceCall);
                    AssertTrue(replacePolicy != null && replacePolicy.MayHaveSideEffects &&
                        replacePolicy.Policy.Verification == ToolVerification.Tool,
                        "replace_cells has one exact native verified-write policy");
                    var replaced = ExecuteNativeConfirmed(
                        runtime, replaceCall, replacePolicy);
                    AssertEqual(ToolExecutionOutcome.Ok, replaced.Outcome,
                        "typed replacement succeeds");
                    var evidence = found.ResourceEvidence.Single();
                    AssertTrue(new EvidenceStateReducer().Reduce(evidence,
                        executor.ResourceAuthority.CaptureMany(new[] { evidence.ScopeId })).State != EvidenceState.Current,
                        "replacement invalidates previous search evidence");
                    AssertEqual(ToolEffectEvidence.VerifiedChange,
                        replaced.Evidence.Effect,
                        "exact replacement read-back certifies the change");
                    AssertEqual("January", adapter.CellValue("Data", "A2"),
                        "direct backend changed the exact cell");
                    AssertEqual(1, adapter.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelReplaceApplyOperation),
                        "replacement reaches one direct apply backend");

                    var applies = adapter.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelReplaceApplyOperation);
                    var dryRun = executor.ExecuteManual(Command(
                        ExcelFindReplaceToolIds.ReplaceCells,
                        "sheet", "Data", "address", "A3",
                        "find", "Feb", "replace", "February"),
                        tools, new AppSettings(), true, true, session);
                    AssertTrue(dryRun.Success &&
                        adapter.CellValue("Data", "A3") == "Feb",
                        "native replacement dry-run stays non-mutating");
                    AssertEqual(applies, adapter.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelReplaceApplyOperation),
                        "dry-run never reaches the replacement backend");
                    AssertTrue(runtime.Describe(new ToolCall(
                        "wrong-case", "EXCEL.FIND_CELLS", "{}")) == null,
                        "native ownership has no case alias");
                });
        }

        private static void ExcelFindReplacePreservesPatternAndScopeSemantics()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    adapter.SetExcelCell("Data", "D1", "Code-12 code-345");
                    adapter.SetExcelFormula("Data", "D2", "=SUM(A2:B2)");
                    var session = NewSession(adapter);
                    var definitions = OfficeToolCatalog.ForHost(adapter.HostName).Where(tool =>
                        ExcelFindReplaceToolIds.Owns(tool.Id)).ToArray();
                    var runtime = executor.CreateNativeRuntime(
                        session, definitions, new AppSettings(), "agent", false);

                    var regexFind = new ToolCall(
                        "find-regex",
                        ExcelFindReplaceToolIds.FindCells,
                        "{\"sheet\":\"Data\",\"address\":\"D1\",\"query\":\"code-(\\\\d+)\",\"mode\":\"regex\",\"matchCase\":false}");
                    var found = ExecuteNative(
                        runtime, regexFind, runtime.Describe(regexFind));
                    var foundJson = JObject.Parse(found.Result.DataJson);
                    AssertEqual(2, foundJson["matchCount"].Value<int>(),
                        "regex search keeps all match counts");
                    AssertEqual("range", (string)foundJson["scope"],
                        "address still infers range scope");
                    AssertTrue(foundJson["scopeSha256"] == null && foundJson["contentSha256"] == null,
                        "find no longer exposes hash guards");

                    var formulaFind = new ToolCall(
                        "find-formula",
                        ExcelFindReplaceToolIds.FindCells,
                        "{\"sheet\":\"Data\",\"address\":\"D2\",\"query\":\"SUM\",\"lookIn\":\"formulas\"}");
                    var formula = ExecuteNative(
                        runtime, formulaFind, runtime.Describe(formulaFind));
                    AssertEqual("formula",
                        (string)JObject.Parse(formula.Result.DataJson)
                            .SelectToken("matches[0].field"),
                        "formula search preserves the selected field");

                    var regexReplace = new ToolCall(
                        "replace-regex",
                        ExcelFindReplaceToolIds.ReplaceCells,
                        "{\"sheet\":\"Data\",\"address\":\"D1\",\"find\":\"code-(\\\\d+)\",\"replace\":\"item-$1\",\"mode\":\"regex\",\"matchCase\":false}");
                    var replaced = ExecuteNativeConfirmed(
                        runtime, regexReplace, runtime.Describe(regexReplace));
                    AssertEqual("item-12 item-345",
                        adapter.CellValue("Data", "D1"),
                        "regex capture replacement is unchanged");
                    AssertEqual(2,
                        JObject.Parse(replaced.Result.DataJson)["replacements"].Value<int>(),
                        "replacement count remains match-based");

                    var formulaReplace = new ToolCall(
                        "replace-formula",
                        ExcelFindReplaceToolIds.ReplaceCells,
                        "{\"sheet\":\"Data\",\"address\":\"D2\",\"find\":\"SUM\",\"replace\":\"AVERAGE\",\"lookIn\":\"formulas\"}");
                    var formulaChanged = ExecuteNativeConfirmed(
                        runtime, formulaReplace, runtime.Describe(formulaReplace));
                    AssertEqual(ToolEffectEvidence.VerifiedChange,
                        formulaChanged.Evidence.Effect,
                        "formula replacement is verified as a formula write");
                    var formulaAfterCall = new ToolCall(
                        "find-formula-after",
                        ExcelFindReplaceToolIds.FindCells,
                        "{\"sheet\":\"Data\",\"address\":\"D2\",\"query\":\"AVERAGE\",\"lookIn\":\"formulas\"}");
                    var formulaAfter = ExecuteNative(
                        runtime, formulaAfterCall, runtime.Describe(formulaAfterCall));
                    AssertEqual(1,
                        JObject.Parse(formulaAfter.Result.DataJson)["matchCount"].Value<int>(),
                        "formula flag survives replacement");

                    adapter.SetExcelCell("Data", "I1", "hit");
                    adapter.SetExcelCell("Data", "I2", "hit");
                    var firstOnly = new ToolCall(
                        "replace-first-only",
                        ExcelFindReplaceToolIds.ReplaceCells,
                        "{\"sheet\":\"Data\",\"address\":\"I1:I2\",\"find\":\"hit\",\"replace\":\"done\",\"replaceAll\":false}");
                    ExecuteNativeConfirmed(runtime, firstOnly, runtime.Describe(firstOnly));
                    AssertEqual("done", adapter.CellValue("Data", "I1"),
                        "replaceAll=false changes the first matching cell");
                    AssertEqual("hit", adapter.CellValue("Data", "I2"),
                        "replaceAll=false leaves later cells unchanged");

                    adapter.SetExcelCell("Data", "E1", "same");
                    var noChangeCall = new ToolCall(
                        "replace-no-change",
                        ExcelFindReplaceToolIds.ReplaceCells,
                        "{\"sheet\":\"Data\",\"address\":\"E1\",\"find\":\"same\",\"replace\":\"same\"}");
                    var applies = adapter.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelReplaceApplyOperation);
                    var noChange = ExecuteNativeConfirmed(
                        runtime, noChangeCall, runtime.Describe(noChangeCall));
                    AssertEqual(ToolEffectEvidence.VerifiedNoChange,
                        noChange.Evidence.Effect,
                        "identical replacement is explicit no-change");
                    AssertEqual(ToolDispatchEvidence.NotDispatched,
                        noChange.Evidence.Dispatch,
                        "identical replacement skips host assignment");
                    AssertEqual(applies, adapter.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelReplaceApplyOperation),
                        "no-change never enters apply");

                    adapter.SetExcelCell("Data", "F1", "x x");
                    var limitedCall = new ToolCall(
                        "replace-limited",
                        ExcelFindReplaceToolIds.ReplaceCells,
                        "{\"sheet\":\"Data\",\"address\":\"F1\",\"find\":\"x\",\"replace\":\"y\",\"maxReplacements\":1}");
                    var limited = ExecuteNativeConfirmed(
                        runtime, limitedCall, runtime.Describe(limitedCall));
                    AssertEqual(ToolExecutionOutcome.Error, limited.Outcome,
                        "replacement ceiling rejects before mutation");
                    AssertEqual("replacement_limit_exceeded",
                        (string)JObject.Parse(limited.Result.DataJson)["code"],
                        "replacement ceiling keeps its exact code");
                    AssertEqual("x x", adapter.CellValue("Data", "F1"),
                        "limit failure does not mutate the cell");
                });
        }

        private static void ExcelFindReplaceClassifiesDispatchFaults()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    adapter.SetExcelCell("Data", "G1", "before");
                    var runtime = ExcelFindReplaceRuntime(executor, adapter);
                    adapter.BeforeExcelBackendCall = operation =>
                    {
                        if (operation == FakeOfficeAdapter.ExcelReplaceApplyOperation)
                            adapter.SetExcelCell("Data", "G1", "concurrent");
                    };
                    var call = ReplaceCall("replace-stale", "G1", "before", "after");
                    var stale = ExecuteNativeConfirmed(runtime, call, runtime.Describe(call));
                    AssertEqual(ToolExecutionOutcome.Error, stale.Outcome,
                        "changed pre-dispatch target is a definite error");
                    AssertEqual(ToolDispatchEvidence.NotDispatched,
                        stale.Evidence.Dispatch,
                        "stale target never crosses the effect boundary");
                    AssertEqual("excel_replace_target_changed",
                        (string)JObject.Parse(stale.Result.DataJson)["code"],
                        "stale target keeps its exact diagnostic");
                });

            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    adapter.SetExcelCell("Data", "G2", "before");
                    adapter.ExcelReplaceThrowAfterMutation = true;
                    var runtime = ExcelFindReplaceRuntime(executor, adapter);
                    var call = ReplaceCall("replace-throw", "G2", "before", "after");
                    var unknown = ExecuteNativeConfirmed(runtime, call, runtime.Describe(call));
                    AssertEqual(ToolExecutionOutcome.Unknown, unknown.Outcome,
                        "mutate-then-throw is unknown");
                    AssertEqual(ToolEffectEvidence.Unknown, unknown.Evidence.Effect,
                        "mutate-then-throw preserves unknown effect evidence");
                    AssertEqual("after", adapter.CellValue("Data", "G2"),
                        "unknown does not imply that mutation failed");
                });

            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    adapter.SetExcelCell("Data", "G3", "before");
                    var runtime = ExcelFindReplaceRuntime(executor, adapter);
                    var reads = 0;
                    adapter.BeforeExcelBackendCall = operation =>
                    {
                        if (operation == FakeOfficeAdapter.ExcelFindScopeReadOperation &&
                            ++reads == 2)
                            adapter.SetExcelCell("Data", "G3", "diverged");
                    };
                    var call = ReplaceCall("replace-diverged", "G3", "before", "after");
                    var unknown = ExecuteNativeConfirmed(runtime, call, runtime.Describe(call));
                    AssertEqual(ToolExecutionOutcome.Unknown, unknown.Outcome,
                        "divergent read-back is unknown");
                    AssertEqual("excel_replace_verification_failed",
                        (string)JObject.Parse(unknown.Result.DataJson)["code"],
                        "divergent read-back keeps the precise code");
                });
        }

        private static void ExcelFindReplaceUsesBoundDocumentScope()
        {
            WithTempPaths(paths =>
            {
                using (var dispatcher = new OfficeStaDispatcher())
                {
                    var document = new BoundTestDocument
                    {
                        StableId = "bound-excel-find-replace",
                        IsAlive = true
                    };
                    var sessionPort = new BoundTestOfficeSession(
                        dispatcher, document, "bound-runtime-find-replace", new object());
                    var inner = FakeOfficeAdapter.ForHost("Excel");
                    inner.SetExcelCell("Data", "H1", "before");
                    var host = new BoundTestOfficeAdapter(sessionPort, inner);
                    var ownerSta = false;
                    var searchOwnerSta = false;
                    host.BeforeRead = operation =>
                    {
                        if (operation == FakeOfficeAdapter.ExcelFindScopeReadOperation) searchOwnerSta = dispatcher.CheckAccess;
                        if (operation == FakeOfficeAdapter.ExcelReplaceApplyOperation)
                            ownerSta = dispatcher.CheckAccess;
                    };
                    var executor = new OfficeToolExecutor(host,
                        new VbaJournalStore(paths), new SkillStore(paths),
                        new ToolStore(paths), paths: paths);
                    var chat = new ChatSession
                    {
                        Host = "Excel",
                        DocumentKey = "bound-excel-find-replace",
                        DocumentTitle = "Bound.xlsx"
                    };
                    var tools = OfficeToolCatalog.ForHost(host.HostName)
                        .Concat(executor.GetControllerTools()).ToList();
                    var search = executor.ExecuteManual(Command(ExcelFindReplaceToolIds.FindCells, "query", "before", "sheet", "Data", "address", "H1"),
                        tools, new AppSettings(), false, false, chat);
                    AssertTrue(search.Success && searchOwnerSta, "search capture uses bound workbook STA");
                    var searchReads = inner.ExcelSearchCellCaptureCount;
                    var result = executor.ExecuteManual(Command(
                        ExcelFindReplaceToolIds.ReplaceCells,
                        "sheet", "Data", "address", "H1",
                        "find", "before", "replace", "after"),
                        tools, new AppSettings(), false, true, chat);
                    AssertTrue(result.Success && ownerSta,
                        "replacement stays on the bound document owner STA");

                    var dispatched = inner.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelReplaceApplyOperation);
                    dispatcher.Invoke(() => document.IsAlive = false);
                    var closedSearch = executor.ExecuteManual(Command(ExcelFindReplaceToolIds.FindCells, "query", "after", "sheet", "Data", "address", "H1"),
                        tools, new AppSettings(), false, false, chat);
                    AssertTrue(!closedSearch.Success, "closed workbook refuses search capture");
                    AssertEqual(searchReads, inner.ExcelSearchCellCaptureCount, "closed search never reads cells");
                    var closed = executor.ExecuteManual(Command(
                        ExcelFindReplaceToolIds.ReplaceCells,
                        "sheet", "Data", "address", "H1",
                        "find", "after", "replace", "blocked"),
                        tools, new AppSettings(), false, true, chat);
                    AssertEqual("active_document_changed", closed.ErrorCode,
                        "closed bound workbook is rejected before replacement");
                    AssertEqual(dispatched, inner.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelReplaceApplyOperation),
                        "closed workbook never reaches replacement apply");
                }
            });
        }

        private static NativeToolRuntimeAdapter ExcelFindReplaceRuntime(
            OfficeToolExecutor executor,
            FakeOfficeAdapter adapter)
        {
            return executor.CreateNativeRuntime(
                NewSession(adapter),
                OfficeToolCatalog.ForHost(adapter.HostName).Where(tool =>
                    ExcelFindReplaceToolIds.Owns(tool.Id)),
                new AppSettings(), "agent", false);
        }

        private static ToolCall ReplaceCall(
            string id, string address, string find, string replacement)
        {
            return new ToolCall(id, ExcelFindReplaceToolIds.ReplaceCells,
                new JObject
                {
                    ["sheet"] = "Data",
                    ["address"] = address,
                    ["find"] = find,
                    ["replace"] = replacement
                }.ToString(Newtonsoft.Json.Formatting.None));
        }

        private static ToolExecutionRecord ExecuteNativeConfirmed(
            NativeToolRuntimeAdapter runtime,
            ToolCall call,
            ToolPolicySnapshot policy)
        {
            return runtime.ExecuteAsync(new ToolExecutionContext(
                call, policy, "run", "turn", call.Id,
                DateTime.UtcNow, true, 1), CancellationToken.None)
                .GetAwaiter().GetResult();
        }
    }
}
