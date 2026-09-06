using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ExcelReadUsesExactNativeOwnership()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var inspectDefinition = tools.Single(tool => tool.Id == ExcelReadToolIds.Inspect);
                var runtime = executor.CreateNativeRuntime(session, new[] { inspectDefinition }, new AppSettings(), "agent", false);
                AssertTrue(runtime.Describe(new ToolCall("inspect", ExcelReadToolIds.Inspect, "{\"kind\":\"sheets\"}")) != null,
                    "composed inspect handler is described");
                AssertTrue(!tools.Any(tool => tool.Id == "excel.read_range") &&
                    DirectToolBindingCatalog.Resolve("excel.read_range") == null &&
                    runtime.Describe(new ToolCall("range", "excel.read_range", "{}")) == null,
                    "static ownership does not create a handler absent from the captured catalog");

                adapter.ExcelBackendCalls.Clear();
                var inspect = executor.ExecuteManual(Command(ExcelReadToolIds.Inspect, "kind", "sheets"),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(inspect.Success, "native inspect succeeds");
                AssertEqual("sheets", (string)JObject.Parse(inspect.DataJson)["kind"], "inspect returns canonical selector");
                AssertEqual(1, adapter.ExcelBackendCalls.Count(operation =>
                    operation == FakeOfficeAdapter.ExcelInspectOperation),
                    "inspect reaches one direct typed backend");

                adapter.ExcelBackendCalls.Clear();
                var range = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId,
                    "target", "Excel range: Data!A1:B4", "representation", "text"),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(range.Success, "native range read succeeds");
                var rangeJson = JObject.Parse(range.DataJson);
                AssertEqual(8, JArray.Parse((string)rangeJson["text"]).SelectMany(row => row).Count(), "range returns all cells");
                AssertTrue(range.ModelResourceRefs.Any(reference => reference.IsExact), "read carries exact refs");
                AssertEqual(1, adapter.ExcelBackendCalls.Count(operation =>
                    operation == FakeOfficeAdapter.ExcelRangeReadOperation),
                    "range reaches one direct typed backend");

            });

            WithTempPaths(paths =>
            {
                using (var dispatcher = new OfficeStaDispatcher())
                {
                    var document = new BoundTestDocument { StableId = "bound-excel", IsAlive = true };
                    var boundSession = new BoundTestOfficeSession(dispatcher, document, "bound-runtime", new object());
                    var inner = FakeOfficeAdapter.ForHost("Excel");
                    var host = new BoundTestOfficeAdapter(boundSession, inner);
                    var ownerSta = false;
                    host.BeforeRead = toolId =>
                    {
                        if (toolId == FakeOfficeAdapter.ExcelInspectOperation) ownerSta = dispatcher.CheckAccess;
                    };
                    var executor = new OfficeToolExecutor(host, new VbaJournalStore(paths),
                        new SkillStore(paths), new ToolStore(paths), paths: paths);
                    var chat = new ChatSession
                    {
                        Host = "Excel", DocumentKey = "bound-excel", DocumentTitle = "Bound.xlsx"
                    };
                    var tools = OfficeToolCatalog.ForHost(host.HostName).Concat(executor.GetControllerTools()).ToList();
                    var result = executor.ExecuteManual(Command(ExcelReadToolIds.Inspect, "kind", "sheets"),
                        tools, new AppSettings(), false, false, chat);
                    AssertTrue(result.Success, "native read succeeds against a bound document session");
                    AssertTrue(ownerSta, "native backend dispatch runs on the bound document owner STA");
                    inner.AddExcelTableForTest("Data", "A1:B4", "Sales", true, "");
                    var tableOwnerSta = false;
                    host.BeforeRead = operation => { if (operation == FakeOfficeAdapter.ExcelRangeReadOperation) tableOwnerSta = dispatcher.CheckAccess; };
                    var tableRead = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId,
                        "target", "Excel table: Sales", "representation", "text"), tools, new AppSettings(), false, false, chat);
                    AssertTrue(tableRead.Success && tableOwnerSta, "named resolution/capture uses the same bound owner STA");
                    var dispatched = inner.ExcelBackendCalls.Count;
                    dispatcher.Invoke(() => document.IsAlive = false);
                    var closed = executor.ExecuteManual(Command(ExcelReadToolIds.Inspect, "kind", "sheets"),
                        tools, new AppSettings(), false, false, chat);
                    AssertEqual("active_document_changed", closed.ErrorCode,
                        "closed bound workbook is rejected before dispatch");
                    AssertEqual(dispatched, inner.ExcelBackendCalls.Count,
                        "closed bound workbook never reaches the backend");
                    var closedTable = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId,
                        "target", "Excel table: Sales", "representation", "text"), tools, new AppSettings(), false, false, chat);
                    AssertTrue(!closedTable.Success, "closed workbook cannot resolve a current table");
                    AssertEqual(dispatched, inner.ExcelBackendCalls.Count, "closed table read performs no backend work");
                }
            });
        }

        private static void ExcelRangeReadsRetainExactEvidence()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                executor.BindResourceAuthority(session);
                var tools = OfficeToolCatalog.ForHost("Excel").Concat(executor.GetControllerTools()).ToList();
                var runtime = executor.CreateNativeRuntime(session, tools, new AppSettings(), "agent", false);
                adapter.SetExcelCellForTest("Data", "A1", new string('a', 20000));
                adapter.SetExcelCellForTest("Data", "B1", new string('b', 20000));
                var first = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = "Excel range: Data!A1:B4", ["representation"] = "structure" });
                AssertEqual(ToolExecutionOutcome.Ok, first.Outcome, "profile uses generic native resource read");
                var evidence = first.ResourceEvidence.Single();
                AssertTrue(evidence.Resource.IsExact && evidence.Payload != null && evidence.Complete,
                    "complete profile records exact CAS-backed evidence");
                var text = (string)JObject.Parse(first.Result.DataJson)["text"];
                AssertTrue(text.Length > 32000, "fixture exercises internally pinned continuation");
                AssertEqual(1, adapter.ExcelBackendCalls.Count(call => call == FakeOfficeAdapter.ExcelRangeReadOperation),
                    "all profile pages share one bounded physical capture");
                adapter.SetExcelCellForTest("Data", "B2", 999);
                var next = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = "Excel range: Data!A1:B4", ["representation"] = "structure" });
                AssertEqual(ToolExecutionOutcome.Ok, next.Outcome, "fresh profile discovers drift");
                AssertTrue(evidence.Resource.Revision != next.ResourceEvidence.Single().Resource.Revision,
                    "profile drift advances logical revision");
                AssertEqual(EvidenceState.Superseded, new EvidenceStateReducer().Reduce(evidence,
                    executor.ResourceAuthority.CaptureMany(new[] { evidence.ScopeId })).State,
                    "old profile cannot remain current model evidence");
                var reads = adapter.ExcelBackendCalls.Count;
                var historical = executor.ResourceGateway.Read(session, new ResourceReadRequest {
                    Reference = evidence.Resource, Representation = "structure", MaxChars = 32000 }).Result;
                AssertEqual(text.Substring(0, 32000), historical.Text, "historical profile uses retained exact bytes");
                AssertEqual(reads, adapter.ExcelBackendCalls.Count, "historical profile performs no Office I/O");
                string error;
                AssertTrue(!ModelToolResultProjection.ValidateAcceptedCall(new ToolCall("old", "excel.read_range", "{}"), out error),
                    "removed calls are explicitly rejected, never translated or replayed");
                var removed = executor.ExecuteManual(Command("excel.read_range"), tools, new AppSettings(), false, false, session);
                AssertEqual("unknown_tool", removed.ErrorCode, "removed reader has no manual fallback");
                AssertEqual(reads, adapter.ExcelBackendCalls.Count, "removed reader cannot dispatch");
            });
        }

        private static void ExcelNamedTableResources()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var session = NewSession(adapter); executor.BindResourceAuthority(session);
                adapter.AddExcelTableForTest("Data", "C1:D2", "Sales", true, "");
                // Equal cells at two extents isolate relocation from value drift.
                foreach (var cell in new[] { "C1", "D1", "C2", "D2", "E1", "F1", "E2", "F2" })
                    adapter.SetExcelCellForTest("Data", cell, "same");
                var gateway = executor.ResourceGateway;
                var listed = gateway.List(session, "excel", ExcelResourceProvider.TableKind, null, 20).Items.Single();
                AssertEqual("Sales", listed.Title, "table discovery uses its semantic name");
                AssertEqual(0, adapter.ExcelBackendCalls.Count(item => item == FakeOfficeAdapter.ExcelRangeReadOperation), "discovery does not read cells");
                AssertTrue(gateway.Find(session, "Sales", "document").Items.Any(item => item.Target == "Excel table: Sales"), "generic find discovers named tables");
                var tools = OfficeToolCatalog.ForHost("Excel").Concat(executor.GetControllerTools()).ToList();
                var runtime = executor.CreateNativeRuntime(session, tools, new AppSettings(), "agent", false);
                var first = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = "Excel table: sales", ["representation"] = "text" });
                AssertEqual(ToolExecutionOutcome.Ok, first.Outcome, "generic native reader accepts a named table case-insensitively");
                var evidence = first.ResourceEvidence.Single();
                AssertEqual(listed.Reference.Uri, evidence.Resource.Uri, "discovery and read share one named identity");
                var original = (string)JObject.Parse(first.Result.DataJson)["text"];
                AssertEqual("C1:D2", (string)JObject.Parse(original)["address"], "captured source includes physical extent");
                adapter.SetExcelTableRangeForTest("Data", "Sales", "E1:F2");
                var second = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = "Excel table: Sales", ["representation"] = "text" });
                AssertEqual(ToolExecutionOutcome.Ok, second.Outcome, "fresh read follows the table's new extent");
                var fresh = second.ResourceEvidence.Single();
                AssertEqual(evidence.Resource.Uri, fresh.Resource.Uri, "relocation preserves logical identity");
                AssertTrue(evidence.Resource.Revision != fresh.Resource.Revision, "equal cells at a new extent still advance the exact revision");
                AssertEqual(EvidenceState.Superseded, new EvidenceStateReducer().Reduce(evidence,
                    executor.ResourceAuthority.CaptureMany(new[] { evidence.ScopeId })).State, "old extent is historical, not current");
                var calls = adapter.ExcelBackendCalls.Count;
                var historical = gateway.Read(session, new ResourceReadRequest { Reference = evidence.Resource, Representation = "text", MaxChars = 32000 }).Result;
                AssertEqual(original, historical.Text, "historical table does not resolve its current address");
                AssertEqual(calls, adapter.ExcelBackendCalls.Count, "historical source performs no Office I/O");
                using (var data = new ResourceDataPlaneService(gateway))
                {
                    var opened = data.Open(session, "workspace", evidence.Resource, "records", "$.values");
                    AssertEqual(evidence.Resource.Revision, opened.Descriptor.Reference.Revision, "HTML records stay on exact named source");
                    AssertEqual(calls, adapter.ExcelBackendCalls.Count, "historical structural projection also avoids Office");
                }
                adapter.SetExcelTableRangeForTest("Data", "Sales", "E1:F3");
                var rows = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = "Excel table: Sales", ["representation"] = "records", ["path"] = "$.values", ["limit"] = 2 });
                AssertEqual(ToolExecutionOutcome.Ok, rows.Outcome, "bounded model records follow table resize through the same provider");
                var formulas = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = "Excel table: Sales", ["representation"] = "formulas" });
                AssertEqual(ToolExecutionOutcome.Ok, formulas.Outcome, "named table retains formula view");
                System.IO.File.Delete(executor.Payloads.PathFor(evidence.Payload.Sha256));
                calls = adapter.ExcelBackendCalls.Count;
                AssertEqual("RESOURCE_SNAPSHOT_UNAVAILABLE", RuntimeThrows<ResourceRequestException>(() => gateway.Read(session,
                    new ResourceReadRequest { Reference = evidence.Resource, Representation = "text" })).ErrorCode, "missing historical CAS never falls forward");
                AssertEqual(calls, adapter.ExcelBackendCalls.Count, "missing CAS does not resolve or capture current table");
            });
        }

        private static void ExcelNamedTableAdmission()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var session = NewSession(adapter); executor.BindResourceAuthority(session);
                adapter.AddExcelTableForTest("Data", "A1:B4", "Sales", true, "");
                var gateway = executor.ResourceGateway;
                var reference = gateway.List(session, "excel", ExcelResourceProvider.TableKind, null, 20).Items.Single().Reference;
                foreach (var defect in new[] { "truncated", "duplicate", "missing", "oversized", "external-address" })
                {
                    var table = new ExcelTableSnapshot { Name = "Sales", Sheet = "Data", Range = "A1:B4", Rows = 3, Columns = 2 };
                    if (defect == "oversized") table.Range = "A1:Z10000";
                    if (defect == "external-address") table.Range = "[other.xlsx]Data!A1:B4";
                    var tables = defect == "missing" ? new List<ExcelTableSnapshot>() : new List<ExcelTableSnapshot> { table };
                    if (defect == "duplicate") tables.Add(table);
                    adapter.QueueExcelInspectSnapshot(new ExcelInspectSnapshot { Kind = "tables", Tables = tables,
                        ReturnedCount = tables.Count, Truncated = defect == "truncated" });
                    var reads = adapter.ExcelBackendCalls.Count(item => item == FakeOfficeAdapter.ExcelRangeReadOperation);
                    RuntimeThrows<ResourceRequestException>(() => gateway.Read(session, new ResourceReadRequest { Reference = reference, Representation = "text" }));
                    AssertEqual(reads, adapter.ExcelBackendCalls.Count(item => item == FakeOfficeAdapter.ExcelRangeReadOperation), defect + " refuses before cell capture");
                    if (defect == "truncated")
                    {
                        adapter.QueueExcelInspectSnapshot(new ExcelInspectSnapshot { Kind = "tables", Tables = tables, ReturnedCount = tables.Count, Truncated = true });
                        RuntimeThrows<ResourceRequestException>(() => gateway.List(session, "excel", ExcelResourceProvider.TableKind, null, 20));
                    }
                }
                var foreign = NewSession(adapter); foreign.DocumentKey = "foreign-workbook";
                RuntimeThrows<InvalidOperationException>(() => gateway.Read(foreign, new ResourceReadRequest { Reference = reference, Representation = "text" }));
            });
        }

        private static void ExcelReadSelectorsAreCanonical()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.AddExcelTableForTest(
                    "Data", "A1:B4", "SalesTable", true, string.Empty);
                adapter.AddExcelChartForTest(
                    "Data", "A1:B4", "SalesChart");
                var session = NewSession(adapter);
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                foreach (var kind in new[] { "workbook", "sheets", "charts", "tables", "names", "shapes" })
                {
                    var result = executor.ExecuteManual(Command(ExcelReadToolIds.Inspect, "kind", kind),
                        tools, new AppSettings(), false, false, session);
                    AssertTrue(result.Success, "inspect selector succeeds: " + kind);
                    var json = JObject.Parse(result.DataJson);
                    AssertEqual(kind, (string)json["kind"], "selector is explicit: " + kind);
                    AssertTrue(json["returnedCount"] != null && json["truncated"] != null,
                        "selector exposes bound evidence: " + kind);
                }

                var chart = executor.ExecuteManual(Command(ExcelReadToolIds.Inspect,
                    "kind", "charts", "sheet", "Data", "chartName", "SalesChart"),
                    tools, new AppSettings(), false, false, session);
                AssertEqual("SalesChart", (string)JObject.Parse(chart.DataJson).SelectToken("item.name"),
                    "exact chart returns one typed item");

                foreach (var content in new[] { "values", "formulas", "profile" })
                {
                    var result = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId,
                        "target", "Excel range: Data!A1:B4", "representation",
                        content == "profile" ? "structure" : content == "values" ? "text" : "formulas"),
                        tools, new AppSettings(), false, false, session);
                    AssertTrue(result.Success, "range representation succeeds: " + content);
                    var projection = JObject.Parse(result.DataJson);
                    AssertTrue((bool)projection["complete"], "complete resource view");
                    var json = content == "profile" ? JObject.Parse((string)projection["text"]) : null;
                    if (content != "profile") {
                        var matrix = JArray.Parse((string)projection["text"]);
                        AssertEqual(4, matrix.Count, "range row count");
                        AssertEqual(2, matrix[0].Count(), "range column count");
                    }
                    if (content == "profile")
                    {
                        AssertTrue(json["blankCells"] != null && json["formulaCells"] != null && json["sample"] != null,
                            "profile is computed by the typed owner");
                    }
                }

                var empty = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId,
                    "target", "Excel range: Data!D1:E2", "representation", "text"),
                    tools, new AppSettings(), false, false, session);
                var emptyJson = JObject.Parse(empty.DataJson);
                AssertTrue(empty.Success && JArray.Parse((string)emptyJson["text"]).SelectMany(row => row).All(value => string.IsNullOrEmpty((string)value)),
                    "an explicit empty-cell range remains a successful snapshot");

                var metadata = new ExcelReadService(new StubExcelReadBackend
                {
                    InspectResult = new ExcelInspectSnapshot
                    {
                        Kind = "names", ReturnedCount = 1, Truncated = false,
                        Names = new List<ExcelNameSnapshot>
                        {
                            new ExcelNameSnapshot { Name = "Sales", RefersTo = "=Data!$A$1:$B$4", Sheet = "Data", Address = "$A$1:$B$4" }
                        }
                    }
                }).Inspect("names", string.Empty, string.Empty);
                var name = (JObject)JObject.Parse(metadata.DataJson)["items"][0];
                AssertTrue(name["value"] == null && (string)name["address"] == "$A$1:$B$4",
                    "defined names expose metadata without range Value2");
            });
        }

        private static void ExcelReadBoundsPrecedeMaterialization()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var oversized = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId,
                    "target", "Excel range: Data!A1:XFD1048576", "representation", "text"),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(!oversized.Success, "oversized range fails");
                AssertEqual("RESOURCE_SNAPSHOT_TOO_LARGE", oversized.ErrorCode, "oversized range keeps exact code");
                AssertEqual(0, adapter.ExcelReadMaterializationCount,
                    "host checks dimensions before values/formulas materialization");

                adapter.SeedExcelSheets(205);
                var bounded = executor.ExecuteManual(Command(ExcelReadToolIds.Inspect, "kind", "sheets"),
                    tools, new AppSettings(), false, false, session);
                var boundedJson = JObject.Parse(bounded.DataJson);
                AssertEqual(ExcelReadService.MaxInspectItems, boundedJson["returnedCount"].Value<int>(),
                    "inspect output is capped");
                AssertTrue(boundedJson["truncated"].Value<bool>(), "inspect reports truncation");

                adapter.ExcelBackendCalls.Clear();
                var wrongSession = NewSession(adapter);
                wrongSession.DocumentKey = "other-document";
                var wrongTarget = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId,
                    "target", "Excel range: Data!A1"), tools, new AppSettings(), false, false, wrongSession);
                AssertEqual("active_document_changed", wrongTarget.ErrorCode,
                    "native handler checks the chat document expectation");
                AssertEqual(0, adapter.ExcelBackendCalls.Count(operation =>
                    operation == FakeOfficeAdapter.ExcelRangeReadOperation),
                    "wrong-target refusal occurs before backend dispatch");
            });

            var inconsistent = RuntimeThrows<ExcelReadBackendException>(() => new ExcelReadService(new StubExcelReadBackend
            {
                RangeResult = new ExcelRangeSnapshot
                {
                    Sheet = "Data", Address = "A1:B2", Rows = 2, Columns = 2,
                    CellCount = 5, Values = Matrix(2, 2)
                }
            }).CaptureRange("Data", "A1:B2", "values"));
            AssertEqual("excel_read_snapshot_invalid", inconsistent.ErrorCode,
                "inconsistent backend snapshot fails closed");

            var missingCollection = new ExcelReadService(new StubExcelReadBackend
            {
                InspectResult = new ExcelInspectSnapshot { Kind = "sheets", ReturnedCount = 0 }
            }).Inspect("sheets", string.Empty, string.Empty);
            AssertTrue(!missingCollection.Success, "missing collection is not projected as an empty workbook");
            AssertEqual("excel_read_snapshot_invalid", missingCollection.ErrorCode,
                "missing collection fails closed");

            var invalidSelectorArguments = new ExcelReadService(new StubExcelReadBackend())
                .Inspect("workbook", "Data", string.Empty);
            AssertEqual("excel_inspect_arguments_invalid", invalidSelectorArguments.ErrorCode,
                "domain selector arguments match the hardened catalog variants");

            var nullSeries = new ExcelReadService(new StubExcelReadBackend
            {
                InspectResult = new ExcelInspectSnapshot
                {
                    Kind = "charts", ReturnedCount = 1, Charts = new List<ExcelChartSnapshot>
                    {
                        new ExcelChartSnapshot
                        {
                            Name = "Chart 1",
                            Series = new List<ExcelChartSeriesSnapshot> { null }
                        }
                    }
                }
            }).Inspect("charts", string.Empty, string.Empty);
            AssertEqual("excel_read_snapshot_invalid", nullSeries.ErrorCode,
                "incomplete chart-series metadata fails closed");
        }

        private static void ExcelReadHtmlUsesSharedRoute()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var bind = Command(HtmlWorkspaceToolCatalog.BindDataToolId,
                    "name", "sales",
                    "target", "Excel range: Data!A1:B4", "view", "text", "policy", "head");
                var bound = executor.ExecuteManual(bind, tools, new AppSettings(), false, false, session);
                AssertTrue(bound.Success, "HTML bind succeeds through the typed read route");
                AssertEqual(1, adapter.ExcelBackendCalls.Count(operation =>
                    operation == FakeOfficeAdapter.ExcelRangeReadOperation),
                    "HTML bind captures through the same Gateway provider");

                var refresh = executor.ExecuteManual(Command(HtmlWorkspaceToolCatalog.RefreshDataToolId, "name", "sales"),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(refresh.Success, "HTML refresh succeeds through the same adapter");
                AssertEqual(2, adapter.ExcelBackendCalls.Count(operation =>
                    operation == FakeOfficeAdapter.ExcelRangeReadOperation),
                    "bind and refresh share one backend route");
            });

            WithTempPaths(paths =>
            {
                using (var dispatcher = new OfficeStaDispatcher())
                {
                    var document = new BoundTestDocument
                    {
                        StableId = "bound-html-excel",
                        IsAlive = true
                    };
                    var documentSession = new BoundTestOfficeSession(
                        dispatcher, document, "bound-html-runtime", new object());
                    var inner = FakeOfficeAdapter.ForHost("Excel");
                    var host = new BoundTestOfficeAdapter(documentSession, inner);
                    var ownerStaReads = 0;
                    host.BeforeRead = operation =>
                    {
                        if (operation != FakeOfficeAdapter.ExcelRangeReadOperation)
                            return;
                        AssertTrue(dispatcher.CheckAccess,
                            "HTML data source read runs on the bound document owner STA");
                        ownerStaReads += 1;
                    };
                    var executor = new OfficeToolExecutor(
                        host, new VbaJournalStore(paths), new SkillStore(paths),
                        new ToolStore(paths), paths: paths);
                    var session = new ChatSession
                    {
                        Host = "Excel",
                        DocumentKey = "bound-html-excel",
                        DocumentTitle = "Bound HTML.xlsx"
                    };
                    var tools = OfficeToolCatalog.ForHost(host.HostName)
                        .Concat(executor.GetControllerTools()).ToList();
                    var bind = Command(HtmlWorkspaceToolCatalog.BindDataToolId,
                        "name", "sales",
                        "target", "Excel range: Data!A1:B4", "view", "text", "policy", "head");

                    var bound = executor.ExecuteManual(
                        bind, tools, new AppSettings(), false, false, session);
                    AssertTrue(bound.Success,
                        "bound HTML bind succeeds through owner STA dispatch");
                    var refreshed = executor.ExecuteManual(
                        Command(HtmlWorkspaceToolCatalog.RefreshDataToolId,
                            "name", "sales"),
                        tools, new AppSettings(), false, false, session);
                    AssertTrue(refreshed.Success,
                        "bound HTML refresh succeeds through owner STA dispatch");
                    AssertEqual(2, ownerStaReads,
                        "bind and refresh each dispatch once to owner STA");
                }
            });
        }

        private static List<List<object>> Matrix(int rows, int columns)
        {
            return Enumerable.Range(0, rows)
                .Select(row => Enumerable.Range(0, columns).Select(column => (object)(row + ":" + column)).ToList())
                .ToList();
        }

        private sealed class StubExcelReadBackend : IExcelReadBackend
        {
            internal ExcelInspectSnapshot InspectResult { get; set; }
            internal ExcelRangeSnapshot RangeResult { get; set; }

            public ExcelInspectSnapshot Inspect(ExcelInspectRequest request) { return InspectResult; }
            public ExcelRangeSnapshot ReadRange(ExcelRangeReadRequest request) { return RangeResult; }
        }
    }
}
