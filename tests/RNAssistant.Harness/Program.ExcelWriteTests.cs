using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
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
        private static void ExcelWriteUsesExactNativeOwnership()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var definition = tools.Single(tool => tool.Id == ExcelWriteToolIds.WriteRange);
                var runtime = executor.CreateNativeRuntime(session, new[] { definition }, new AppSettings(), "agent", false);
                var call = new ToolCall("excel-write-change", ExcelWriteToolIds.WriteRange,
                    "{\"kind\":\"value\",\"sheet\":\"Data\",\"address\":\"J1\",\"value\":42}");
                var policy = runtime.Describe(call);
                AssertTrue(policy != null && policy.MayHaveSideEffects &&
                    policy.Policy.Verification == ToolVerification.Tool,
                    "write_range has an exact native verified-write policy");

                var changed = ExecuteNative(runtime, call, policy);
                AssertEqual(ToolExecutionOutcome.Ok, changed.Outcome, "verified write succeeds");
                AssertEqual(ToolDispatchEvidence.MayHaveDispatched, changed.Evidence.Dispatch,
                    "write records its effect boundary");
                AssertEqual(ToolEffectEvidence.VerifiedChange, changed.Evidence.Effect,
                    "matching read-back certifies a change");
                AssertEqual(1, adapter.ExcelBackendCalls.Count(operation =>
                    operation == FakeOfficeAdapter.ExcelWriteApplyOperation),
                    "one direct typed apply owns the write");
                AssertEqual(2, adapter.ExcelBackendCalls.Count(operation =>
                    operation == FakeOfficeAdapter.ExcelWriteReadOperation),
                    "the same direct backend reads before and after the exact target");

                var noopCall = new ToolCall("excel-write-noop", ExcelWriteToolIds.WriteRange,
                    "{\"kind\":\"value\",\"sheet\":\"Data\",\"address\":\"J1\",\"value\":42}");
                var noop = ExecuteNative(runtime, noopCall, policy);
                AssertEqual(ToolExecutionOutcome.Ok, noop.Outcome, "matching target is successful");
                AssertEqual(ToolDispatchEvidence.NotDispatched, noop.Evidence.Dispatch,
                    "verified no-op skips host write dispatch");
                AssertEqual(ToolEffectEvidence.VerifiedNoChange, noop.Evidence.Effect,
                    "matching before state is explicit no-change evidence");
                AssertEqual(1, adapter.ExcelBackendCalls.Count(operation =>
                    operation == FakeOfficeAdapter.ExcelWriteApplyOperation),
                    "no-op does not call apply");

                var applies = adapter.ExcelBackendCalls.Count(operation =>
                    operation == FakeOfficeAdapter.ExcelWriteApplyOperation);
                var dryRun = executor.ExecuteManual(Command(ExcelWriteToolIds.WriteRange, "kind", "value", "sheet", "Data",
                    "address", "J2", "value", "preview"), tools, new AppSettings(), true, true, session);
                AssertTrue(dryRun.Success && string.IsNullOrEmpty(adapter.CellValue("Data", "J2")),
                    "native write dry-run remains non-mutating");
                AssertEqual(applies, adapter.ExcelBackendCalls.Count(operation =>
                    operation == FakeOfficeAdapter.ExcelWriteApplyOperation),
                    "dry-run never reaches the write backend");
                AssertTrue(runtime.Describe(new ToolCall("wrong-case", "EXCEL.WRITE_RANGE", "{}")) == null,
                    "native ownership has no case alias");
            });
        }

        private static void ExcelWriteNormalizesAndVerifiesKinds()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var definition = tools.Single(tool => tool.Id == ExcelWriteToolIds.WriteRange);
                var runtime = executor.CreateNativeRuntime(session, new[] { definition }, new AppSettings(), "agent", false);

                var tableCall = new ToolCall("excel-table", ExcelWriteToolIds.WriteRange,
                    "{\"kind\":\"table\",\"sheet\":\"Data\",\"address\":\"K2\",\"values\":[[1,2],[3]]}");
                var table = ExecuteNative(runtime, tableCall, runtime.Describe(tableCall));
                AssertEqual(ToolEffectEvidence.VerifiedChange, table.Evidence.Effect,
                    "ragged table is verified after write");
                var tableJson = JObject.Parse(table.Result.DataJson);
                AssertEqual("K2:L3", (string)tableJson["address"], "table target is normalized to an exact rectangle");
                AssertEqual("3", adapter.CellValue("Data", "K3"), "ragged row keeps its provided cell");
                AssertEqual(string.Empty, adapter.CellValue("Data", "L3"),
                    "ragged row is deterministically null-padded");

                var formulaCall = new ToolCall("excel-formula", ExcelWriteToolIds.WriteRange,
                    "{\"kind\":\"formula\",\"sheet\":\"Data\",\"address\":\"M1:M2\",\"formula\":\"=1\"}");
                var formula = ExecuteNative(runtime, formulaCall, runtime.Describe(formulaCall));
                AssertEqual(ToolEffectEvidence.VerifiedChange, formula.Evidence.Effect,
                    "formula write verifies formula state across the range");
                var formulaNoop = ExecuteNative(runtime,
                    new ToolCall("excel-formula-noop", ExcelWriteToolIds.WriteRange, formulaCall.ArgumentsJson),
                    runtime.Describe(formulaCall));
                AssertEqual(ToolEffectEvidence.VerifiedNoChange, formulaNoop.Evidence.Effect,
                    "the same formula is a verified no-op");

                var constantCall = new ToolCall("excel-formula-to-value", ExcelWriteToolIds.WriteRange,
                    "{\"kind\":\"value\",\"sheet\":\"Data\",\"address\":\"M1:M2\",\"value\":\"=1\"}");
                var constant = ExecuteNative(runtime, constantCall, runtime.Describe(constantCall));
                AssertEqual(ToolEffectEvidence.VerifiedChange, constant.Evidence.Effect,
                    "equal-looking constants still replace formulas because formula flags are verified");

                var clearCall = new ToolCall("excel-null-value", ExcelWriteToolIds.WriteRange,
                    "{\"kind\":\"value\",\"sheet\":\"Data\",\"address\":\"K2\",\"value\":null}");
                var cleared = ExecuteNative(runtime, clearCall, runtime.Describe(clearCall));
                AssertEqual(ToolEffectEvidence.VerifiedChange, cleared.Evidence.Effect,
                    "required null remains a scalar clear operation");
                AssertEqual(string.Empty, adapter.CellValue("Data", "K2"),
                    "null scalar clears the exact cell");
            });

            var backend = new CountingExcelWriteBackend();
            var oversizedRow = Enumerable.Repeat((object)1, ExcelWriteService.MaxWriteColumns + 1).ToArray();
            var oversized = new ExcelWriteService(backend).Write(new ExcelWriteRequest
            {
                Kind = "table", Sheet = "Data", Address = "A1",
                Values = new IReadOnlyList<object>[] { oversizedRow }
            }, delegate { }, CancellationToken.None);
            AssertEqual(ExcelWriteOutcomeStatus.Error, oversized.Status, "oversized table is rejected");
            AssertEqual("excel_write_table_invalid", oversized.ErrorCode,
                "per-row bound is explicit before host allocation");
            AssertEqual(0, backend.ReadCount + backend.ApplyCount,
                "oversized table never reaches the backend");
        }

        private static void ExcelWriteClassifiesDispatchFaults()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var definition = tools.Single(tool => tool.Id == ExcelWriteToolIds.WriteRange);
                var runtime = executor.CreateNativeRuntime(session, new[] { definition }, new AppSettings(), "agent", false);
                adapter.QueueExcelWriteApplyFailure(
                    "protected target", "excel_write_protected", false);
                var call = new ToolCall("excel-write-refused", ExcelWriteToolIds.WriteRange,
                    "{\"kind\":\"value\",\"sheet\":\"Data\",\"address\":\"N1\",\"value\":\"x\"}");
                var refused = ExecuteNative(runtime, call, runtime.Describe(call));
                AssertEqual(ToolExecutionOutcome.Error, refused.Outcome,
                    "backend refusal before its boundary is a definite error");
                AssertEqual(ToolDispatchEvidence.NotDispatched, refused.Evidence.Dispatch,
                    "predispatch refusal has no effect boundary");
                AssertEqual(ToolEffectEvidence.None, refused.Evidence.Effect,
                    "predispatch refusal does not invent effect evidence");
                AssertEqual("excel_write_protected", (string)JObject.Parse(refused.Result.DataJson)["code"],
                    "predispatch error code survives typed mapping");
            });

            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                var definition = OfficeToolCatalog.ForHost(adapter.HostName).Single(tool => tool.Id == ExcelWriteToolIds.WriteRange);
                var runtime = executor.CreateNativeRuntime(session, new[] { definition }, new AppSettings(), "agent", false);
                adapter.BeforeExcelBackendCall = operation =>
                {
                    if (operation == FakeOfficeAdapter.ExcelWriteApplyOperation)
                        adapter.ThrowOnExcelBackendOperation = FakeOfficeAdapter.ExcelWriteReadOperation;
                };
                var call = new ToolCall("excel-write-readback-fault", ExcelWriteToolIds.WriteRange,
                    "{\"kind\":\"value\",\"sheet\":\"Data\",\"address\":\"N2\",\"value\":\"written\"}");
                var unknown = ExecuteNative(runtime, call, runtime.Describe(call));
                AssertEqual(ToolExecutionOutcome.Unknown, unknown.Outcome,
                    "unavailable read-back after dispatch is unknown");
                AssertEqual(ToolDispatchEvidence.MayHaveDispatched, unknown.Evidence.Dispatch,
                    "unknown preserves the write boundary");
                AssertEqual(ToolEffectEvidence.Unknown, unknown.Evidence.Effect,
                    "unverified final state has unknown effect evidence");
                AssertEqual("excel_write_effect_unknown", (string)JObject.Parse(unknown.Result.DataJson)["code"],
                    "postdispatch failure is non-retryable effect uncertainty");
                AssertEqual("written", adapter.CellValue("Data", "N2"),
                    "unknown does not imply that the write failed");
            });

            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                var definition = OfficeToolCatalog.ForHost(adapter.HostName).Single(tool => tool.Id == ExcelWriteToolIds.WriteRange);
                var runtime = executor.CreateNativeRuntime(session, new[] { definition }, new AppSettings(), "agent", false);
                adapter.ExcelWriteThrowAfterMutation = true;
                var call = new ToolCall("excel-write-throw-after", ExcelWriteToolIds.WriteRange,
                    "{\"kind\":\"value\",\"sheet\":\"Data\",\"address\":\"N3\",\"value\":\"written\"}");
                var unknown = ExecuteNative(runtime, call, runtime.Describe(call));
                AssertEqual(ToolExecutionOutcome.Unknown, unknown.Outcome,
                    "mutate-then-throw cannot be reported as a retryable error");
                AssertEqual(ToolEffectEvidence.Unknown, unknown.Evidence.Effect,
                    "mutate-then-throw keeps unknown effect evidence");
            });

            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                var definition = OfficeToolCatalog.ForHost(adapter.HostName).Single(tool => tool.Id == ExcelWriteToolIds.WriteRange);
                var runtime = executor.CreateNativeRuntime(session, new[] { definition }, new AppSettings(), "agent", false);
                var reads = 0;
                adapter.BeforeExcelBackendCall = operation =>
                {
                    if (operation == FakeOfficeAdapter.ExcelWriteReadOperation && ++reads == 2)
                        adapter.SetExcelCell("Data", "N4", "diverged");
                };
                var call = new ToolCall("excel-write-diverged", ExcelWriteToolIds.WriteRange,
                    "{\"kind\":\"value\",\"sheet\":\"Data\",\"address\":\"N4\",\"value\":\"intended\"}");
                var unknown = ExecuteNative(runtime, call, runtime.Describe(call));
                AssertEqual(ToolExecutionOutcome.Unknown, unknown.Outcome,
                    "divergent exact read-back is unknown");
                AssertEqual("excel_write_verification_failed",
                    (string)JObject.Parse(unknown.Result.DataJson)["code"],
                    "read-back mismatch retains its precise diagnostic code");
            });
        }

        private static void ExcelWriteUsesBoundDocumentScope()
        {
            WithTempPaths(paths =>
            {
                using (var dispatcher = new OfficeStaDispatcher())
                {
                    var document = new BoundTestDocument { StableId = "bound-excel-write", IsAlive = true };
                    var sessionPort = new BoundTestOfficeSession(dispatcher, document, "bound-runtime-write", new object());
                    var inner = FakeOfficeAdapter.ForHost("Excel");
                    var host = new BoundTestOfficeAdapter(sessionPort, inner);
                    var ownerSta = false;
                    host.BeforeRead = toolId =>
                    {
                        if (toolId == FakeOfficeAdapter.ExcelWriteApplyOperation) ownerSta = dispatcher.CheckAccess;
                    };
                    var executor = new OfficeToolExecutor(host, new VbaJournalStore(paths),
                        new SkillStore(paths), new ToolStore(paths), paths: paths);
                    var chat = new ChatSession
                    {
                        Host = "Excel", DocumentKey = "bound-excel-write", DocumentTitle = "Bound.xlsx"
                    };
                    var tools = OfficeToolCatalog.ForHost(host.HostName).Concat(executor.GetControllerTools()).ToList();
                    var result = executor.ExecuteManual(Command(ExcelWriteToolIds.WriteRange, "kind", "value",
                        "sheet", "Data", "address", "P1", "value", "bound"),
                        tools, new AppSettings(), false, false, chat);
                    AssertTrue(result.Success && ownerSta, "typed write stays on the bound document owner STA");

                    var dispatched = inner.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelWriteApplyOperation);
                    dispatcher.Invoke(() => document.IsAlive = false);
                    var closed = executor.ExecuteManual(Command(ExcelWriteToolIds.WriteRange, "kind", "value",
                        "sheet", "Data", "address", "P2", "value", "blocked"),
                        tools, new AppSettings(), false, false, chat);
                    AssertEqual("active_document_changed", closed.ErrorCode,
                        "closed bound workbook is rejected before write dispatch");
                    AssertEqual(dispatched, inner.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelWriteApplyOperation),
                        "closed workbook never reaches the write backend");
                }
            });
        }

        private static ToolExecutionRecord ExecuteNative(NativeToolRuntimeAdapter runtime,
            ToolCall call, ToolPolicySnapshot policy)
        {
            return runtime.ExecuteAsync(new ToolExecutionContext(call, policy, "run", "turn", call.Id,
                DateTime.UtcNow, false, 1), CancellationToken.None).GetAwaiter().GetResult();
        }

        private sealed class CountingExcelWriteBackend : IExcelWriteBackend
        {
            internal int ReadCount { get; private set; }
            internal int ApplyCount { get; private set; }

            public ExcelWriteSnapshot Read(ExcelWriteReadRequest request)
            {
                ReadCount++;
                throw new InvalidOperationException("Oversized input must not be read.");
            }

            public void Apply(ExcelWriteApplyRequest request, Action markDispatchPossible)
            {
                ApplyCount++;
                throw new InvalidOperationException("Oversized input must not be applied.");
            }
        }
    }
}
