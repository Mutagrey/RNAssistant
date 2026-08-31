using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.Office.Runtime;
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
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var inspectDefinition = tools.Single(tool => tool.Id == ExcelReadToolIds.Inspect);
                var runtime = executor.CreateNativeRuntime(session, new[] { inspectDefinition }, new AppSettings(), "agent", false);
                AssertTrue(runtime.Describe(new ToolCall("inspect", ExcelReadToolIds.Inspect, "{\"kind\":\"sheets\"}")) != null,
                    "composed inspect handler is described");
                AssertTrue(runtime.Describe(new ToolCall("range", ExcelReadToolIds.ReadRange, "{}")) == null,
                    "static ownership does not create a handler absent from the captured catalog");

                adapter.ExcelBackendCalls.Clear();
                var inspect = executor.Execute(Command(ExcelReadToolIds.Inspect, "kind", "sheets"),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(inspect.Success, "native inspect succeeds");
                AssertEqual("sheets", (string)JObject.Parse(inspect.DataJson)["kind"], "inspect returns canonical selector");
                AssertEqual(1, adapter.ExcelBackendCalls.Count(operation =>
                    operation == FakeOfficeAdapter.ExcelInspectOperation),
                    "inspect reaches one direct typed backend");
                AssertEqual(0, adapter.Executed.Count(command => command.ToolId == ExcelReadToolIds.Inspect),
                    "public inspect never reaches the host adapter");

                adapter.ExcelBackendCalls.Clear();
                var range = executor.Execute(Command(ExcelReadToolIds.ReadRange,
                    "sheet", "Data", "address", "A1:B4", "content", "values"),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(range.Success, "native range read succeeds");
                var rangeJson = JObject.Parse(range.DataJson);
                AssertEqual(8L, rangeJson["cellCount"].Value<long>(), "range reports exact cell count");
                AssertEqual(1, adapter.ExcelBackendCalls.Count(operation =>
                    operation == FakeOfficeAdapter.ExcelRangeReadOperation),
                    "range reaches one direct typed backend");

                AssertEqual("excel_public_read_moved",
                    adapter.ExecuteTool(Command(ExcelReadToolIds.Inspect, "kind", "sheets")).ErrorCode,
                    "host adapter cannot execute the moved public id");
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
                    var tools = host.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                    var result = executor.Execute(Command(ExcelReadToolIds.Inspect, "kind", "sheets"),
                        tools, new AppSettings(), false, false, chat);
                    AssertTrue(result.Success, "native read succeeds against a bound document session");
                    AssertTrue(ownerSta, "native backend dispatch runs on the bound document owner STA");
                    var dispatched = inner.ExcelBackendCalls.Count;
                    dispatcher.Invoke(() => document.IsAlive = false);
                    var closed = executor.Execute(Command(ExcelReadToolIds.Inspect, "kind", "sheets"),
                        tools, new AppSettings(), false, false, chat);
                    AssertEqual("active_document_changed", closed.ErrorCode,
                        "closed bound workbook is rejected before dispatch");
                    AssertEqual(dispatched, inner.ExcelBackendCalls.Count,
                        "closed bound workbook never reaches the backend");
                }
            });
        }

        private static void ExcelReadSelectorsAreCanonical()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.ExecuteTool(Command("excel.add_table", "sheet", "Data", "name", "SalesTable"));
                adapter.ExecuteTool(Command("excel.upsert_chart", "sheet", "Data", "chartName", "SalesChart"));
                var session = NewSession(adapter);
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                foreach (var kind in new[] { "workbook", "sheets", "charts", "tables", "names", "shapes" })
                {
                    var result = executor.Execute(Command(ExcelReadToolIds.Inspect, "kind", kind),
                        tools, new AppSettings(), false, false, session);
                    AssertTrue(result.Success, "inspect selector succeeds: " + kind);
                    var json = JObject.Parse(result.DataJson);
                    AssertEqual(kind, (string)json["kind"], "selector is explicit: " + kind);
                    AssertTrue(json["returnedCount"] != null && json["truncated"] != null,
                        "selector exposes bound evidence: " + kind);
                }

                var chart = executor.Execute(Command(ExcelReadToolIds.Inspect,
                    "kind", "charts", "sheet", "Data", "chartName", "SalesChart"),
                    tools, new AppSettings(), false, false, session);
                AssertEqual("SalesChart", (string)JObject.Parse(chart.DataJson).SelectToken("item.name"),
                    "exact chart returns one typed item");

                foreach (var content in new[] { "values", "formulas", "profile" })
                {
                    var result = executor.Execute(Command(ExcelReadToolIds.ReadRange,
                        "sheet", "Data", "address", "A1:B4", "content", content),
                        tools, new AppSettings(), false, false, session);
                    AssertTrue(result.Success, "range representation succeeds: " + content);
                    var json = JObject.Parse(result.DataJson);
                    AssertEqual(content, (string)json["content"], "range representation is explicit");
                    AssertEqual(4, json["rows"].Value<int>(), "range row count");
                    AssertEqual(2, json["columns"].Value<int>(), "range column count");
                    if (content == "profile")
                    {
                        AssertTrue(json["blankCells"] != null && json["formulaCells"] != null && json["sample"] != null,
                            "profile is computed by the typed owner");
                    }
                }

                var empty = executor.Execute(Command(ExcelReadToolIds.ReadRange,
                    "sheet", "Data", "address", "D1:E2", "content", "values"),
                    tools, new AppSettings(), false, false, session);
                var emptyJson = JObject.Parse(empty.DataJson);
                AssertTrue(empty.Success && emptyJson["values"].SelectMany(row => row).All(value => string.IsNullOrEmpty((string)value)),
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
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var oversized = executor.Execute(Command(ExcelReadToolIds.ReadRange,
                    "sheet", "Data", "address", "A1:XFD1048576", "content", "values"),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(!oversized.Success, "oversized range fails");
                AssertEqual("excel_range_too_large", oversized.ErrorCode, "oversized range keeps exact code");
                AssertEqual(0, adapter.ExcelReadMaterializationCount,
                    "host checks dimensions before values/formulas materialization");

                adapter.SeedExcelSheets(205);
                var bounded = executor.Execute(Command(ExcelReadToolIds.Inspect, "kind", "sheets"),
                    tools, new AppSettings(), false, false, session);
                var boundedJson = JObject.Parse(bounded.DataJson);
                AssertEqual(ExcelReadService.MaxInspectItems, boundedJson["returnedCount"].Value<int>(),
                    "inspect output is capped");
                AssertTrue(boundedJson["truncated"].Value<bool>(), "inspect reports truncation");

                adapter.ExcelBackendCalls.Clear();
                var wrongSession = NewSession(adapter);
                wrongSession.DocumentKey = "other-document";
                var wrongTarget = executor.Execute(Command(ExcelReadToolIds.ReadRange,
                    "sheet", "Data", "address", "A1"), tools, new AppSettings(), false, false, wrongSession);
                AssertEqual("active_document_changed", wrongTarget.ErrorCode,
                    "native handler checks the chat document expectation");
                AssertEqual(0, adapter.ExcelBackendCalls.Count(operation =>
                    operation == FakeOfficeAdapter.ExcelRangeReadOperation),
                    "wrong-target refusal occurs before backend dispatch");
            });

            var inconsistent = new ExcelReadService(new StubExcelReadBackend
            {
                RangeResult = new ExcelRangeSnapshot
                {
                    Sheet = "Data", Address = "A1:B2", Rows = 2, Columns = 2,
                    CellCount = 5, Values = Matrix(2, 2)
                }
            }).ReadRange("Data", "A1:B2", "values");
            AssertTrue(!inconsistent.Success, "domain rejects inconsistent backend dimensions");
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
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var bind = Command(HtmlArtifactToolExecutor.BindDataToolId,
                    "dataName", "sales",
                    "sourceTool", ExcelReadToolIds.ReadRange,
                    "sourceArguments", new JObject
                    {
                        ["sheet"] = "Data", ["address"] = "A1:B4", ["content"] = "values"
                    },
                    "transform", "table", "headers", "firstRow");
                var bound = executor.Execute(bind, tools, new AppSettings(), false, false, session);
                AssertTrue(bound.Success, "HTML bind succeeds through the typed read route");
                AssertEqual(1, adapter.ExcelBackendCalls.Count(operation =>
                    operation == FakeOfficeAdapter.ExcelRangeReadOperation),
                    "HTML bind uses the direct backend once");
                AssertEqual(0, adapter.Executed.Count(command => command.ToolId == ExcelReadToolIds.ReadRange),
                    "HTML bind never dispatches the public id to the host");

                var refresh = executor.Execute(Command(HtmlArtifactToolExecutor.RefreshDataToolId, "name", "sales"),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(refresh.Success, "HTML refresh succeeds through the same adapter");
                AssertEqual(2, adapter.ExcelBackendCalls.Count(operation =>
                    operation == FakeOfficeAdapter.ExcelRangeReadOperation),
                    "bind and refresh share one backend route");
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
