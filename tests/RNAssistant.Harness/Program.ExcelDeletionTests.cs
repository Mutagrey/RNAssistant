using System;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static NativeToolRuntimeAdapter DeletionRuntime(OfficeToolExecutor executor, FakeOfficeAdapter adapter,
            bool autoConfirm = true, Func<ToolExecutionContext, ToolPreparationResult, string> registrar = null)
        {
            var policy = ConversationRunPolicy.For("agent", autoConfirmToolActions: autoConfirm);
            var session = NewSession(adapter);
            session.LastRun = new ChatRunRecord { RunId = "run", TurnId = "turn", ConversationPolicy = policy };
            return executor.CreateNativeRuntime(session, OfficeToolCatalog.ForHost(adapter.HostName)
                .Where(tool => ExcelRangeMutationToolIds.Owns(tool.Id)),
                new AppSettings { AutoConfirmToolActions = autoConfirm }, "agent", false, registrar, runPolicy: policy);
        }

        private static ToolExecutionRecord ExecuteDeletion(NativeToolRuntimeAdapter runtime, ToolCall call, string prepared = null)
        {
            return runtime.ExecuteAsync(new ToolExecutionContext(call, runtime.Describe(call), "run", "turn", call.Id,
                DateTime.UtcNow, prepared != null, 1, preparedStateJson: prepared), CancellationToken.None).GetAwaiter().GetResult();
        }

        private static ToolCall DeletionCall(string scope, string axis, int start, int count, string table = null, string sheet = "Data")
        {
            var args = new JObject { ["sheet"] = sheet, ["scope"] = scope, ["axis"] = axis, ["start"] = start, ["count"] = count };
            if (table != null) args["table"] = table;
            return new ToolCall("delete-" + Guid.NewGuid().ToString("N"), ExcelRangeMutationToolIds.DeleteRowsColumns, args.ToString());
        }

        private static void SeedDeletionTable(FakeOfficeAdapter adapter)
        {
            var headers = new[] { "Code", "FullPath", "OG", "filename", "LastModified", "isReady", "Column1" };
            for (var c = 0; c < headers.Length; c++)
            {
                var column = ((char)('B' + c)).ToString();
                adapter.SetExcelCellForTest("Data", column + "18", headers[c]);
                adapter.SetExcelCellForTest("Data", column + "19", "first-" + headers[c]);
                adapter.SetExcelCellForTest("Data", column + "20", "second-" + headers[c]);
            }
            adapter.AddExcelTableForTest("Data", "B18:H20", "filesTB", true, "");
            adapter.SetExcelCellForTest("Data", "H1", "outside column");
            adapter.SetExcelCellForTest("Data", "J19", "outside row");
        }

        private static void ExcelDeletionRemovesTableStructure()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                SeedDeletionTable(adapter);
                var runtime = DeletionRuntime(executor, adapter);
                foreach (var clearWhat in new[] { "values", "all" })
                {
                    var clear = new ToolCall("header-" + clearWhat, ExcelRangeMutationToolIds.ClearRange,
                        new JObject { ["sheet"] = "Data", ["address"] = "H18", ["clearWhat"] = clearWhat }.ToString());
                    var refused = ExecuteNativeConfirmed(runtime, clear, runtime.Describe(clear));
                    AssertEqual(ToolExecutionOutcome.Error, refused.Outcome, "header clearing is a definite pre-dispatch refusal");
                    AssertEqual(ToolDispatchEvidence.NotDispatched, refused.Evidence.Dispatch, "header is untouched");
                    AssertEqual("excel_clear_table_header", (string)JObject.Parse(refused.Result.DataJson)["code"], "actionable header error");
                    AssertContains(refused.Message, "excel.rows.columns.delete", "recovery names the structural tool");
                }
                var formats = new ToolCall("header-format", ExcelRangeMutationToolIds.ClearRange,
                    "{\"sheet\":\"Data\",\"address\":\"H18\",\"clearWhat\":\"formats\"}");
                AssertEqual(ToolExecutionOutcome.Ok, ExecuteNativeConfirmed(runtime, formats, runtime.Describe(formats)).Outcome,
                    "format clearing remains allowed on table headers");
                var remove = DeletionCall("table", "columns", 7, 1, "filesTB");
                var policy = runtime.Describe(remove);
                AssertTrue(policy.RequiresConfirmation && policy.MayHaveSideEffects && !policy.IndependentLocalRead,
                    "structural deletion keeps confirmation and mutation policy");
                var result = ExecuteDeletion(runtime, remove);
                AssertEqual(ToolExecutionOutcome.Ok, result.Outcome, "extra table column is deleted: " + result.Message);
                AssertEqual(ToolEffectEvidence.VerifiedChange, result.Evidence.Effect, "read-back confirms table shrink");
                AssertEqual(6, JObject.Parse(result.Result.DataJson)["columns"].Count(), "six columns remain");
                AssertEqual("first-isReady", adapter.ExcelCellText("Data", "G19"), "surviving data remains");
                AssertEqual(string.Empty, adapter.ExcelCellText("Data", "H19"), "removed table cell is gone");
                AssertEqual("outside column", adapter.ExcelCellText("Data", "H1"), "worksheet column remains");
                var row = DeletionCall("table", "rows", 1, 1, "filesTB");
                result = ExecuteDeletion(runtime, row);
                AssertEqual(ToolExecutionOutcome.Ok, result.Outcome, "data row is deleted");
                AssertEqual("Code", adapter.ExcelCellText("Data", "B18"), "header is not row one");
                AssertEqual("second-Code", adapter.ExcelCellText("Data", "B19"), "remaining data row moves up");
                AssertEqual("outside row", adapter.ExcelCellText("Data", "J19"), "worksheet row remains");
                row = DeletionCall("table", "rows", 1, 1, "filesTB");
                result = ExecuteDeletion(runtime, row);
                AssertEqual(ToolExecutionOutcome.Ok, result.Outcome, "all data rows may be deleted");
                AssertEqual(0, (int)JObject.Parse(result.Result.DataJson)["dataRows"], "empty table survives");
            });
        }

        private static void ExcelDeletionShiftsWorksheetCells()
        {
            foreach (var axis in new[] { "rows", "columns" })
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                for (var r = 1; r <= 4; r++)
                for (var c = 0; c < 4; c++)
                    adapter.SetExcelCellForTest("DeleteSheet", ((char)('A' + c)).ToString() + r, r + ":" + (c + 1));
                var runtime = DeletionRuntime(executor, adapter);
                var call = DeletionCall("worksheet", axis, 2, 2, sheet: "DeleteSheet");
                var result = ExecuteDeletion(runtime, call);
                AssertEqual(ToolExecutionOutcome.Ok, result.Outcome, "worksheet deletion succeeds: " + axis + ": " + result.Message);
                AssertEqual(ToolEffectEvidence.VerifiedChange, result.Evidence.Effect, "shift has read-back evidence");
                AssertEqual(axis == "rows" ? "4:2" : "2:4", adapter.ExcelCellText("DeleteSheet", "B2"), "surviving cells shift");
                AssertEqual(string.Empty, adapter.ExcelCellText("DeleteSheet", "D4"), "old tail is empty");
                AssertEqual("1:1", adapter.ExcelCellText("DeleteSheet", "A1"), "cells before selection remain");
            });
        }

        private static void ExcelDeletionRejectsInvalidTargets()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                SeedDeletionTable(adapter);
                var runtime = DeletionRuntime(executor, adapter);
                foreach (var call in new[]
                {
                    DeletionCall("table", "columns", 1, 7, "filesTB"),
                    DeletionCall("table", "rows", 2, 2, "filesTB"),
                    DeletionCall("table", "columns", 1, 1),
                    DeletionCall("table", "columns", 1, 1, "missing"),
                    DeletionCall("worksheet", "rows", 1, 1, "filesTB"),
                    DeletionCall("worksheet", "rows", 1048576, 2),
                    DeletionCall("worksheet", "columns", 0, 1),
                    DeletionCall("worksheet", "columns", 1, 0),
                    DeletionCall("implicit", "columns", 1, 1),
                    DeletionCall("worksheet", "both", 1, 1)
                })
                {
                    var result = ExecuteDeletion(runtime, call);
                    AssertEqual(ToolExecutionOutcome.Error, result.Outcome, "invalid selection is rejected");
                    AssertEqual(ToolDispatchEvidence.NotDispatched, result.Evidence.Dispatch, "invalid selection cannot delete");
                }
                AssertTrue(!adapter.ExcelBackendCalls.Contains(FakeOfficeAdapter.ExcelDeletionApplyOperation), "no invalid selection reached apply");
                AssertEqual("first-Code", adapter.ExcelCellText("Data", "B19"), "invalid selections preserve data");
            });
        }

        private static void ExcelDeletionPinsConfirmationState()
        {
            foreach (var scope in new[] { "table", "worksheet" })
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                SeedDeletionTable(adapter);
                var runtime = DeletionRuntime(executor, adapter, false, (context, preparation) => "delete-pending");
                var call = DeletionCall(scope, "columns", 7, 1, scope == "table" ? "filesTB" : null);
                var pending = ExecuteDeletion(runtime, call);
                AssertEqual(ToolExecutionOutcome.AwaitingConfirmation, pending.Outcome, "deletion waits for approval");
                AssertTrue(!string.IsNullOrWhiteSpace(pending.PreparedStateJson), "approval pins original target state");
                AssertTrue(!adapter.ExcelBackendCalls.Contains(FakeOfficeAdapter.ExcelDeletionApplyOperation), "preparation never deletes");
                if (scope == "table") adapter.SetExcelCellForTest("Data", "B19", "external edit");
                else adapter.SetExcelCellForTest("Data", "Z50", "new data outside prepared window");
                var result = ExecuteDeletion(runtime, call, pending.PreparedStateJson);
                AssertEqual(ToolExecutionOutcome.Error, result.Outcome, "changed prepared state is rejected");
                AssertEqual(ToolDispatchEvidence.NotDispatched, result.Evidence.Dispatch, "stale approval cannot delete");
                AssertEqual("excel_delete_target_changed", (string)JObject.Parse(result.Result.DataJson)["code"], "stale approval keeps its cause");
                AssertTrue(!adapter.ExcelBackendCalls.Contains(FakeOfficeAdapter.ExcelDeletionApplyOperation), "changed state cannot reach apply");
                var nextCall = DeletionCall(scope, "columns", 7, 1, scope == "table" ? "filesTB" : null);
                pending = ExecuteDeletion(runtime, nextCall);
                result = ExecuteDeletion(runtime, nextCall, pending.PreparedStateJson);
                AssertEqual(ToolExecutionOutcome.Ok, result.Outcome, "fresh approval executes exactly the new snapshot");
            });
        }

        private static void ExcelDeletionPreservesFaultEvidence()
        {
            foreach (var fault in new[] { "drift", "throw", "no-op", "read-back", "read-failure" })
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                SeedDeletionTable(adapter);
                var runtime = DeletionRuntime(executor, adapter);
                if (fault == "drift") adapter.BeforeExcelBackendCall = operation =>
                {
                    if (operation == FakeOfficeAdapter.ExcelDeletionApplyOperation) adapter.SetExcelCellForTest("Data", "B19", "external edit");
                };
                adapter.ExcelDeletionThrowAfterMutation = fault == "throw";
                adapter.ExcelDeletionSkipMutation = fault == "no-op";
                var reads = 0;
                adapter.ExcelDeletionReadTransform = snapshot =>
                {
                    if (++reads == 3)
                    {
                        if (fault == "read-failure") throw new InvalidOperationException("read-back unavailable");
                        if (fault == "read-back") snapshot.Cells[1][0].ValueJson = "\"wrong surviving cell\"";
                    }
                    return snapshot;
                };
                var call = DeletionCall("table", "columns", 7, 1, "filesTB");
                var result = ExecuteDeletion(runtime, call);
                AssertEqual(fault == "drift" ? ToolExecutionOutcome.Error : ToolExecutionOutcome.Unknown, result.Outcome, "fault outcome: " + fault);
                AssertEqual(fault == "drift" ? ToolDispatchEvidence.NotDispatched : ToolDispatchEvidence.MayHaveDispatched,
                    result.Evidence.Dispatch, "fault dispatch evidence: " + fault);
                AssertEqual(1, adapter.ExcelBackendCalls.Count(op => op == FakeOfficeAdapter.ExcelDeletionApplyOperation), "no automatic retry");
            });
        }
    }
}
