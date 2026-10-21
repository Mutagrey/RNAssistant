using RNAssistant.Office.Domains;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.ModelProtocol;
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
        private static void ExcelWriteRequiresExplicitTarget()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var definition = OfficeToolCatalog.ForHost("Excel").Single(tool => tool.Id == ExcelWriteToolIds.WriteRange);
                JObject schema;
                string error;
                AssertTrue(ToolSchemaSupport.TryParse(definition, out schema, out error), "write schema parses: " + error);
                var schemas = new[] { schema, ToolSchemaSupport.ForPrompt(schema), ToolSchemaSupport.ForStructuredOutput(schema) };
                var parser = new ConversationResponseParser();
                var context = new ModelProtocolCallContext(new string[0]);
                var runtime = executor.CreateNativeRuntime(NewSession(adapter), new[] { definition },
                    new AppSettings(), "agent", false, manualRun: true);
                var backend = new CountingExcelWriteBackend();
                var direct = new ExcelWriteToolAdapter(backend);
                adapter.AddExcelSheetForTest("Sheet1");
                adapter.SetActiveExcelSheet("Sheet1");
                adapter.SetExcelCell("Sheet1", "A1", "preserve active sheet");
                adapter.SetExcelCell("Data", "J1", "preserve other cell");

                foreach (var payload in new[] {
                    JObject.Parse("{kind:'value',value:false}"),
                    JObject.Parse("{kind:'formula',formula:'=1'}"),
                    JObject.Parse("{kind:'table',values:[[42]]}") })
                {
                    var valid = (JObject)payload.DeepClone();
                    valid["sheet"] = "Data";
                    valid["address"] = "A1";
                    foreach (var field in new[] { "sheet", "address" })
                    foreach (var absent in new JToken[] { null, JValue.CreateNull(), new JValue(""), new JValue(" \t\r\n") })
                    {
                        var arguments = (JObject)valid.DeepClone();
                        if (absent == null) arguments.Remove(field);
                        else arguments[field] = absent.DeepClone();
                        foreach (var contract in schemas)
                        foreach (var defaults in new[] { false, true })
                            AssertTrue(!ToolSchemaSupport.ValidateArguments((JObject)arguments.DeepClone(), contract, defaults, out error),
                                "every schema rejects absent/blank " + field + " for " + payload["kind"]);
                        var wire = ModelProtocolWire.Write("", new[] { new ConversationToolCall { Name = definition.Id, Arguments = arguments } });
                        AssertTrue(!parser.Parse(wire, new[] { definition }, new[] { definition }, context).Success,
                            "parser cannot normalize a missing target into a default");

                        var reads = adapter.ExcelBackendCalls.Count;
                        var call = new ToolCall("missing-target", definition.Id, arguments.ToString(Formatting.None));
                        var rejected = ExecuteNative(runtime, call, runtime.Describe(call));
                        AssertEqual(ToolExecutionOutcome.Error, rejected.Outcome, "native runtime rejects an incomplete target");
                        AssertEqual(ToolDispatchEvidence.NotDispatched, rejected.Evidence.Dispatch, "rejection never dispatches");
                        AssertEqual(ToolEffectEvidence.None, rejected.Evidence.Effect, "rejection claims no write effect");
                        AssertEqual(reads, adapter.ExcelBackendCalls.Count, "invalid target never reaches Excel");

                        var dispatched = false;
                        var directResult = direct.Execute(arguments.ToObject<Dictionary<string, object>>(),
                            () => dispatched = true, CancellationToken.None);
                        AssertEqual(DomainOutcomeStatus.Error, directResult.Status, "domain rejects the target even without schema validation");
                        AssertEqual("excel_write_" + field + "_required", directResult.ErrorCode, "missing field has an actionable error");
                        AssertTrue(!dispatched && backend.ReadCount + backend.ApplyCount == 0,
                            "adapter/domain never invent a target or touch the backend");
                    }

                    foreach (var contract in schemas)
                        AssertTrue(ToolSchemaSupport.ValidateArguments((JObject)valid.DeepClone(), contract, true, out error),
                            "an explicitly selected A1 is valid in every schema: " + error);
                    var accepted = new ToolCall("explicit-target", definition.Id, valid.ToString(Formatting.None));
                    var written = ExecuteNative(runtime, accepted, runtime.Describe(accepted));
                    AssertEqual(ToolExecutionOutcome.Ok, written.Outcome, "explicit target remains writable");
                    AssertEqual(ToolEffectEvidence.VerifiedChange, written.Evidence.Effect, "each kind is verified at the selected target");
                    var result = JObject.Parse(written.Result.DataJson);
                    AssertEqual("Data", (string)result["sheet"], "result identifies the selected sheet");
                    AssertEqual("A1", (string)result["address"], "result identifies the selected cell");
                    AssertEqual("preserve active sheet", adapter.CellValue("Sheet1", "A1"), "active sheet is untouched");
                    AssertEqual("preserve other cell", adapter.CellValue("Data", "J1"), "other cells are untouched");
                }
            });
        }

        private static void ExcelWritePreservesWorksheetNames()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                var tools = OfficeToolCatalog.ForHost("Excel").Concat(executor.GetControllerTools()).ToList();
                var runtime = executor.CreateNativeRuntime(session, tools, new AppSettings(), "agent", false, manualRun: true);
                var original = adapter.CellValue("Data", "A1");
                foreach (var example in new[] {
                    new[] { "Sales Report", "'Sales Report'!A1" },
                    new[] { "O'Brien", "'O''Brien'!A1" },
                    new[] { "Q''4", "'Q''''4'!A1" },
                    new[] { " Продажи! 2026 ", "' Продажи! 2026 '!A1" },
                    new[] { "\"Data\"", "'\"Data\"'!A1" } })
                {
                    var sheet = example[0];
                    var reference = example[1];
                    adapter.AddExcelSheetForTest(sheet);
                    if (sheet != sheet.Trim())
                    {
                        adapter.AddExcelSheetForTest(sheet.Trim());
                        adapter.SetExcelCell(sheet.Trim(), "A1", "distinct trimmed name");
                    }
                    adapter.SetActiveExcelSheet("Data");
                    var write = ExecuteHtmlNative(runtime, ExcelWriteToolIds.WriteRange,
                        new JObject { ["kind"] = "value", ["sheet"] = sheet, ["address"] = "A1", ["value"] = sheet });
                    AssertEqual(ToolEffectEvidence.VerifiedChange, write.Evidence.Effect, "literal worksheet name is writable");
                    AssertEqual(sheet, (string)JObject.Parse(write.Result.DataJson)["sheet"], "sheet field keeps its literal name");
                    AssertContains(write.Result.Message, reference, "write result quotes the composed reference exactly once");

                    var target = "Excel range: " + reference;
                    var found = executor.ResourceGateway.Find(session, target, "document");
                    AssertEqual(target, found.Items.Single().Target, "quoted reference round-trips through discovery");
                    var read = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                        new JObject { ["target"] = target, ["representation"] = "text" });
                    AssertEqual(ToolExecutionOutcome.Ok, read.Outcome, "generated reference is readable");
                    var values = JArray.Parse((string)JObject.Parse(read.Result.DataJson)["text"]);
                    AssertEqual(sheet, (string)values[0][0], "read resolves the same exact worksheet");
                    var listed = executor.ResourceGateway.List(session, "excel", "excel-range", null, 50);
                    var listedSheet = listed.Items.Single(item => item.Metadata["sheet"] == sheet);
                    AssertEqual(reference.Substring(0, reference.Length - 2) + listedSheet.Metadata["address"],
                        listedSheet.Title, "enumeration quotes the literal sheet name and its own used range");
                    AssertEqual(original, adapter.CellValue("Data", "A1"), "other active worksheet is untouched");
                    if (sheet != sheet.Trim())
                        AssertEqual("distinct trimmed name", adapter.CellValue(sheet.Trim(), "A1"), "trimmed name remains a distinct worksheet");
                }

                var malformed = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = "Excel range: 'O'Brien'!A1", ["representation"] = "text" });
                AssertEqual(ToolExecutionOutcome.Error, malformed.Outcome, "undoubled internal apostrophe is not silently repaired");
                var quotedName = ExecuteHtmlNative(runtime, ExcelWriteToolIds.WriteRange,
                    new JObject { ["kind"] = "value", ["sheet"] = "'Sales Report'", ["address"] = "A1", ["value"] = "wrong" });
                AssertEqual(ToolExecutionOutcome.Error, quotedName.Outcome, "reference delimiters are not part of the sheet argument");
                AssertEqual(ToolDispatchEvidence.NotDispatched, quotedName.Evidence.Dispatch, "invalid literal sheet name never dispatches");
                AssertEqual("Sales Report", adapter.CellValue("Sales Report", "A1"), "literal names are never silently redirected");
            });
        }

        private static void ExcelWriteUsesExactNativeOwnership()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var definition = tools.Single(tool => tool.Id == ExcelWriteToolIds.WriteRange);
                var runtime = executor.CreateNativeRuntime(session, new[] { definition }, new AppSettings(), "agent", false, manualRun: true);
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
                var runtime = executor.CreateNativeRuntime(session, new[] { definition }, new AppSettings(), "agent", false, manualRun: true);

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
            AssertEqual(DomainOutcomeStatus.Error, oversized.Status, "oversized table is rejected");
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
                var runtime = executor.CreateNativeRuntime(session, new[] { definition }, new AppSettings(), "agent", false, manualRun: true);
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
                var refusedData = JObject.Parse(refused.Result.DataJson);
                AssertEqual("excel_write_protected", (string)refusedData["code"],
                    "predispatch error code survives typed mapping");
                AssertEqual("RejectedNoEffect",
                    (string)refused.Recovery?.ModelProjection()?["failureKind"],
                    "definite domain refusal exposes no-effect recovery");
                AssertEqual("Replan",
                    (string)refused.Recovery?.ModelProjection()?["retryPolicy"],
                    "non-retryable domain refusal requires a changed plan");

                adapter.QueueExcelWriteApplyFailure(
                    "Excel is temporarily busy", "excel_write_busy", true);
                var transientCall = new ToolCall("excel-write-busy",
                    ExcelWriteToolIds.WriteRange,
                    "{\"kind\":\"value\",\"sheet\":\"Data\",\"address\":\"N5\",\"value\":\"x\"}");
                var transient = ExecuteNative(runtime, transientCall,
                    runtime.Describe(transientCall));
                var transientData = JObject.Parse(transient.Result.DataJson);
                AssertEqual(ToolExecutionOutcome.Error, transient.Outcome,
                    "retryable predispatch backend refusal remains a definite error");
                AssertEqual("RejectedNoEffect",
                    (string)transient.Recovery?.ModelProjection()?["failureKind"],
                    "transient refusal still certifies no effect");
                AssertEqual("RetryLater",
                    (string)transient.Recovery?.ModelProjection()?["retryPolicy"],
                    "transient refusal is deferred without automatic replay");
            });

            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                var definition = OfficeToolCatalog.ForHost(adapter.HostName).Single(tool => tool.Id == ExcelWriteToolIds.WriteRange);
                var runtime = executor.CreateNativeRuntime(session, new[] { definition }, new AppSettings(), "agent", false, manualRun: true);
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
                var runtime = executor.CreateNativeRuntime(session, new[] { definition }, new AppSettings(), "agent", false, manualRun: true);
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
                var runtime = executor.CreateNativeRuntime(session, new[] { definition }, new AppSettings(), "agent", false, manualRun: true);
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
                    var recovery = closed.Recovery?.ModelProjection();
                    AssertEqual("RejectedNoEffect", (string)recovery?["failureKind"],
                        "closed bound workbook exposes a typed no-effect rejection");
                    AssertEqual("None", (string)recovery?["retryPolicy"],
                        "closed document is not retried inside the model loop");
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
                throw new InvalidOperationException("Invalid input must not be read.");
            }

            public void Apply(ExcelWriteApplyRequest request, Action markDispatchPossible)
            {
                ApplyCount++;
                throw new InvalidOperationException("Invalid input must not be applied.");
            }
        }
    }
}
