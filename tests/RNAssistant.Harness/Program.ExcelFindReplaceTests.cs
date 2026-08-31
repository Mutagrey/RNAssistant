using System;
using System.Linq;
using System.Threading;
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
        private static void ExcelFindReplaceUsesExactNativeOwnership()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var session = NewSession(adapter);
                    var tools = adapter.GetBuiltInTools()
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
                    AssertEqual(0, adapter.Executed.Count(command =>
                        command.ToolId == ExcelFindReplaceToolIds.FindCells),
                        "find public id never reaches generic host dispatch");

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
                    AssertEqual(ToolEffectEvidence.VerifiedChange,
                        replaced.Evidence.Effect,
                        "exact replacement read-back certifies the change");
                    AssertEqual("January", adapter.CellValue("Data", "A2"),
                        "direct backend changed the exact cell");
                    AssertEqual(1, adapter.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelReplaceApplyOperation),
                        "replacement reaches one direct apply backend");
                    AssertEqual(0, adapter.Executed.Count(command =>
                        command.ToolId == ExcelFindReplaceToolIds.ReplaceCells),
                        "replace public id never reaches generic host dispatch");

                    var applies = adapter.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelReplaceApplyOperation);
                    var dryRun = executor.Execute(Command(
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
                    var definitions = adapter.GetBuiltInTools().Where(tool =>
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
                    AssertEqual((string)foundJson["scopeSha256"],
                        (string)foundJson["contentSha256"],
                        "find keeps the stable scope/content hash alias");

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
                    host.BeforeRead = operation =>
                    {
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
                    var tools = host.GetBuiltInTools()
                        .Concat(executor.GetControllerTools()).ToList();
                    var result = executor.Execute(Command(
                        ExcelFindReplaceToolIds.ReplaceCells,
                        "sheet", "Data", "address", "H1",
                        "find", "before", "replace", "after"),
                        tools, new AppSettings(), false, true, chat);
                    AssertTrue(result.Success && ownerSta,
                        "replacement stays on the bound document owner STA");

                    var dispatched = inner.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelReplaceApplyOperation);
                    dispatcher.Invoke(() => document.IsAlive = false);
                    var closed = executor.Execute(Command(
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
                adapter.GetBuiltInTools().Where(tool =>
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
