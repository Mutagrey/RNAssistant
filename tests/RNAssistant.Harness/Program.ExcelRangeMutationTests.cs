using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ExcelRangeMutationUsesExactNativeOwnership()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var session = NewSession(adapter);
                    var tools = adapter.GetBuiltInTools()
                        .Concat(executor.GetControllerTools()).ToList();
                    var runtime = executor.CreateNativeRuntime(
                        session,
                        tools.Where(tool =>
                            ExcelRangeMutationToolIds.Owns(tool.Id)),
                        new AppSettings(), "agent", false);

                    foreach (var toolId in new[]
                    {
                        ExcelRangeMutationToolIds.FormatRange,
                        ExcelRangeMutationToolIds.ClearRange,
                        ExcelRangeMutationToolIds.SortRange,
                        ExcelRangeMutationToolIds.FilterRange
                    })
                    {
                        var policy = runtime.Describe(new ToolCall(
                            "range-policy-" + toolId, toolId,
                            RangeMutationArguments(toolId)));
                        AssertTrue(policy != null && policy.MayHaveSideEffects &&
                            policy.Policy.Verification == ToolVerification.Tool,
                            toolId + " has one exact native verified policy");
                    }

                    var format = new ToolCall(
                        "range-format",
                        ExcelRangeMutationToolIds.FormatRange,
                        "{\"sheet\":\"Data\",\"address\":\"A1:B1\",\"bold\":true}");
                    var formatted = ExecuteNativeConfirmed(
                        runtime, format, runtime.Describe(format));
                    AssertEqual(ToolExecutionOutcome.Ok, formatted.Outcome,
                        "typed format succeeds");
                    AssertEqual(ToolEffectEvidence.VerifiedChange,
                        formatted.Evidence.Effect,
                        "format read-back certifies the change");
                    AssertTrue(adapter.HasExcelRangeFormat("Data", "A1:B1"),
                        "direct backend stores the requested format state");
                    AssertEqual(0, adapter.Executed.Count(command =>
                        ExcelRangeMutationToolIds.Owns(command.ToolId)),
                        "range mutation ids never reach generic host dispatch");

                    var calls = adapter.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelRangeMutationApplyOperation);
                    var dryRun = executor.Execute(
                        Command(ExcelRangeMutationToolIds.ClearRange,
                            "sheet", "Data", "address", "A2:B2"),
                        tools, new AppSettings(), true, true, session);
                    AssertTrue(dryRun.Success &&
                        !string.IsNullOrWhiteSpace(adapter.ExcelCellText("Data", "A2")),
                        "native range dry-run stays non-mutating");
                    AssertEqual(calls, adapter.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelRangeMutationApplyOperation),
                        "dry-run never enters range apply");
                    AssertTrue(runtime.Describe(new ToolCall(
                        "range-case", "EXCEL.CLEAR_RANGE", "{}")) == null,
                        "native range ownership has no case alias");
                });
        }

        private static void ExcelRangeMutationPreservesFamilySemantics()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var runtime = ExcelRangeMutationRuntime(executor, adapter);
                    var format = new ToolCall(
                        "range-format-state",
                        ExcelRangeMutationToolIds.FormatRange,
                        "{\"sheet\":\"Data\",\"address\":\"A2:B2\",\"italic\":true}");
                    ExecuteNativeConfirmed(runtime, format, runtime.Describe(format));

                    var clearValues = new ToolCall(
                        "range-clear-values",
                        ExcelRangeMutationToolIds.ClearRange,
                        "{\"sheet\":\"Data\",\"address\":\"A2:B2\"}");
                    var cleared = ExecuteNativeConfirmed(
                        runtime, clearValues, runtime.Describe(clearValues));
                    AssertEqual(ToolEffectEvidence.VerifiedChange,
                        cleared.Evidence.Effect,
                        "default clear removes values with read-back");
                    AssertEqual(string.Empty, adapter.ExcelCellText("Data", "A2"),
                        "default clearWhat remains values");
                    AssertTrue(adapter.HasExcelRangeFormat("Data", "A2:B2"),
                        "values clear preserves formatting");

                    var clearFormats = new ToolCall(
                        "range-clear-formats",
                        ExcelRangeMutationToolIds.ClearRange,
                        "{\"sheet\":\"Data\",\"address\":\"A2:B2\",\"clearWhat\":\"formats\"}");
                    ExecuteNativeConfirmed(
                        runtime, clearFormats, runtime.Describe(clearFormats));
                    AssertTrue(!adapter.HasExcelRangeFormat("Data", "A2:B2"),
                        "format clear preserves the separate values contract");

                    adapter.SetExcelCellForTest("Data", "A2", "Jan");
                    adapter.SetExcelCellForTest("Data", "B2", "120");
                    var sort = new ToolCall(
                        "range-sort",
                        ExcelRangeMutationToolIds.SortRange,
                        "{\"sheet\":\"Data\",\"address\":\"A1:B4\",\"keyColumn\":2,\"descending\":true,\"hasHeaders\":true}");
                    var sorted = ExecuteNativeConfirmed(
                        runtime, sort, runtime.Describe(sort));
                    AssertEqual(ToolEffectEvidence.VerifiedChange,
                        sorted.Evidence.Effect,
                        "sort order is verified from exact range read-back");
                    AssertEqual("Mar", adapter.ExcelCellText("Data", "A2"),
                        "descending sort reorders whole rows");
                    AssertEqual("Jan", adapter.ExcelCellText("Data", "A4"),
                        "descending sort preserves the lowest row last");

                    var applyCalls = adapter.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelRangeMutationApplyOperation);
                    var sortedAgain = ExecuteNativeConfirmed(
                        runtime, sort, runtime.Describe(sort));
                    AssertEqual(ToolEffectEvidence.VerifiedNoChange,
                        sortedAgain.Evidence.Effect,
                        "already sorted range is explicit no-change");
                    AssertEqual(applyCalls, adapter.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelRangeMutationApplyOperation),
                        "verified no-change skips sort dispatch");

                    var filter = new ToolCall(
                        "range-filter",
                        ExcelRangeMutationToolIds.FilterRange,
                        "{\"sheet\":\"Data\",\"address\":\"A1:B4\",\"field\":1,\"criteria\":\"Mar\"}");
                    var filtered = ExecuteNativeConfirmed(
                        runtime, filter, runtime.Describe(filter));
                    AssertEqual(ToolEffectEvidence.VerifiedChange,
                        filtered.Evidence.Effect,
                        "filter state is verified from the exact target");

                    var invalid = new ToolCall(
                        "range-invalid-key",
                        ExcelRangeMutationToolIds.SortRange,
                        "{\"sheet\":\"Data\",\"address\":\"A1:B4\",\"keyColumn\":3}");
                    var invalidResult = ExecuteNativeConfirmed(
                        runtime, invalid, runtime.Describe(invalid));
                    AssertEqual(ToolExecutionOutcome.Error, invalidResult.Outcome,
                        "out-of-range selector is a definite error");
                    AssertEqual(ToolDispatchEvidence.NotDispatched,
                        invalidResult.Evidence.Dispatch,
                        "selector validation happens before dispatch");
                    AssertEqual("excel_sort_key_out_of_range",
                        (string)JObject.Parse(invalidResult.Result.DataJson)["code"],
                        "selector failure keeps its exact diagnostic");

                    var oversizedAutoFit = new ToolCall(
                        "range-autofit-bound",
                        ExcelRangeMutationToolIds.FormatRange,
                        "{\"sheet\":\"Data\",\"address\":\"A1:A10001\",\"autoFit\":\"rows\"}");
                    var oversizedResult = ExecuteNativeConfirmed(
                        runtime, oversizedAutoFit,
                        runtime.Describe(oversizedAutoFit));
                    AssertEqual(ToolExecutionOutcome.Error,
                        oversizedResult.Outcome,
                        "autofit dimension ceiling rejects before dispatch");
                    AssertEqual("excel_range_autofit_too_large",
                        (string)JObject.Parse(
                            oversizedResult.Result.DataJson)["code"],
                        "autofit bound keeps its exact diagnostic");
                });
        }

        private static void ExcelRangeMutationClassifiesDispatchFaults()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var runtime = ExcelRangeMutationRuntime(executor, adapter);
                    adapter.BeforeExcelBackendCall = operation =>
                    {
                        if (operation ==
                            FakeOfficeAdapter.ExcelRangeMutationApplyOperation)
                            adapter.SetExcelCellForTest("Data", "A2", "Concurrent");
                    };
                    var call = new ToolCall(
                        "range-stale",
                        ExcelRangeMutationToolIds.ClearRange,
                        "{\"sheet\":\"Data\",\"address\":\"A2:B2\"}");
                    var stale = ExecuteNativeConfirmed(
                        runtime, call, runtime.Describe(call));
                    AssertEqual(ToolExecutionOutcome.Error, stale.Outcome,
                        "changed range state is a definite error");
                    AssertEqual(ToolDispatchEvidence.NotDispatched,
                        stale.Evidence.Dispatch,
                        "stale range never crosses the effect boundary");
                    AssertEqual("excel_range_target_changed",
                        (string)JObject.Parse(stale.Result.DataJson)["code"],
                        "stale range keeps its exact diagnostic");
                });

            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    adapter.ExcelRangeMutationThrowAfterMutation = true;
                    var runtime = ExcelRangeMutationRuntime(executor, adapter);
                    var call = new ToolCall(
                        "range-throw",
                        ExcelRangeMutationToolIds.ClearRange,
                        "{\"sheet\":\"Data\",\"address\":\"A2:B2\"}");
                    var unknown = ExecuteNativeConfirmed(
                        runtime, call, runtime.Describe(call));
                    AssertEqual(ToolExecutionOutcome.Unknown, unknown.Outcome,
                        "mutate-then-throw is unknown");
                    AssertEqual(ToolEffectEvidence.Unknown,
                        unknown.Evidence.Effect,
                        "post-dispatch failure preserves unknown effect");
                    AssertEqual(string.Empty, adapter.ExcelCellText("Data", "A2"),
                        "unknown does not imply that clear failed");
                });

            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var runtime = ExcelRangeMutationRuntime(executor, adapter);
                    var reads = 0;
                    adapter.ExcelRangeMutationReadTransform = snapshot =>
                    {
                        reads++;
                        if (reads < 2) return snapshot;
                        return new ExcelRangeMutationSnapshot
                        {
                            Kind = snapshot.Kind,
                            Sheet = snapshot.Sheet,
                            Address = snapshot.Address,
                            Rows = snapshot.Rows,
                            Columns = snapshot.Columns,
                            CellCount = snapshot.CellCount,
                            StateToken = snapshot.StateToken,
                            Satisfied = false
                        };
                    };
                    var call = new ToolCall(
                        "range-diverged",
                        ExcelRangeMutationToolIds.FormatRange,
                        "{\"sheet\":\"Data\",\"address\":\"A1\",\"bold\":true}");
                    var unknown = ExecuteNativeConfirmed(
                        runtime, call, runtime.Describe(call));
                    AssertEqual(ToolExecutionOutcome.Unknown, unknown.Outcome,
                        "divergent range read-back is unknown");
                    AssertEqual("excel_range_mutation_verification_failed",
                        (string)JObject.Parse(unknown.Result.DataJson)["code"],
                        "divergent read-back keeps the precise code");
                });
        }

        private static void ExcelRangeMutationUsesBoundDocumentScope()
        {
            WithTempPaths(paths =>
            {
                using (var dispatcher = new OfficeStaDispatcher())
                {
                    var document = new BoundTestDocument
                    {
                        StableId = "bound-excel-range",
                        IsAlive = true
                    };
                    var sessionPort = new BoundTestOfficeSession(
                        dispatcher, document, "bound-runtime-range", new object());
                    var inner = FakeOfficeAdapter.ForHost("Excel");
                    var host = new BoundTestOfficeAdapter(sessionPort, inner);
                    var ownerSta = false;
                    host.BeforeRead = operation =>
                    {
                        if (operation ==
                            FakeOfficeAdapter.ExcelRangeMutationApplyOperation)
                            ownerSta = dispatcher.CheckAccess;
                    };
                    var executor = new OfficeToolExecutor(host,
                        new VbaJournalStore(paths), new SkillStore(paths),
                        new ToolStore(paths), paths: paths);
                    var chat = new ChatSession
                    {
                        Host = "Excel",
                        DocumentKey = "bound-excel-range",
                        DocumentTitle = "Bound.xlsx"
                    };
                    var tools = host.GetBuiltInTools()
                        .Concat(executor.GetControllerTools()).ToList();
                    var result = executor.Execute(Command(
                        ExcelRangeMutationToolIds.ClearRange,
                        "sheet", "Data", "address", "A2:B2"),
                        tools, new AppSettings(), false, true, chat);
                    AssertTrue(result.Success && ownerSta,
                        "range mutation stays on the bound document owner STA");

                    var dispatched = inner.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelRangeMutationApplyOperation);
                    dispatcher.Invoke(() => document.IsAlive = false);
                    var closed = executor.Execute(Command(
                        ExcelRangeMutationToolIds.ClearRange,
                        "sheet", "Data", "address", "A3:B3"),
                        tools, new AppSettings(), false, true, chat);
                    AssertEqual("active_document_changed", closed.ErrorCode,
                        "closed bound workbook is rejected before range mutation");
                    AssertEqual(dispatched, inner.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelRangeMutationApplyOperation),
                        "closed workbook never reaches range apply");
                }
            });
        }

        private static NativeToolRuntimeAdapter ExcelRangeMutationRuntime(
            OfficeToolExecutor executor,
            FakeOfficeAdapter adapter)
        {
            return executor.CreateNativeRuntime(
                NewSession(adapter),
                adapter.GetBuiltInTools().Where(tool =>
                    ExcelRangeMutationToolIds.Owns(tool.Id)),
                new AppSettings(), "agent", false);
        }

        private static string RangeMutationArguments(string toolId)
        {
            if (toolId == ExcelRangeMutationToolIds.FormatRange)
                return "{\"bold\":true}";
            return toolId == ExcelRangeMutationToolIds.ClearRange
                ? "{\"address\":\"A1\"}"
                : toolId == ExcelRangeMutationToolIds.SortRange
                    ? "{\"address\":\"A1:B2\"}"
                    : "{\"address\":\"A1:B2\"}";
        }
    }
}
