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
        private static void ExcelTableUsesExactNativeOwnership()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var session = NewSession(adapter);
                    var tools = adapter.GetBuiltInTools()
                        .Concat(executor.GetControllerTools()).ToList();
                    var runtime = executor.CreateNativeRuntime(
                        session,
                        tools.Where(tool => ExcelTableToolIds.Owns(tool.Id)),
                        new AppSettings(), "agent", false);
                    var call = new ToolCall(
                        "table-add", ExcelTableToolIds.AddTable,
                        "{\"sheet\":\"Data\",\"sourceRange\":\"A1:B4\",\"name\":\"SalesTable\",\"hasHeaders\":true,\"style\":\"TableStyleMedium2\"}");
                    var policy = runtime.Describe(call);
                    AssertTrue(policy != null && policy.MayHaveSideEffects &&
                        policy.Policy.Verification == ToolVerification.Tool,
                        "add_table has one exact native verified policy");
                    var added = ExecuteNativeConfirmed(runtime, call, policy);
                    AssertEqual(ToolExecutionOutcome.Ok, added.Outcome,
                        "typed table add succeeds");
                    AssertEqual(ToolEffectEvidence.VerifiedChange,
                        added.Evidence.Effect,
                        "table read-back certifies the change");
                    var table = adapter.ExcelTableForTest("Data", "SalesTable");
                    AssertTrue(table != null && table.Range == "A1:B4" &&
                        table.HasHeaders && table.Style == "TableStyleMedium2",
                        "direct backend stores the exact table contract");
                    AssertEqual(0, adapter.Executed.Count(command =>
                        ExcelTableToolIds.Owns(command.ToolId)),
                        "table public id never reaches generic host dispatch");

                    var applyCalls = adapter.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelTableAddOperation);
                    var dryRun = executor.Execute(
                        Command(ExcelTableToolIds.AddTable,
                            "sheet", "Data", "sourceRange", "D1:E2",
                            "name", "DryTable"),
                        tools, new AppSettings(), true, true, session);
                    AssertTrue(dryRun.Success &&
                        adapter.ExcelTableForTest("Data", "DryTable") == null,
                        "native table dry-run stays non-mutating");
                    AssertEqual(applyCalls,
                        adapter.ExcelBackendCalls.Count(operation =>
                            operation == FakeOfficeAdapter.ExcelTableAddOperation),
                        "dry-run never enters table add");
                    AssertTrue(runtime.Describe(new ToolCall(
                        "table-case", "EXCEL.ADD_TABLE", "{}")) == null,
                        "native table ownership has no case alias");
                });
        }

        private static void ExcelTablePreservesCreationSemantics()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var runtime = ExcelTableRuntime(executor, adapter);
                    var defaults = new ToolCall(
                        "table-default", ExcelTableToolIds.AddTable, "{}");
                    var added = ExecuteNativeConfirmed(
                        runtime, defaults, runtime.Describe(defaults));
                    AssertEqual(ToolExecutionOutcome.Ok, added.Outcome,
                        "default table creation succeeds");
                    var generated = adapter.ExcelTableForTest("Data", "Table1");
                    AssertTrue(generated != null &&
                        generated.Range == "A1:B2" && generated.HasHeaders,
                        "default range, generated name, and headers are preserved");

                    var addCalls = adapter.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelTableAddOperation);
                    var duplicate = new ToolCall(
                        "table-duplicate", ExcelTableToolIds.AddTable,
                        "{\"sheet\":\"Data\",\"sourceRange\":\"D1:E2\",\"name\":\"table1\"}");
                    var duplicateResult = ExecuteNativeConfirmed(
                        runtime, duplicate, runtime.Describe(duplicate));
                    AssertEqual(ToolExecutionOutcome.Error,
                        duplicateResult.Outcome,
                        "case-insensitive table-name collision is rejected");
                    AssertEqual(ToolDispatchEvidence.NotDispatched,
                        duplicateResult.Evidence.Dispatch,
                        "name collision is rejected before effect");
                    AssertEqual(addCalls,
                        adapter.ExcelBackendCalls.Count(operation =>
                            operation == FakeOfficeAdapter.ExcelTableAddOperation),
                        "name collision skips backend add");
                    AssertEqual("excel_table_already_exists",
                        (string)JObject.Parse(
                            duplicateResult.Result.DataJson)["code"],
                        "collision keeps its exact diagnostic");

                    var oversized = new ToolCall(
                        "table-bound", ExcelTableToolIds.AddTable,
                        "{\"sheet\":\"Data\",\"sourceRange\":\"A1:A100001\"}");
                    var oversizedResult = ExecuteNativeConfirmed(
                        runtime, oversized, runtime.Describe(oversized));
                    AssertEqual(ToolExecutionOutcome.Error,
                        oversizedResult.Outcome,
                        "oversized table source is a definite error");
                    AssertEqual(ToolDispatchEvidence.NotDispatched,
                        oversizedResult.Evidence.Dispatch,
                        "table cell ceiling applies before dispatch");
                    AssertEqual("excel_table_too_large",
                        (string)JObject.Parse(
                            oversizedResult.Result.DataJson)["code"],
                        "table bound keeps its exact diagnostic");
                });
        }

        private static void ExcelTableClassifiesDispatchFaults()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var runtime = ExcelTableRuntime(executor, adapter);
                    adapter.BeforeExcelBackendCall = operation =>
                    {
                        if (operation == FakeOfficeAdapter.ExcelTableAddOperation)
                            adapter.SetExcelCellForTest("Data", "A1", "changed");
                    };
                    var call = new ToolCall(
                        "table-stale", ExcelTableToolIds.AddTable,
                        "{\"sheet\":\"Data\",\"sourceRange\":\"A1:B4\",\"name\":\"StaleTable\"}");
                    var stale = ExecuteNativeConfirmed(
                        runtime, call, runtime.Describe(call));
                    AssertEqual(ToolExecutionOutcome.Error, stale.Outcome,
                        "changed table source is a definite error");
                    AssertEqual(ToolDispatchEvidence.NotDispatched,
                        stale.Evidence.Dispatch,
                        "stale source never crosses the effect boundary");
                    AssertEqual("excel_table_target_changed",
                        (string)JObject.Parse(stale.Result.DataJson)["code"],
                        "stale source keeps its exact diagnostic");
                });

            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    adapter.ExcelTableThrowAfterMutation = true;
                    var runtime = ExcelTableRuntime(executor, adapter);
                    var call = new ToolCall(
                        "table-throw", ExcelTableToolIds.AddTable,
                        "{\"sheet\":\"Data\",\"sourceRange\":\"A1:B4\",\"name\":\"MaybeTable\"}");
                    var unknown = ExecuteNativeConfirmed(
                        runtime, call, runtime.Describe(call));
                    AssertEqual(ToolExecutionOutcome.Unknown, unknown.Outcome,
                        "add-then-throw is unknown");
                    AssertEqual(ToolEffectEvidence.Unknown,
                        unknown.Evidence.Effect,
                        "post-dispatch table failure preserves unknown effect");
                    AssertTrue(adapter.ExcelTableForTest(
                        "Data", "MaybeTable") != null,
                        "unknown does not imply that table creation failed");
                });

            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var runtime = ExcelTableRuntime(executor, adapter);
                    var reads = 0;
                    adapter.ExcelTableReadTransform = snapshot =>
                    {
                        reads++;
                        if (reads < 2) return snapshot;
                        return new ExcelTableCollectionSnapshot
                        {
                            Sheet = snapshot.Sheet,
                            SourceRange = snapshot.SourceRange,
                            Rows = snapshot.Rows,
                            Columns = snapshot.Columns,
                            CellCount = snapshot.CellCount,
                            StateToken = snapshot.StateToken,
                            Tables = snapshot.Tables.Where(table =>
                                !string.Equals(table.Name, "DivergedTable",
                                    StringComparison.Ordinal)).ToArray()
                        };
                    };
                    var call = new ToolCall(
                        "table-diverged", ExcelTableToolIds.AddTable,
                        "{\"sheet\":\"Data\",\"sourceRange\":\"A1:B4\",\"name\":\"DivergedTable\"}");
                    var unknown = ExecuteNativeConfirmed(
                        runtime, call, runtime.Describe(call));
                    AssertEqual(ToolExecutionOutcome.Unknown, unknown.Outcome,
                        "divergent table read-back is unknown");
                    AssertEqual("excel_table_verification_failed",
                        (string)JObject.Parse(unknown.Result.DataJson)["code"],
                        "divergent read-back keeps the precise code");
                });
        }

        private static void ExcelTableUsesBoundDocumentScope()
        {
            WithTempPaths(paths =>
            {
                using (var dispatcher = new OfficeStaDispatcher())
                {
                    var document = new BoundTestDocument
                    {
                        StableId = "bound-excel-table",
                        IsAlive = true
                    };
                    var sessionPort = new BoundTestOfficeSession(
                        dispatcher, document, "bound-runtime-table", new object());
                    var inner = FakeOfficeAdapter.ForHost("Excel");
                    var host = new BoundTestOfficeAdapter(sessionPort, inner);
                    var ownerSta = false;
                    host.BeforeRead = operation =>
                    {
                        if (operation == FakeOfficeAdapter.ExcelTableAddOperation)
                            ownerSta = dispatcher.CheckAccess;
                    };
                    var executor = new OfficeToolExecutor(host,
                        new VbaJournalStore(paths), new SkillStore(paths),
                        new ToolStore(paths), paths: paths);
                    var chat = new ChatSession
                    {
                        Host = "Excel",
                        DocumentKey = "bound-excel-table",
                        DocumentTitle = "Bound.xlsx"
                    };
                    var tools = host.GetBuiltInTools()
                        .Concat(executor.GetControllerTools()).ToList();
                    var result = executor.Execute(Command(
                        ExcelTableToolIds.AddTable,
                        "sheet", "Data", "sourceRange", "A1:B4",
                        "name", "BoundTable"),
                        tools, new AppSettings(), false, true, chat);
                    AssertTrue(result.Success && ownerSta,
                        "table creation stays on the bound document owner STA");

                    var dispatched = inner.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelTableAddOperation);
                    dispatcher.Invoke(() => document.IsAlive = false);
                    var closed = executor.Execute(Command(
                        ExcelTableToolIds.AddTable,
                        "sheet", "Data", "sourceRange", "D1:E2",
                        "name", "BlockedTable"),
                        tools, new AppSettings(), false, true, chat);
                    AssertEqual("active_document_changed", closed.ErrorCode,
                        "closed bound workbook is rejected before table mutation");
                    AssertEqual(dispatched,
                        inner.ExcelBackendCalls.Count(operation =>
                            operation == FakeOfficeAdapter.ExcelTableAddOperation),
                        "closed workbook never reaches table add");
                }
            });
        }

        private static NativeToolRuntimeAdapter ExcelTableRuntime(
            OfficeToolExecutor executor,
            FakeOfficeAdapter adapter)
        {
            return executor.CreateNativeRuntime(
                NewSession(adapter),
                adapter.GetBuiltInTools().Where(tool =>
                    ExcelTableToolIds.Owns(tool.Id)),
                new AppSettings(), "agent", false);
        }
    }
}
