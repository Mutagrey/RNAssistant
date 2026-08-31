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
        private static void ExcelSheetUsesExactNativeOwnership()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var session = NewSession(adapter);
                    var tools = adapter.GetBuiltInTools()
                        .Concat(executor.GetControllerTools()).ToList();
                    var runtime = executor.CreateNativeRuntime(
                        session,
                        tools.Where(tool => ExcelSheetToolIds.Owns(tool.Id)),
                        new AppSettings(), "agent", false);

                    var add = new ToolCall(
                        "sheet-add", ExcelSheetToolIds.AddSheet,
                        "{\"name\":\"Report\"}");
                    var addPolicy = runtime.Describe(add);
                    AssertTrue(addPolicy != null && addPolicy.MayHaveSideEffects &&
                        addPolicy.Policy.Verification == ToolVerification.Tool,
                        "add_sheet has one exact native verified policy");
                    var added = ExecuteNativeConfirmed(runtime, add, addPolicy);
                    AssertEqual(ToolExecutionOutcome.Ok, added.Outcome,
                        "typed add succeeds");
                    AssertEqual(ToolEffectEvidence.VerifiedChange,
                        added.Evidence.Effect,
                        "add read-back certifies the change");
                    AssertTrue(adapter.HasSheet("Report"),
                        "direct sheet backend adds the exact name");

                    var rename = new ToolCall(
                        "sheet-rename", ExcelSheetToolIds.RenameSheet,
                        "{\"sheet\":\"Report\",\"newName\":\"Summary\"}");
                    var renamed = ExecuteNativeConfirmed(
                        runtime, rename, runtime.Describe(rename));
                    AssertEqual(ToolEffectEvidence.VerifiedChange,
                        renamed.Evidence.Effect,
                        "rename read-back certifies the change");
                    AssertTrue(adapter.HasSheet("Summary") &&
                        !adapter.HasSheet("Report"),
                        "direct sheet backend renames the exact target");
                    AssertEqual(0, adapter.Executed.Count(command =>
                        ExcelSheetToolIds.Owns(command.ToolId)),
                        "sheet public ids never reach generic host dispatch");

                    var addCalls = adapter.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelSheetAddOperation);
                    var dryRun = executor.Execute(
                        Command(ExcelSheetToolIds.AddSheet, "name", "DryRun"),
                        tools, new AppSettings(), true, true, session);
                    AssertTrue(dryRun.Success && !adapter.HasSheet("DryRun"),
                        "native sheet dry-run stays non-mutating");
                    AssertEqual(addCalls, adapter.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelSheetAddOperation),
                        "dry-run never enters the sheet backend");
                    AssertTrue(runtime.Describe(new ToolCall(
                        "sheet-case", "EXCEL.ADD_SHEET", "{}")) == null,
                        "native sheet ownership has no case alias");
                });
        }

        private static void ExcelSheetPreservesLifecycleSemantics()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var runtime = ExcelSheetRuntime(executor, adapter);
                    var defaultAdd = new ToolCall(
                        "sheet-default", ExcelSheetToolIds.AddSheet, "{}");
                    var added = ExecuteNativeConfirmed(
                        runtime, defaultAdd, runtime.Describe(defaultAdd));
                    AssertEqual(ToolExecutionOutcome.Ok, added.Outcome,
                        "default sheet creation succeeds");
                    AssertTrue(adapter.HasSheet("AI Sheet"),
                        "missing name preserves AI Sheet default");

                    var calls = adapter.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelSheetAddOperation);
                    var duplicate = new ToolCall(
                        "sheet-duplicate", ExcelSheetToolIds.AddSheet,
                        "{\"name\":\"ai sheet\"}");
                    var duplicateResult = ExecuteNativeConfirmed(
                        runtime, duplicate, runtime.Describe(duplicate));
                    AssertEqual(ToolExecutionOutcome.Error, duplicateResult.Outcome,
                        "case-insensitive duplicate is rejected");
                    AssertEqual(ToolDispatchEvidence.NotDispatched,
                        duplicateResult.Evidence.Dispatch,
                        "duplicate is rejected before effect");
                    AssertEqual(calls, adapter.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelSheetAddOperation),
                        "domain validation avoids duplicate apply");

                    var invalid = new ToolCall(
                        "sheet-invalid", ExcelSheetToolIds.AddSheet,
                        "{\"name\":\"bad/name\"}");
                    var invalidResult = ExecuteNativeConfirmed(
                        runtime, invalid, runtime.Describe(invalid));
                    AssertEqual("excel_sheet_name_invalid",
                        (string)JObject.Parse(invalidResult.Result.DataJson)["code"],
                        "worksheet name rules remain exact");

                    adapter.SetActiveExcelSheet("Data");
                    var activeRename = new ToolCall(
                        "sheet-active", ExcelSheetToolIds.RenameSheet,
                        "{\"newName\":\"Input\"}");
                    ExecuteNativeConfirmed(
                        runtime, activeRename, runtime.Describe(activeRename));
                    AssertTrue(adapter.HasSheet("Input") && !adapter.HasSheet("Data"),
                        "omitted source still targets the active bound sheet");

                    var renameCalls = adapter.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelSheetRenameOperation);
                    var noChange = new ToolCall(
                        "sheet-no-change", ExcelSheetToolIds.RenameSheet,
                        "{\"sheet\":\"Input\",\"newName\":\"Input\"}");
                    var noChangeResult = ExecuteNativeConfirmed(
                        runtime, noChange, runtime.Describe(noChange));
                    AssertEqual(ToolEffectEvidence.VerifiedNoChange,
                        noChangeResult.Evidence.Effect,
                        "identical rename is explicit no-change");
                    AssertEqual(ToolDispatchEvidence.NotDispatched,
                        noChangeResult.Evidence.Dispatch,
                        "identical rename skips host assignment");
                    AssertEqual(renameCalls, adapter.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelSheetRenameOperation),
                        "no-change avoids rename apply");
                });
        }

        private static void ExcelSheetClassifiesDispatchFaults()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var runtime = ExcelSheetRuntime(executor, adapter);
                    adapter.BeforeExcelBackendCall = operation =>
                    {
                        if (operation == FakeOfficeAdapter.ExcelSheetAddOperation)
                            adapter.AddExcelSheetForTest("Concurrent");
                    };
                    var call = new ToolCall(
                        "sheet-stale", ExcelSheetToolIds.AddSheet,
                        "{\"name\":\"Planned\"}");
                    var stale = ExecuteNativeConfirmed(
                        runtime, call, runtime.Describe(call));
                    AssertEqual(ToolExecutionOutcome.Error, stale.Outcome,
                        "changed sheet collection is a definite error");
                    AssertEqual(ToolDispatchEvidence.NotDispatched,
                        stale.Evidence.Dispatch,
                        "stale collection never crosses the effect boundary");
                    AssertEqual("excel_sheet_target_changed",
                        (string)JObject.Parse(stale.Result.DataJson)["code"],
                        "stale collection keeps its exact diagnostic");
                });

            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    adapter.ExcelSheetThrowAfterMutation = true;
                    var runtime = ExcelSheetRuntime(executor, adapter);
                    var call = new ToolCall(
                        "sheet-throw", ExcelSheetToolIds.AddSheet,
                        "{\"name\":\"MaybeAdded\"}");
                    var unknown = ExecuteNativeConfirmed(
                        runtime, call, runtime.Describe(call));
                    AssertEqual(ToolExecutionOutcome.Unknown, unknown.Outcome,
                        "add-then-throw is unknown");
                    AssertEqual(ToolEffectEvidence.Unknown, unknown.Evidence.Effect,
                        "post-dispatch failure preserves unknown effect");
                    AssertTrue(adapter.HasSheet("MaybeAdded"),
                        "unknown does not imply that creation failed");
                });

            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var runtime = ExcelSheetRuntime(executor, adapter);
                    var reads = 0;
                    adapter.ExcelSheetReadTransform = snapshot =>
                    {
                        reads++;
                        if (reads < 2) return snapshot;
                        return new ExcelSheetCollectionSnapshot
                        {
                            ActiveSheet = snapshot.ActiveSheet,
                            SheetNames = snapshot.SheetNames.Where(name =>
                                !string.Equals(name, "Diverged", StringComparison.Ordinal))
                                .ToArray()
                        };
                    };
                    var call = new ToolCall(
                        "sheet-diverged", ExcelSheetToolIds.AddSheet,
                        "{\"name\":\"Diverged\"}");
                    var unknown = ExecuteNativeConfirmed(
                        runtime, call, runtime.Describe(call));
                    AssertEqual(ToolExecutionOutcome.Unknown, unknown.Outcome,
                        "divergent sheet read-back is unknown");
                    AssertEqual("excel_sheet_verification_failed",
                        (string)JObject.Parse(unknown.Result.DataJson)["code"],
                        "divergent read-back keeps the precise code");
                });
        }

        private static void ExcelSheetUsesBoundDocumentScope()
        {
            WithTempPaths(paths =>
            {
                using (var dispatcher = new OfficeStaDispatcher())
                {
                    var document = new BoundTestDocument
                    {
                        StableId = "bound-excel-sheet",
                        IsAlive = true
                    };
                    var sessionPort = new BoundTestOfficeSession(
                        dispatcher, document, "bound-runtime-sheet", new object());
                    var inner = FakeOfficeAdapter.ForHost("Excel");
                    var host = new BoundTestOfficeAdapter(sessionPort, inner);
                    var ownerSta = false;
                    host.BeforeRead = operation =>
                    {
                        if (operation == FakeOfficeAdapter.ExcelSheetAddOperation)
                            ownerSta = dispatcher.CheckAccess;
                    };
                    var executor = new OfficeToolExecutor(host,
                        new VbaJournalStore(paths), new SkillStore(paths),
                        new ToolStore(paths), paths: paths);
                    var chat = new ChatSession
                    {
                        Host = "Excel",
                        DocumentKey = "bound-excel-sheet",
                        DocumentTitle = "Bound.xlsx"
                    };
                    var tools = host.GetBuiltInTools()
                        .Concat(executor.GetControllerTools()).ToList();
                    var result = executor.Execute(Command(
                        ExcelSheetToolIds.AddSheet, "name", "BoundSheet"),
                        tools, new AppSettings(), false, true, chat);
                    AssertTrue(result.Success && ownerSta,
                        "sheet mutation stays on the bound document owner STA");

                    var dispatched = inner.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelSheetAddOperation);
                    dispatcher.Invoke(() => document.IsAlive = false);
                    var closed = executor.Execute(Command(
                        ExcelSheetToolIds.AddSheet, "name", "Blocked"),
                        tools, new AppSettings(), false, true, chat);
                    AssertEqual("active_document_changed", closed.ErrorCode,
                        "closed bound workbook is rejected before sheet mutation");
                    AssertEqual(dispatched, inner.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelSheetAddOperation),
                        "closed workbook never reaches sheet apply");
                }
            });
        }

        private static NativeToolRuntimeAdapter ExcelSheetRuntime(
            OfficeToolExecutor executor,
            FakeOfficeAdapter adapter)
        {
            return executor.CreateNativeRuntime(
                NewSession(adapter),
                adapter.GetBuiltInTools().Where(tool =>
                    ExcelSheetToolIds.Owns(tool.Id)),
                new AppSettings(), "agent", false);
        }
    }
}
