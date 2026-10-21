using System;
using System.Linq;
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
        private static void ExcelChartUsesExactNativeOwnership()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var session = NewSession(adapter);
                    var tools = OfficeToolCatalog.ForHost(adapter.HostName)
                        .Concat(executor.GetControllerTools()).ToList();
                    var runtime = executor.CreateNativeRuntime(
                        session,
                        tools.Where(tool => ExcelChartToolIds.Owns(tool.Id)),
                        new AppSettings(), "agent", false);
                    foreach (var id in new[]
                    {
                        ExcelChartToolIds.CreateChatChart,
                        ExcelChartToolIds.UpsertChart,
                        ExcelChartToolIds.DeleteChart
                    })
                    {
                        var policy = runtime.Describe(new ToolCall(
                            "chart-policy-" + id, id, id ==
                                ExcelChartToolIds.DeleteChart
                                ? "{\"chartName\":\"X\"}" : "{}"));
                        AssertTrue(policy != null,
                            "chart family has one exact native registration: " + id);
                        AssertEqual(
                            ExcelChartToolIds.IsMutation(id),
                            policy.MayHaveSideEffects,
                            "chart effect policy is source-owned: " + id);
                        if (ExcelChartToolIds.IsMutation(id))
                            AssertEqual(ToolVerification.Tool,
                                policy.Policy.Verification,
                                "chart mutations require tool verification: " + id);
                    }

                    var chatCall = new ToolCall(
                        "chart-chat", ExcelChartToolIds.CreateChatChart,
                        "{\"sheet\":\"Data\",\"address\":\"A1:B4\",\"title\":\"Sales\",\"chartType\":\"column\"}");
                    var chat = ExecuteNative(
                        runtime, chatCall, runtime.Describe(chatCall));
                    AssertEqual(ToolExecutionOutcome.Ok, chat.Outcome,
                        "chat chart uses the typed read backend");
                    var artifact = JObject.Parse(chat.Result.DataJson);
                    AssertEqual("rnassistant.chart", (string)artifact["Type"],
                        "chat chart keeps the artifact contract");
                    AssertEqual("Data", (string)artifact["Source"]["Sheet"],
                        "chat chart source is bound and explicit");

                    var applyCalls = adapter.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelChartApplyOperation);
                    var dryRun = executor.ExecuteManual(Command(
                        ExcelChartToolIds.UpsertChart,
                        "sheet", "Data", "chartName", "DryChart",
                        "sourceRange", "A1:B4"),
                        tools, new AppSettings(), true, true, session);
                    AssertTrue(dryRun.Success &&
                        adapter.ExcelChartForTest("Data", "DryChart") == null,
                        "native chart dry-run stays non-mutating");
                    AssertEqual(applyCalls,
                        adapter.ExcelBackendCalls.Count(operation =>
                            operation == FakeOfficeAdapter.ExcelChartApplyOperation),
                        "dry-run never enters chart apply");
                    AssertTrue(runtime.Describe(new ToolCall(
                        "chart-case", "EXCEL.UPSERT_CHART", "{}")) == null,
                        "native chart ownership has no case alias");
                });
        }

        private static void ExcelChartPreservesFamilySemantics()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var runtime = ExcelChartRuntime(executor, adapter);
                    var definition = OfficeToolCatalog.ForHost("Excel")
                        .Single(tool => tool.Id == ExcelChartToolIds.UpsertChart);
                    JObject schema;
                    string error;
                    AssertTrue(ToolSchemaSupport.TryParse(definition, out schema, out error),
                        "chart schema parses: " + error);
                    foreach (var contract in new[] { schema,
                        ToolSchemaSupport.ForPrompt(schema) })
                    foreach (var invalid in new[] { "", " \t" })
                        AssertTrue(!ToolSchemaSupport.ValidateArguments(
                            new JObject { ["sourceRange"] = invalid },
                            contract, true, out error),
                            "present blank chart source is rejected by schema");
                    AssertTrue(!ToolSchemaSupport.ValidateArguments(
                        new JObject { ["sourceRange"] = "A1:B6",
                            ["chartType"] = "scatter" },
                        schema, true, out error),
                        "schema rejects unsupported chart types");
                    var strict = ToolSchemaSupport.ForStructuredOutput(schema);
                    var strictUpdate = new JObject();
                    foreach (var property in ((JObject)schema["properties"]).Properties())
                        strictUpdate[property.Name] = JValue.CreateNull();
                    strictUpdate["sheet"] = "Data";
                    strictUpdate["chartName"] = "Chart 1";
                    strictUpdate["left"] = 16;
                    strictUpdate["sourceRange"] = " \t";
                    AssertTrue(!ToolSchemaSupport.ValidateArguments(
                        (JObject)strictUpdate.DeepClone(), strict, false, out error),
                        "strict schema rejects a present blank chart source");
                    strictUpdate["sourceRange"] = JValue.CreateNull();
                    strictUpdate["chartType"] = "scatter";
                    AssertTrue(!ToolSchemaSupport.ValidateArguments(
                        (JObject)strictUpdate.DeepClone(), strict, false, out error),
                        "strict schema rejects unsupported chart types");
                    strictUpdate["chartType"] = JValue.CreateNull();
                    AssertTrue(ToolSchemaSupport.ValidateArguments(
                        (JObject)strictUpdate.DeepClone(), strict, false, out error),
                        "strict update may omit the source via null: " + error);

                    var initialApplyCalls = adapter.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelChartApplyOperation);
                    var marked = false;
                    var badType = new ExcelChartService(adapter).Mutate(
                        new ExcelChartMutationRequest
                        {
                            ToolId = ExcelChartToolIds.UpsertChart,
                            Sheet = "Data",
                            ChartName = "BadType",
                            HasSourceRange = true,
                            SourceRange = "A1:B6",
                            HasChartType = true,
                            ChartType = "scatter"
                        }, () => marked = true,
                        System.Threading.CancellationToken.None);
                    AssertEqual("excel_chart_type_invalid", badType.ErrorCode,
                        "domain refuses an unsupported chart type");
                    AssertTrue(!marked &&
                        adapter.ExcelChartForTest("Data", "BadType") == null,
                        "unsupported chart type never creates a line chart");
                    foreach (var source in new[] { "{}",
                        "{\"mode\":\"createOnly\",\"sheet\":\"Data\",\"chartName\":\"NoSource\"}",
                        "{\"sourceRange\":null}",
                        "{\"sourceRange\":\"\"}",
                        "{\"sourceRange\":\" \"}" })
                    {
                        var missing = new ToolCall(
                            "chart-source-missing", ExcelChartToolIds.UpsertChart,
                            source);
                        var rejected = ExecuteNativeConfirmed(
                            runtime, missing, runtime.Describe(missing));
                        AssertEqual(ToolExecutionOutcome.Error, rejected.Outcome,
                            "chart creation requires an explicit source");
                        AssertEqual(ToolDispatchEvidence.NotDispatched,
                            rejected.Evidence.Dispatch,
                            "missing chart source never dispatches");
                        AssertEqual(ToolEffectEvidence.None,
                            rejected.Evidence.Effect,
                            "missing chart source has no effect");
                        AssertEqual(initialApplyCalls,
                            adapter.ExcelBackendCalls.Count(operation =>
                                operation == FakeOfficeAdapter.ExcelChartApplyOperation),
                            "missing chart source never reaches apply");
                    }
                    var missingSource = new ToolCall(
                        "chart-source-domain", ExcelChartToolIds.UpsertChart,
                        "{\"chartName\":\"NoSource\"}");
                    var domainError = ExecuteNativeConfirmed(
                        runtime, missingSource, runtime.Describe(missingSource));
                    AssertEqual("excel_chart_source_required",
                        (string)JObject.Parse(domainError.Result.DataJson)["code"],
                        "upsert creation reports the missing source");

                    var create = new ToolCall(
                        "chart-default", ExcelChartToolIds.UpsertChart,
                        "{\"sourceRange\":\"A1:B6\"}");
                    var created = ExecuteNativeConfirmed(
                        runtime, create, runtime.Describe(create));
                    AssertEqual(ToolExecutionOutcome.Ok, created.Outcome,
                        "chart creation with an explicit source succeeds");
                    var chart = adapter.ExcelChartForTest("Data", "Chart 1");
                    AssertTrue(chart != null && chart.ChartType == "line" &&
                        chart.HasTitle && chart.Title == "Chart" &&
                        chart.Series.Count == 1 &&
                        chart.Series[0].Formula.IndexOf(
                            "A1:B6", StringComparison.Ordinal) >= 0 &&
                        chart.Left == 300 && chart.Top == 20 &&
                        chart.Width == 480 && chart.Height == 300,
                        "creation keeps type, title, geometry and generated-name defaults");

                    var update = new ToolCall(
                        "chart-update", ExcelChartToolIds.UpsertChart,
                        "{\"sheet\":\"Data\",\"chartName\":\"Chart 1\",\"sourceRange\":\"A1:B4\",\"chartType\":\"bar\",\"title\":\"\",\"categoryLabelsRange\":\"A2:A4\",\"xAxisTitle\":\"Period\",\"yAxisTitle\":\"Amount\",\"left\":12,\"top\":18,\"width\":10,\"height\":20}");
                    var updated = ExecuteNativeConfirmed(
                        runtime, update, runtime.Describe(update));
                    AssertEqual(ToolExecutionOutcome.Ok, updated.Outcome,
                        "explicit chart update succeeds");
                    chart = adapter.ExcelChartForTest("Data", "Chart 1");
                    AssertTrue(chart.ChartType == "bar" && !chart.HasTitle &&
                        chart.HasXAxisTitle && chart.XAxisTitle == "Period" &&
                        chart.HasYAxisTitle && chart.YAxisTitle == "Amount" &&
                        chart.Left == 12 && chart.Top == 18 &&
                        chart.Width == 40 && chart.Height == 40,
                        "explicit update applies labels and clamped geometry");

                    var applyCalls = adapter.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelChartApplyOperation);
                    var noChange = ExecuteNativeConfirmed(
                        runtime, update, runtime.Describe(update));
                    AssertEqual(ToolExecutionOutcome.Ok, noChange.Outcome,
                        "matching update is successful");
                    AssertEqual(ToolEffectEvidence.VerifiedNoChange,
                        noChange.Evidence.Effect,
                        "matching update reports verified no-change");
                    AssertEqual(applyCalls,
                        adapter.ExcelBackendCalls.Count(operation =>
                            operation == FakeOfficeAdapter.ExcelChartApplyOperation),
                        "verified no-change skips chart dispatch");

                    var wire = ModelProtocolWire.Write("", new[] {
                        new ConversationToolCall { Name = definition.Id,
                            Arguments = (JObject)strictUpdate.DeepClone() } });
                    var parsed = new ConversationResponseParser().Parse(
                        wire, new[] { definition }, new[] { definition },
                        new ModelProtocolCallContext(new string[0]));
                    AssertTrue(parsed.Success,
                        "strict update with null source parses: " + parsed.Error);
                    var preservedArguments = parsed.Response.ToolCalls.Single().Arguments;
                    AssertTrue(preservedArguments.Property("sourceRange") == null,
                        "parser removes the optional null source");
                    var sourceFormula = chart.Series[0].Formula;
                    var preserve = new ToolCall(
                        "chart-preserve-source", ExcelChartToolIds.UpsertChart,
                        preservedArguments.ToString(Formatting.None));
                    var preserved = ExecuteNativeConfirmed(
                        runtime, preserve, runtime.Describe(preserve));
                    AssertEqual(ToolExecutionOutcome.Ok, preserved.Outcome,
                        "geometry-only update preserves source");
                    chart = adapter.ExcelChartForTest("Data", "Chart 1");
                    AssertTrue(chart.ChartType == "bar" && chart.Left == 16 &&
                        chart.Series[0].Formula == sourceFormula,
                        "omitted source, type and labels remain unchanged");

                    var createOnly = new ToolCall(
                        "chart-create-only", ExcelChartToolIds.UpsertChart,
                        "{\"mode\":\"createOnly\",\"sheet\":\"Data\",\"chartName\":\"Chart 1\"}");
                    AssertEqual(ToolExecutionOutcome.Error,
                        ExecuteNativeConfirmed(runtime, createOnly,
                            runtime.Describe(createOnly)).Outcome,
                        "createOnly rejects an existing chart");
                    var updateOnly = new ToolCall(
                        "chart-update-only", ExcelChartToolIds.UpsertChart,
                        "{\"mode\":\"updateOnly\",\"sheet\":\"Data\",\"chartName\":\"Missing\"}");
                    AssertEqual(ToolExecutionOutcome.Error,
                        ExecuteNativeConfirmed(runtime, updateOnly,
                            runtime.Describe(updateOnly)).Outcome,
                        "updateOnly rejects a missing chart");

                    adapter.AddExcelSheetForTest("Other");
                    adapter.AddExcelChartForTest(
                        "Data", "A1:B4", "Duplicate");
                    adapter.AddExcelChartForTest(
                        "Other", "A1:B4", "Duplicate");
                    var ambiguous = new ToolCall(
                        "chart-ambiguous", ExcelChartToolIds.DeleteChart,
                        "{\"chartName\":\"Duplicate\"}");
                    var ambiguity = ExecuteNativeConfirmed(
                        runtime, ambiguous, runtime.Describe(ambiguous));
                    AssertEqual("excel_chart_ambiguous",
                        (string)JObject.Parse(
                            ambiguity.Result.DataJson)["code"],
                        "cross-sheet duplicate requires an explicit sheet");

                    var oversized = new ToolCall(
                        "chart-bound", ExcelChartToolIds.UpsertChart,
                        "{\"sheet\":\"Data\",\"chartName\":\"TooBig\",\"sourceRange\":\"A1:A10001\"}");
                    var bound = ExecuteNativeConfirmed(
                        runtime, oversized, runtime.Describe(oversized));
                    AssertEqual(ToolExecutionOutcome.Error, bound.Outcome,
                        "oversized chart source is a definite error");
                    AssertEqual(ToolDispatchEvidence.NotDispatched,
                        bound.Evidence.Dispatch,
                        "chart source ceiling applies before dispatch");

                    var delete = new ToolCall(
                        "chart-delete", ExcelChartToolIds.DeleteChart,
                        "{\"sheet\":\"Data\",\"chartName\":\"Chart 1\"}");
                    var deleted = ExecuteNativeConfirmed(
                        runtime, delete, runtime.Describe(delete));
                    AssertEqual(ToolExecutionOutcome.Ok, deleted.Outcome,
                        "chart delete succeeds");
                    AssertTrue(adapter.ExcelChartForTest(
                        "Data", "Chart 1") == null,
                        "chart delete has exact read-back");
                });
        }

        private static void ExcelChartClassifiesDispatchFaults()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    adapter.AddExcelChartForTest(
                        "Data", "A1:B4", "BrokenGeometry");
                    adapter.ExcelChartReadTransform = snapshot =>
                    {
                        snapshot.Charts[0].Width = double.NaN;
                        return snapshot;
                    };
                    var runtime = ExcelChartRuntime(executor, adapter);
                    var call = new ToolCall(
                        "chart-invalid-snapshot", ExcelChartToolIds.UpsertChart,
                        "{\"sheet\":\"Data\",\"chartName\":\"BrokenGeometry\"}");
                    var invalid = ExecuteNativeConfirmed(
                        runtime, call, runtime.Describe(call));
                    AssertEqual(ToolExecutionOutcome.Error, invalid.Outcome,
                        "invalid chart snapshot fails before dispatch");
                    AssertEqual(ToolDispatchEvidence.NotDispatched,
                        invalid.Evidence.Dispatch,
                        "invalid chart snapshot has no mutation effect");
                    var diagnostic = JObject.Parse(invalid.Result.DataJson);
                    AssertEqual("excel_chart_snapshot_invalid",
                        (string)diagnostic["code"],
                        "invalid chart retains its error code");
                    AssertEqual("width",
                        (string)diagnostic.SelectToken("details.field"),
                        "invalid chart identifies the rejected field");
                    AssertEqual(0,
                        (int)diagnostic.SelectToken("details.chartIndex"),
                        "invalid chart identifies the item index");
                });

            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var runtime = ExcelChartRuntime(executor, adapter);
                    adapter.BeforeExcelBackendCall = operation =>
                    {
                        if (operation == FakeOfficeAdapter.ExcelChartApplyOperation)
                            adapter.SetExcelCellForTest("Data", "A1", "changed");
                    };
                    var call = new ToolCall(
                        "chart-stale", ExcelChartToolIds.UpsertChart,
                        "{\"sheet\":\"Data\",\"chartName\":\"StaleChart\",\"sourceRange\":\"A1:B6\"}");
                    var stale = ExecuteNativeConfirmed(
                        runtime, call, runtime.Describe(call));
                    AssertEqual(ToolExecutionOutcome.Error, stale.Outcome,
                        "changed chart source is a definite error");
                    AssertEqual(ToolDispatchEvidence.NotDispatched,
                        stale.Evidence.Dispatch,
                        "stale chart source never crosses the effect boundary");
                    AssertEqual("excel_chart_target_changed",
                        (string)JObject.Parse(stale.Result.DataJson)["code"],
                        "stale chart source keeps its exact diagnostic");
                });

            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    adapter.ExcelChartThrowAfterMutation = true;
                    var runtime = ExcelChartRuntime(executor, adapter);
                    var call = new ToolCall(
                        "chart-throw", ExcelChartToolIds.UpsertChart,
                        "{\"sheet\":\"Data\",\"chartName\":\"MaybeChart\",\"sourceRange\":\"A1:B6\"}");
                    var unknown = ExecuteNativeConfirmed(
                        runtime, call, runtime.Describe(call));
                    AssertEqual(ToolExecutionOutcome.Unknown, unknown.Outcome,
                        "create-then-throw is unknown");
                    AssertEqual(ToolEffectEvidence.Unknown,
                        unknown.Evidence.Effect,
                        "post-dispatch chart failure preserves unknown effect");
                    AssertTrue(adapter.ExcelChartForTest(
                        "Data", "MaybeChart") != null,
                        "unknown does not imply chart creation failed");
                });

            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var runtime = ExcelChartRuntime(executor, adapter);
                    var reads = 0;
                    adapter.ExcelChartReadTransform = snapshot =>
                    {
                        reads++;
                        if (reads < 3) return snapshot;
                        return new ExcelChartCollectionSnapshot
                        {
                            ActiveSheet = snapshot.ActiveSheet,
                            StateToken = snapshot.StateToken,
                            Charts = snapshot.Charts.Where(chart =>
                                !string.Equals(chart.Name, "DivergedChart",
                                    StringComparison.Ordinal)).ToArray()
                        };
                    };
                    var call = new ToolCall(
                        "chart-diverged", ExcelChartToolIds.UpsertChart,
                        "{\"sheet\":\"Data\",\"chartName\":\"DivergedChart\",\"sourceRange\":\"A1:B6\"}");
                    var unknown = ExecuteNativeConfirmed(
                        runtime, call, runtime.Describe(call));
                    AssertEqual(ToolExecutionOutcome.Unknown, unknown.Outcome,
                        "divergent chart read-back is unknown");
                    AssertEqual("excel_chart_verification_failed",
                        (string)JObject.Parse(unknown.Result.DataJson)["code"],
                        "divergent chart read-back keeps the precise code");
                });
        }

        private static void ExcelChartUsesBoundDocumentScope()
        {
            WithTempPaths(paths =>
            {
                using (var dispatcher = new OfficeStaDispatcher())
                {
                    var document = new BoundTestDocument
                    {
                        StableId = "bound-excel-chart",
                        IsAlive = true
                    };
                    var sessionPort = new BoundTestOfficeSession(
                        dispatcher, document, "bound-runtime-chart", new object());
                    var inner = FakeOfficeAdapter.ForHost("Excel");
                    var host = new BoundTestOfficeAdapter(sessionPort, inner);
                    var ownerSta = false;
                    host.BeforeRead = operation =>
                    {
                        if (operation == FakeOfficeAdapter.ExcelChartApplyOperation)
                            ownerSta = dispatcher.CheckAccess;
                    };
                    var executor = new OfficeToolExecutor(host,
                        new VbaJournalStore(paths), new SkillStore(paths),
                        new ToolStore(paths), paths: paths);
                    var chat = new ChatSession
                    {
                        Host = "Excel",
                        DocumentKey = "bound-excel-chart",
                        DocumentTitle = "Bound.xlsx"
                    };
                    var tools = OfficeToolCatalog.ForHost(host.HostName)
                        .Concat(executor.GetControllerTools()).ToList();
                    var result = executor.ExecuteManual(Command(
                        ExcelChartToolIds.UpsertChart,
                        "sheet", "Data", "chartName", "BoundChart",
                        "sourceRange", "A1:B6"),
                        tools, new AppSettings(), false, true, chat);
                    AssertTrue(result.Success && ownerSta,
                        "chart mutation stays on the bound document owner STA");

                    var dispatched = inner.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelChartApplyOperation);
                    dispatcher.Invoke(() => document.IsAlive = false);
                    var closed = executor.ExecuteManual(Command(
                        ExcelChartToolIds.DeleteChart,
                        "sheet", "Data", "chartName", "BoundChart"),
                        tools, new AppSettings(), false, true, chat);
                    AssertEqual("active_document_changed", closed.ErrorCode,
                        "closed bound workbook is rejected before chart mutation");
                    AssertEqual(dispatched,
                        inner.ExcelBackendCalls.Count(operation =>
                            operation == FakeOfficeAdapter.ExcelChartApplyOperation),
                        "closed workbook never reaches chart apply");
                }
            });
        }

        private static NativeToolRuntimeAdapter ExcelChartRuntime(
            OfficeToolExecutor executor,
            FakeOfficeAdapter adapter)
        {
            return executor.CreateNativeRuntime(
                NewSession(adapter),
                OfficeToolCatalog.ForHost(adapter.HostName).Where(tool =>
                    ExcelChartToolIds.Owns(tool.Id)),
                new AppSettings(), "agent", false);
        }
    }
}
