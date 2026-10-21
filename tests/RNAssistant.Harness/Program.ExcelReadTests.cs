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
        private static void ExcelColumnAnalysisScansSelectedCells()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                const string sheet = "Big O'Brien";
                adapter.AddExcelSheetForTest(sheet);
                adapter.SetExcelCell(sheet, "A1", 10d);
                adapter.SetExcelCell(sheet, "A1024", 20d);
                adapter.SetExcelCell(sheet, "A1025", 30d);
                adapter.SetExcelCell(sheet, "A200001", 40d);
                adapter.SetExcelCell(sheet, "A500", "12");
                adapter.SetExcelCell(sheet, "A800", true);
                adapter.SetExcelCell(sheet, "XFD200001", 3d);
                adapter.SetExcelCell(sheet, "XFD2", new string('z', 50000));
                var session = NewSession(adapter);
                var tools = OfficeToolCatalog.ForHost("Excel").Concat(executor.GetControllerTools()).ToList();
                var result = executor.ExecuteManual(Command(ExcelReadToolIds.SummarizeColumns,
                    "sheet", sheet, "address", "A1:XFD200001", "columns", new JArray("A", "XFD")),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(result.Success, "large selected-column scan succeeds: " + result.Message);
                var data = JObject.Parse(result.DataJson);
                AssertTrue((bool)data["complete"] && (int)data["rowsScanned"] == 200001 &&
                    (long)data["cellsScanned"] == 400002 && (string)data["consistency"] == "sequential-observation",
                    "completion reports all selected cells and the observation semantics");
                var columns = (JArray)data["columns"];
                AssertEqual(2, columns.Count, "only requested columns reach the result");
                var first = columns[0]; var far = columns[1];
                AssertTrue((string)first["column"] == "A" && (int)first["numericCells"] == 4 &&
                    (int)first["otherCells"] == 2 && (int)first["emptyCells"] == 199995 &&
                    (double)first["sum"] == 100d && (double)first["average"] == 25d &&
                    (double)first["min"] == 10d && (double)first["max"] == 40d,
                    "numeric values are exact across chunk boundaries; numeric text and booleans are not silently coerced");
                AssertTrue((string)far["column"] == "XFD" && (int)far["numericCells"] == 1 &&
                    (int)far["otherCells"] == 1 && (int)far["emptyCells"] == 199999 &&
                    (double)far["sum"] == 3d, "the far column is scanned without intervening columns");
                AssertTrue(result.DataJson.Length < 1200 && !result.DataJson.Contains(new string('z', 20)),
                    "long source cell text does not enter the model result");
                var projected = ModelToolResultProjection.Project(AgentJsonProtocol.CreateToolResultMessage(
                    new ToolInvocation { ToolCallId = "selected_column_analysis", ToolId = ExcelReadToolIds.SummarizeColumns },
                    RNAssistant.Core.Tools.Contracts.ToolResult.Ok(result.Message, result.DataJson), ToolResultRoles.Tool));
                AssertContains(projected.Content, "cellsScanned", "the model sees full scan coverage");
                AssertContains(projected.Content, "numericCells", "the model sees numeric results");
                AssertTrue(!projected.Content.Contains(new string('z', 20)), "projection keeps long cell bodies out");
                AssertTrue(adapter.MaxExcelAnalysisChunkCells <= ExcelColumnAnalysisService.ChunkRows &&
                    adapter.ExcelReadMaterializationCount > 2 && adapter.DocumentSnapshotReadCount == 0,
                    "analysis uses bounded native chunks and no document preview");
                var reads = adapter.ExcelBackendCalls.Count;
                var tooLarge = executor.ExecuteManual(Command(ExcelReadToolIds.SummarizeColumns,
                    "sheet", sheet, "address", "A1:XFD1048576",
                    "columns", new JArray("A", "B", "C", "D", "E", "F", "G", "H")),
                    tools, new AppSettings(), false, false, session);
                AssertEqual("excel_analysis_too_large", tooLarge.ErrorCode, "requested work is bounded before Office access");
                var duplicate = executor.ExecuteManual(Command(ExcelReadToolIds.SummarizeColumns,
                    "sheet", sheet, "address", "A1:A200001", "columns", new JArray("A", "a")),
                    tools, new AppSettings(), false, false, session);
                AssertEqual("excel_analysis_columns_invalid", duplicate.ErrorCode, "duplicate columns fail explicitly");
                AssertEqual(reads, adapter.ExcelBackendCalls.Count, "invalid scans perform no backend reads");
            });

            var metadata = new ExcelRangeSnapshot { Sheet = "Data", Address = "A1:A1025",
                Rows = 1025, Columns = 1, CellCount = 1025 };
            var calls = 0;
            var failing = new StubExcelReadBackend { ReadRangeOverride = request => {
                calls++;
                if (request.Content == "metadata") return metadata;
                if (request.Address == "A1:A1024") return new ExcelRangeSnapshot {
                    Sheet = "Data", Address = request.Address, Rows = 1024, Columns = 1,
                    CellCount = 1024, Values = Matrix(1024, 1) };
                throw new ExcelReadBackendException("later chunk failed", "excel_analysis_chunk_failed", false);
            } };
            var incomplete = new ExcelColumnAnalysisService(failing).Analyze("Data", "A1:A1025",
                new[] { "A" }, System.Threading.CancellationToken.None);
            AssertTrue(!incomplete.Success && incomplete.ErrorCode == "excel_analysis_chunk_failed" && calls == 3 &&
                !incomplete.DataJson.Contains("columns"), "a later capture failure publishes no partial total");

            var driftChecks = 0;
            var drift = new StubExcelReadBackend { ReadRangeOverride = request => {
                if (request.Content == "metadata")
                {
                    driftChecks++;
                    return new ExcelRangeSnapshot { Sheet = driftChecks == 1 ? "Data" : "Renamed", Address = "A1",
                        Rows = 1, Columns = 1, CellCount = 1 };
                }
                return new ExcelRangeSnapshot { Sheet = "Data", Address = "A1", Rows = 1, Columns = 1,
                    CellCount = 1, Values = Matrix(1, 1) };
            } };
            var changed = new ExcelColumnAnalysisService(drift).Analyze("Data", "A1", new[] { "A" },
                System.Threading.CancellationToken.None);
            AssertTrue(!changed.Success && changed.ErrorCode == "excel_analysis_source_changed" && driftChecks == 2,
                "a changed bound sheet at the end cannot become a complete result");

            using (var cancellation = new System.Threading.CancellationTokenSource())
            {
                var cancelledCalls = 0;
                var cancelling = new StubExcelReadBackend { ReadRangeOverride = request => {
                    cancelledCalls++;
                    if (request.Content == "metadata") return metadata;
                    cancellation.Cancel();
                    return new ExcelRangeSnapshot { Sheet = "Data", Address = request.Address,
                        Rows = 1024, Columns = 1, CellCount = 1024, Values = Matrix(1024, 1) };
                } };
                RuntimeThrows<OperationCanceledException>(() => new ExcelColumnAnalysisService(cancelling)
                    .Analyze("Data", "A1:A1025", new[] { "A" }, cancellation.Token));
                AssertEqual(2, cancelledCalls, "cancellation stops the traversal after its first chunk");
            }
        }

        private static void ExcelLargeRangesOfferBoundedOverview()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                var gateway = executor.ResourceGateway;
                var tools = executor.GetControllerTools().ToList();
                const string sheet = "Big O'Brien";
                adapter.AddExcelSheetForTest(sheet);
                adapter.SetExcelCell(sheet, "A1", "unread-body-" + new string('x', 50000));
                var small = ResourceFindProjection.Serialize(gateway.Find(session, null, "document"));
                adapter.SetExcelCell(sheet, "XFD1048576", "tail");
                var found = gateway.Find(session, null, "document");
                var large = found.Items.Single(item => item.Target == "Excel range: 'Big O''Brien'!A1:XFD1048576");
                AssertContains(large.Usage, "structure", "large extents have a useful overview route");
                var discovery = ResourceFindProjection.Serialize(found);
                AssertTrue(discovery.Length < small.Length + 512 && !discovery.Contains("unread-body-"),
                    "discovery size does not grow with the worksheet area or cell contents");
                AssertEqual(0, adapter.DocumentSnapshotReadCount, "browse does not read a document preview");
                AssertEqual(0, adapter.ExcelReadMaterializationCount, "browse does not read worksheet values");

                var read = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId,
                    "target", large.Target, "representation", "structure"), tools, new AppSettings(), false, false, session);
                AssertTrue(read.Success, "full-sheet structure is readable without a full-sheet snapshot: " + read.Message);
                var projection = JObject.Parse(read.DataJson);
                var overviewText = (string)projection["text"];
                var overview = JObject.Parse(overviewText);
                AssertEqual("range-overview", (string)overview["content"], "overview is explicit, not a complete cell profile");
                AssertTrue(!(bool)overview["valuesRead"] && overview["blankCells"] == null && overview["formulaCells"] == null,
                    "metadata cannot imply whole-range counts or sampled data");
                AssertEqual(17179869184L, (long)overview["cellCount"], "full Excel bounds use a 64-bit count");
                var samples = (JArray)overview["sampleTargets"];
                AssertEqual(9, samples.Count, "sample plan covers the beginning, middle and end of both axes");
                AssertTrue(samples.Sum(sample => (int)sample["cells"]) <= 108 && overviewText.Length < 4096,
                    "sample plan and model output stay small independently of the source area");
                AssertEqual("Excel range: 'Big O''Brien'!A1:D3", (string)samples.First["target"], "first sample preserves quoted sheet names");
                AssertEqual("Excel range: 'Big O''Brien'!XFA1048574:XFD1048576", (string)samples.Last["target"], "last sample includes the far corner");
                AssertEqual(0, adapter.ExcelReadMaterializationCount, "overview reads range coordinates only");
                AssertEqual(0, adapter.DocumentSnapshotReadCount, "overview does not fall back to a workbook preview");

                var reference = gateway.ResolveIntentTarget(session, large.Target).Reference;
                var exact = gateway.Resolve(session, reference.Uri).Resource.Reference;
                var calls = adapter.ExcelBackendCalls.Count;
                var retained = gateway.ReadWhole(session, exact, "structure").Result;
                AssertEqual(overviewText, retained.Text, "retained overview preserves its explicit metadata-only meaning");
                AssertEqual(calls, adapter.ExcelBackendCalls.Count, "retained overview performs no Office reads");
                AssertTrue(gateway.Evidence(session, retained).All(item => item.View == "structure"),
                    "overview evidence never claims a values or formulas read");
                foreach (var view in new[] { "text", "formulas", "table", "records" })
                {
                    var command = view == "table" || view == "records"
                        ? Command(ResourceToolCatalog.ReadToolId, "target", large.Target, "representation", view, "limit", 1)
                        : Command(ResourceToolCatalog.ReadToolId, "target", large.Target, "representation", view);
                    var refused = executor.ExecuteManual(command, tools, new AppSettings(), false, false, session);
                    AssertEqual("RESOURCE_SNAPSHOT_TOO_LARGE", refused.ErrorCode, "raw large " + view + " remains bounded before capture");
                    AssertContains(refused.Message, "structure", "refusal offers a useful next step");
                }
                AssertEqual(calls, adapter.ExcelBackendCalls.Count, "even a one-row selector cannot trigger whole-range materialization");
                var sampleRead = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId,
                    "target", (string)samples.Last["target"], "representation", "text"), tools, new AppSettings(), false, false, session);
                AssertTrue(sampleRead.Success && sampleRead.DataJson.Contains("tail") && !sampleRead.DataJson.Contains("unread-body-"),
                    "the model can read the selected far slice exactly without reading the rest");
                AssertEqual(1, adapter.ExcelReadMaterializationCount, "only the selected small slice materializes values");

                var tall = gateway.ReadWhole(session, gateway.ResolveIntentTarget(session,
                    "Excel range: 'Big O''Brien'!C7:C200007").Reference, "structure").Result;
                var tallSamples = (JArray)JObject.Parse(tall.Text)["sampleTargets"];
                AssertTrue(tallSamples.Count == 3 && tallSamples.Sum(sample => (int)sample["cells"]) == 9,
                    "a narrow offset range has three non-overlapping row windows");
                AssertEqual("Excel range: 'Big O''Brien'!C200005:C200007", (string)tallSamples.Last["target"],
                    "sample offsets are relative to the requested extent");
                adapter.SeedExcelSheets(40);
                var bounded = gateway.Find(session, null, "document");
                AssertTrue(bounded.Items.Count == 20 && !bounded.Complete, "many sheets do not expand the model catalog unboundedly");
                var missing = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId,
                    "target", "Excel range: Missing!A1:XFD1048576", "representation", "structure"), tools, new AppSettings(), false, false, session);
                AssertTrue(!missing.Success, "overview validates the live worksheet rather than only parsing an address");
            });

            var leakedBodies = RuntimeThrows<ExcelReadBackendException>(() => new ExcelReadService(new StubExcelReadBackend {
                RangeResult = new ExcelRangeSnapshot { Sheet = "Data", Address = "A1:XFD1048576", Rows = 1048576,
                    Columns = 16384, CellCount = 17179869184L, Values = Matrix(1, 1) }
            }).CaptureRange("Data", "A1:XFD1048576", "metadata"));
            AssertEqual("excel_read_snapshot_invalid", leakedBodies.ErrorCode, "metadata snapshots reject unexpected cell bodies");
        }

        private static void ExcelUsedRangeDiscoveryPreservesCoverage()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                var gateway = executor.ResourceGateway;
                adapter.AddExcelSheetForTest("Quarterly Data");
                adapter.SetExcelCell("Quarterly Data", "C3", "first");
                adapter.SetExcelCell("Quarterly Data", "D6", "last");
                adapter.AddExcelSheetForTest("Blank");
                var first = gateway.List(session, "excel", null, null, 1);
                AssertTrue(first.Total == 3 && first.Truncated && first.NextCursor != null,
                    "used-range metadata is discoverable and paginated");
                var tail = gateway.List(session, "excel", null, first.NextCursor, 50);
                AssertTrue(tail.Items.Count == 2 && !tail.Truncated && tail.NextCursor == null,
                    "a complete workbook has a complete terminal page");
                var report = tail.Items.Single(item => item.Metadata["sheet"] == "Quarterly Data");
                AssertEqual("C3:D6", report.Metadata["address"], "extent comes from workbook metadata, not a default A1 range");
                AssertEqual("'Quarterly Data'!C3:D6", report.Title, "discovered extent keeps a usable quoted reference");
                AssertEqual("A1", tail.Items.Single(item => item.Metadata["sheet"] == "Blank").Metadata["address"],
                    "an empty fake sheet still supplies its bounded used-range address");
                var found = gateway.Find(session, "Quarterly Data", "document");
                AssertTrue(found.Complete && !found.Partial && found.Items.Any(item =>
                    item.Target == "Excel range: 'Quarterly Data'!C3:D6"),
                    "semantic discovery exposes the actual worksheet extent");
                AssertTrue(adapter.ExcelBackendCalls.All(operation => operation == FakeOfficeAdapter.ExcelInspectOperation),
                    "discovery reads metadata without cell materialization or mutations");

                adapter.SetExcelCell("Quarterly Data", "F8", "expanded");
                AssertEqual("resource_revision_changed", RuntimeThrows<ResourceRequestException>(() =>
                    gateway.List(session, "excel", null, first.NextCursor, 50)).ErrorCode,
                    "changed extent invalidates the existing collection cursor");
                var calls = adapter.ExcelBackendCalls.Count;
                adapter.QueueExcelInspectSnapshot(new ExcelInspectSnapshot { Kind = "workbook", ReturnedCount = 1,
                    Workbook = new ExcelWorkbookSnapshot { Name = "Invalid.xlsx",
                        Sheets = new List<ExcelSheetSnapshot> { new ExcelSheetSnapshot { Name = "Data" } } } });
                RuntimeThrows<ExcelReadBackendException>(() => gateway.List(session, "excel", null, null, 50));
                AssertEqual(calls + 1, adapter.ExcelBackendCalls.Count,
                    "missing UsedRange is rejected without a silent skip or fallback read");

                adapter.SeedExcelSheets(ExcelReadService.MaxInspectItems);
                var bounded = gateway.List(session, "excel", null, null, 50);
                var page = bounded;
                var captured = new List<ResourceDescriptor>();
                do
                {
                    captured.AddRange(page.Items);
                    if (page.NextCursor == null) break;
                    page = gateway.List(session, "excel", null, page.NextCursor, 50);
                } while (true);
                AssertTrue(captured.Count == ExcelReadService.MaxInspectItems && page.Truncated && page.NextCursor == null,
                    "terminal page retains missing coverage beyond the inspection bound");
                var missing = gateway.Find(session, "absent-range", "document");
                AssertTrue(!missing.Complete && !missing.Empty, "bounded discovery cannot prove that an unseen range is absent");
                adapter.QueueExcelInspectSnapshot(new ExcelInspectSnapshot { Kind = "workbook", ReturnedCount = captured.Count,
                    Workbook = new ExcelWorkbookSnapshot { Name = adapter.DocumentTitle,
                        Sheets = captured.Select(item => new ExcelSheetSnapshot {
                            Name = item.Metadata["sheet"], UsedRange = item.Metadata["address"] }).ToList() } });
                AssertEqual("resource_revision_changed", RuntimeThrows<ResourceRequestException>(() =>
                    gateway.List(session, "excel", null, bounded.NextCursor, 50)).ErrorCode,
                    "coverage-only changes invalidate continuation even when the captured ranges are identical");
                AssertTrue(adapter.ExcelBackendCalls.All(operation => operation == FakeOfficeAdapter.ExcelInspectOperation),
                    "all discovery and failure paths remain metadata-only");
            });
        }

        private static void ExcelDiscoveryDoesNotBlockDocumentReads()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                var gateway = executor.ResourceGateway;
                for (var index = 0; index <= ExcelReadService.MaxInspectItems; index++)
                    adapter.ExcelNamesForTest.Add(new ExcelNameSnapshot { Name = "Name" + index, RefersTo = "=42" });
                var root = gateway.List(session, LiveDocumentResourceProvider.ProviderName,
                    LiveDocumentResourceProvider.DocumentKind, null, 20).Items.Single();
                var target = ResourceGatewayService.IntentTarget(root);
                var found = gateway.Find(session, "Name", "document");
                AssertTrue(found.Items.Count > 0 && !found.Complete && !found.Partial,
                    "large name catalog remains bounded discovery, not provider unavailability");
                AssertTrue(!gateway.Find(session, "missing-name", "document").Empty,
                    "bounded Excel discovery never proves absence");
                var calls = adapter.ExcelBackendCalls.Count(item => item == FakeOfficeAdapter.ExcelInspectOperation);
                var read = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId, "target", target, "representation", "text"),
                    executor.GetControllerTools().ToList(), new AppSettings(), false, false, session);
                AssertTrue(read.Success, "document text read survives a large unrelated name catalog: " + read.Message);
                AssertEqual(calls, adapter.ExcelBackendCalls.Count(item => item == FakeOfficeAdapter.ExcelInspectOperation),
                    "document resolution does not enumerate Excel names, tables or sheets");
                AssertEqual("resource_target_not_found", RuntimeThrows<ResourceRequestException>(() =>
                    gateway.ResolveIntentTarget(session, target + " stale")).ErrorCode, "document title is still validated exactly");
                AssertEqual("RESOURCE_SNAPSHOT_TOO_LARGE", RuntimeThrows<ResourceRequestException>(() =>
                    gateway.ResolveIntentTarget(session, "Excel name: Name0")).ErrorCode, "individual name resolution still requires complete identity evidence");
                var range = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId, "target", "Excel range: Data!A1:B2", "representation", "text"),
                    executor.GetControllerTools().ToList(), new AppSettings(), false, false, session);
                AssertTrue(range.Success, "explicit sheet ranges remain readable independently of catalog size");
                AssertContains(ResourceReadToolHandler.Descriptor.Description,
                    "For known Excel cells", "tool guidance names the direct range route");
                AssertContains((string)JObject.Parse(ResourceReadToolHandler.Descriptor.ParametersJson)
                        ["properties"]["target"]["description"],
                    "discovery is optional", "target schema permits a known A1 range");
                var first = gateway.List(session, "excel", ExcelResourceProvider.NameKind, null, 50);
                var page = first; var total = page.Items.Count;
                while (page.NextCursor != null)
                {
                    page = gateway.List(session, "excel", ExcelResourceProvider.NameKind, page.NextCursor, 50);
                    total += page.Items.Count;
                }
                AssertTrue(total == ExcelReadService.MaxInspectItems && page.Truncated,
                    "terminal page preserves the source bound after all visible pages");
                var search = gateway.Search(session, "excel", "Name199", ExcelResourceProvider.NameKind, 20, 600);
                AssertTrue(search.Matches.Count == 0 && search.ScanTruncated, "single-page metadata search cannot claim a complete negative beyond its page");
                adapter.ExcelNamesForTest.RemoveAt(adapter.ExcelNamesForTest.Count - 1);
                AssertEqual("resource_revision_changed", RuntimeThrows<ResourceRequestException>(() => gateway.List(session,
                    "excel", ExcelResourceProvider.NameKind, first.NextCursor, 50)).ErrorCode,
                    "coverage-only changes invalidate continuation even with unchanged captured descriptors");
                adapter.ExcelNamesForTest.RemoveRange(25, adapter.ExcelNamesForTest.Count - 25);
                search = gateway.Search(session, "excel", "Name", ExcelResourceProvider.NameKind, 20, 600);
                AssertTrue(search.Matches.Count == 20 && search.ScanTruncated, "result clipping cannot masquerade as complete source search");
                var foreign = NewSession(adapter); foreign.DocumentKey = "another-document";
                AssertEqual("active_document_changed", RuntimeThrows<ResourceRequestException>(() =>
                    gateway.ResolveIntentTarget(foreign, target)).ErrorCode, "domain routing preserves bound-document checks");
            });
        }

        private static void ExcelReadUsesExactNativeOwnership()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var inspectDefinition = tools.Single(tool => tool.Id == ExcelReadToolIds.Inspect);
                var summarizeDefinition = tools.Single(tool => tool.Id == ExcelReadToolIds.SummarizeColumns);
                var runtime = executor.CreateNativeRuntime(session, new[] { inspectDefinition }, new AppSettings(), "agent", false);
                AssertTrue(runtime.Describe(new ToolCall("inspect", ExcelReadToolIds.Inspect, "{\"kind\":\"sheets\"}")) != null,
                    "composed inspect handler is described");
                AssertTrue(runtime.Describe(new ToolCall("summary", ExcelReadToolIds.SummarizeColumns, "{}")) == null,
                    "column analysis is unavailable until its exact schema is offered");
                var analysisRuntime = executor.CreateNativeRuntime(session, new[] { summarizeDefinition }, new AppSettings(), "agent", false);
                AssertTrue(analysisRuntime.Describe(new ToolCall("summary", ExcelReadToolIds.SummarizeColumns,
                    "{\"sheet\":\"Data\",\"address\":\"A1:A2\",\"columns\":[\"A\"]}")) != null,
                    "exact column-analysis binding is described when offered");
                AssertTrue(!tools.Any(tool => tool.Id == "excel.read_range") &&
                    !NativeToolRuntimeAdapter.Owns("excel.read_range") &&
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

                adapter.ExcelNamesForTest.Clear();
                for (var index = 0; index <= ExcelReadService.MaxInspectItems; index++)
                    adapter.ExcelNamesForTest.Add(new ExcelNameSnapshot
                    {
                        Name = "Name" + index,
                        RefersTo = "=42"
                    });
                adapter.ExcelBackendCalls.Clear();
                var document = executor.ExecuteManual(Command(
                        ResourceToolCatalog.ReadToolId,
                        "target", "document: " + session.DocumentTitle,
                        "representation", "text"),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(document.Success,
                    "the current document target resolves without unrelated Excel catalogs");
                AssertEqual(0, adapter.ExcelBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.ExcelInspectOperation),
                    "document target resolution does not enumerate defined names");

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
                    inner.ExcelNamesForTest.Add(new ExcelNameSnapshot { Name = "SalesRange", RefersTo = "=Data!$A$1:$B$4",
                        TargetKind = ExcelNameTargetKind.BoundRange, Sheet = "Data", Address = "A1:B4" });
                    tableOwnerSta = false;
                    var nameRead = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId,
                        "target", "Excel name: SalesRange", "representation", "text"), tools, new AppSettings(), false, false, chat);
                    AssertTrue(nameRead.Success && tableOwnerSta, "defined-name resolution/capture uses the bound owner STA");
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
                    var closedName = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId,
                        "target", "Excel name: SalesRange", "representation", "text"), tools, new AppSettings(), false, false, chat);
                    AssertTrue(!closedName.Success, "closed workbook cannot resolve a current defined name");
                    AssertEqual(dispatched, inner.ExcelBackendCalls.Count, "closed defined-name read performs no backend work");
                }
            });
        }

        private static void ExcelRangeReadsRetainExactEvidence()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                executor.BindResourceAuthority(session);
                var gateway = executor.ResourceGateway;
                var tools = OfficeToolCatalog.ForHost("Excel").Concat(executor.GetControllerTools()).ToList();
                var runtime = executor.CreateNativeRuntime(session, tools, new AppSettings(), "agent", false);
                adapter.ExcelBackendCalls.Clear();
                var direct = gateway.Find(session, "Data!A1:B4", "document");
                AssertTrue(direct.Complete && !direct.Partial && direct.Items.Count == 1,
                    "an exact Excel address is a complete point lookup");
                AssertContains(direct.Items.Single().Usage, "omit path",
                    "range discovery keeps the canonical record path runtime-owned");
                AssertEqual(0, adapter.ExcelBackendCalls.Count(call =>
                    call == FakeOfficeAdapter.ExcelInspectOperation),
                    "an exact range lookup does not scan unrelated document catalogs");
                adapter.SetExcelCellForTest("новый лист (2)", "A1", "first");
                adapter.SetExcelCellForTest("новый лист (2)", "A2", "second");
                var quoted = gateway.Find(session, "'новый лист (2)'!A1:A2", "document");
                AssertTrue(quoted.Complete && !quoted.Partial && quoted.Items.Count == 1,
                    "quoted sheet names with spaces are exact point lookups");
                AssertEqual("Excel range: 'новый лист (2)'!A1:A2", quoted.Items.Single().Target,
                    "range target preserves the actual sheet name");
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
                var quotedRead = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = "Excel range: 'новый лист (2)'!A1:A2", ["representation"] = "text" });
                AssertEqual(ToolExecutionOutcome.Ok, quotedRead.Outcome,
                    "model-facing read accepts Excel-quoted sheet names");
                var quotedText = JArray.Parse((string)JObject.Parse(quotedRead.Result.DataJson)["text"]);
                AssertEqual("first", (string)quotedText[0][0],
                    "quoted range reads the intended sheet");
                AssertEqual("second", (string)quotedText[1][0],
                    "quoted range preserves the requested address");
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
                var tableCandidate = gateway.Find(session, "Sales", "document").Items
                    .Single(item => item.Target == "Excel table: Sales");
                AssertContains(tableCandidate.Usage, "omit path",
                    "table discovery keeps the canonical record path runtime-owned");
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
                    new JObject { ["target"] = "Excel table: Sales", ["representation"] = "table", ["limit"] = 2 });
                AssertEqual(ToolExecutionOutcome.Ok, rows.Outcome,
                    "bounded model table rows use the canonical path without model plumbing");
                var rangeReads = adapter.ExcelBackendCalls.Count(item =>
                    item == FakeOfficeAdapter.ExcelRangeReadOperation);
                var wrongPath = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = "Excel table: Sales", ["representation"] = "table", ["path"] = "$.records" });
                AssertEqual(ToolExecutionOutcome.Error, wrongPath.Outcome,
                    "a conflicting explicit table path is rejected");
                AssertEqual("RESOURCE_VIEW_PATH_MISMATCH",
                    (string)JObject.Parse(wrongPath.Result.DataJson)["code"],
                    "path mismatch has a stable replan code");
                AssertEqual(ToolRetryPolicy.Replan, wrongPath.Recovery.RetryPolicy,
                    "path mismatch requires changed arguments, not an automatic retry");
                AssertContains(wrongPath.Result.Message, "$.values",
                    "path mismatch reports the exact canonical value");
                AssertEqual(rangeReads, adapter.ExcelBackendCalls.Count(item =>
                    item == FakeOfficeAdapter.ExcelRangeReadOperation),
                    "path mismatch is rejected before cell capture");
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
                        AssertTrue(gateway.List(session, "excel", ExcelResourceProvider.TableKind, null, 20).Truncated, "bounded table discovery retains explicit incomplete coverage");
                    }
                }
                var foreign = NewSession(adapter); foreign.DocumentKey = "foreign-workbook";
                RuntimeThrows<InvalidOperationException>(() => gateway.Read(foreign, new ResourceReadRequest { Reference = reference, Representation = "text" }));
            });
        }

        private static void ExcelDefinedNameResources()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var session = NewSession(adapter); executor.BindResourceAuthority(session);
                var name = new ExcelNameSnapshot { Name = "Sales", RefersTo = "=Data!$C$1:$D$2",
                    TargetKind = ExcelNameTargetKind.BoundRange, Sheet = "Data", Address = "C1:D2" };
                adapter.ExcelNamesForTest.Add(name);
                adapter.ExcelNamesForTest.Add(new ExcelNameSnapshot { Name = "Data!Sales", RefersTo = "=42" });
                foreach (var cell in new[] { "C1", "D1", "C2", "D2", "E1", "F1", "E2", "F2" })
                    adapter.SetExcelCellForTest("Data", cell, "same");
                var gateway = executor.ResourceGateway;
                var listed = gateway.List(session, "excel", ExcelResourceProvider.NameKind, null, 20).Items;
                AssertEqual(2, listed.Select(item => item.Reference.Uri).Distinct().Count(), "global and sheet-qualified names have distinct identities");
                var nameCandidate = gateway.Find(session, "Sales", "document").Items
                    .Single(item => item.Target == "Excel name: Sales");
                AssertContains(nameCandidate.Usage, "omit path",
                    "defined-name discovery keeps the canonical record path runtime-owned");
                AssertEqual("Excel name: Data!Sales", gateway.Find(session, "Excel name: Data!Sales", "document").Items.Single().Target,
                    "qualified name bypasses A1 target parsing");
                AssertEqual(0, adapter.ExcelBackendCalls.Count(item => item == FakeOfficeAdapter.ExcelRangeReadOperation), "name discovery does not read cells");
                var tools = OfficeToolCatalog.ForHost("Excel").Concat(executor.GetControllerTools()).ToList();
                var runtime = executor.CreateNativeRuntime(session, tools, new AppSettings(), "agent", false);
                var first = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = "Excel name: sales", ["representation"] = "text" });
                AssertEqual(ToolExecutionOutcome.Ok, first.Outcome, "generic native reader accepts a case-insensitive exact name");
                var evidence = first.ResourceEvidence.Single();
                AssertEqual(listed.Single(item => item.Title == "Sales").Reference.Uri, evidence.Resource.Uri, "read and discovery share identity");
                var original = (string)JObject.Parse(first.Result.DataJson)["text"];
                AssertEqual(name.RefersTo, (string)JObject.Parse(original).SelectToken("definition.refersTo"), "source retains the full definition");
                AssertEqual("C1:D2", (string)JObject.Parse(original).SelectToken("range.address"), "source retains the observed extent");
                name.RefersTo = "=OFFSET(Data!$C$1,0,0,2,2)";
                var changed = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = "Excel name: Sales", ["representation"] = "text" });
                AssertEqual(ToolExecutionOutcome.Ok, changed.Outcome, "resolved dynamic definition uses the same bound range capture");
                var changedEvidence = changed.ResourceEvidence.Single();
                AssertTrue(evidence.Resource.Revision != changedEvidence.Resource.Revision, "definition drift alone advances revision");
                name.Address = "E1:F2";
                var moved = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = "Excel name: Sales", ["representation"] = "text" });
                AssertEqual(ToolExecutionOutcome.Ok, moved.Outcome, "fresh read follows a changed resolved extent");
                AssertEqual(evidence.Resource.Uri, moved.ResourceEvidence.Single().Resource.Uri, "name identity survives relocation");
                AssertTrue(changedEvidence.Resource.Revision != moved.ResourceEvidence.Single().Resource.Revision, "extent drift advances revision despite equal cells");
                AssertEqual(EvidenceState.Superseded, new EvidenceStateReducer().Reduce(evidence,
                    executor.ResourceAuthority.CaptureMany(new[] { evidence.ScopeId })).State, "old definition is historical");
                adapter.ExcelNamesForTest.Remove(name);
                var calls = adapter.ExcelBackendCalls.Count;
                AssertEqual(original, gateway.Read(session, new ResourceReadRequest { Reference = evidence.Resource,
                    Representation = "text", MaxChars = 32000 }).Result.Text, "retained read survives removal of the live name");
                using (var data = new ResourceDataPlaneService(gateway))
                {
                    var opened = data.Open(session, "workspace", evidence.Resource, "records", "$.range.values");
                    AssertEqual(evidence.Resource.Revision, opened.Descriptor.Reference.Revision, "HTML records retain exact definition revision");
                }
                AssertEqual(calls, adapter.ExcelBackendCalls.Count, "historical text and records perform no Office I/O");
                adapter.ExcelNamesForTest.Add(name); name.Address = "E1:F3";
                foreach (var view in new[] { "table", "formulas", "structure" })
                {
                    var args = new JObject { ["target"] = "Excel name: Sales", ["representation"] = view };
                    if (view == "table") args["limit"] = 2;
                    var read = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId, args);
                    AssertEqual(ToolExecutionOutcome.Ok, read.Outcome, "resized name supports shared " + view + " view");
                }
                System.IO.File.Delete(executor.Payloads.PathFor(evidence.Payload.Sha256));
                calls = adapter.ExcelBackendCalls.Count;
                AssertEqual("RESOURCE_SNAPSHOT_UNAVAILABLE", RuntimeThrows<ResourceRequestException>(() => gateway.Read(session,
                    new ResourceReadRequest { Reference = evidence.Resource, Representation = "text" })).ErrorCode, "missing name CAS never falls forward");
                AssertEqual(calls, adapter.ExcelBackendCalls.Count, "missing CAS does not consult current name");
            });
        }

        private static void ExcelDefinedNameAdmission()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var session = NewSession(adapter); executor.BindResourceAuthority(session);
                var gateway = executor.ResourceGateway;
                foreach (var kind in new[] { ExcelNameTargetKind.Unresolved, ExcelNameTargetKind.ForeignRange, ExcelNameTargetKind.MultipleAreas })
                {
                    adapter.ExcelNamesForTest.Add(new ExcelNameSnapshot { Name = kind.ToString(), RefersTo = "=42",
                        TargetKind = kind, Sheet = "Data", Address = "A1:B4" });
                    var descriptor = gateway.List(session, "excel", ExcelResourceProvider.NameKind, null, 20).Items.Single(item => item.Title == kind.ToString());
                    AssertTrue(descriptor.Representations.SequenceEqual(new[] { "metadata" }), "unverified targets advertise metadata only: " + kind);
                    var metadata = gateway.Read(session, new ResourceReadRequest { Reference = descriptor.Reference, Representation = "auto" }).Result;
                    AssertEqual("=42", (string)JObject.Parse(metadata.Text)["refersTo"], "metadata preserves unresolved/external definition");
                    AssertEqual("RESOURCE_VIEW_UNAVAILABLE", RuntimeThrows<ResourceRequestException>(() => gateway.Read(session,
                        new ResourceReadRequest { Reference = descriptor.Reference, Representation = "text" })).ErrorCode, "unverified range fields cannot authorize cells");
                }
                AssertEqual(0, adapter.ExcelBackendCalls.Count(item => item == FakeOfficeAdapter.ExcelRangeReadOperation), "metadata/unsupported views never capture local cells");
                adapter.ExcelNamesForTest.Clear();
                var name = new ExcelNameSnapshot { Name = "Sales", RefersTo = "=Data!$A$1:$B$4",
                    TargetKind = ExcelNameTargetKind.BoundRange, Sheet = "Data", Address = "A1:B4" };
                adapter.ExcelNamesForTest.Add(name);
                var reference = gateway.List(session, "excel", ExcelResourceProvider.NameKind, null, 20).Items.Single().Reference;
                foreach (var defect in new[] { "truncated", "duplicate", "missing", "oversized", "external-address" })
                {
                    name.Address = defect == "oversized" ? "A1:Z10000" : defect == "external-address" ? "[other.xlsx]Data!A1:B4" : "A1:B4";
                    var names = defect == "missing" ? new List<ExcelNameSnapshot>() : new List<ExcelNameSnapshot> { name };
                    if (defect == "duplicate") names.Add(name);
                    adapter.QueueExcelInspectSnapshot(new ExcelInspectSnapshot { Kind = "names", Names = names,
                        ReturnedCount = names.Count, Truncated = defect == "truncated" });
                    RuntimeThrows<ResourceRequestException>(() => gateway.Read(session, new ResourceReadRequest { Reference = reference, Representation = "text" }));
                    AssertEqual(0, adapter.ExcelBackendCalls.Count(item => item == FakeOfficeAdapter.ExcelRangeReadOperation), defect + " refuses before cell capture");
                }
                name.Address = "A1:B4";
                foreach (var defect in new[] { "invalid-kind", "missing-sheet", "missing-address", "control-sheet" })
                {
                    name.TargetKind = defect == "invalid-kind" ? (ExcelNameTargetKind)999 : ExcelNameTargetKind.BoundRange;
                    name.Sheet = defect == "missing-sheet" ? null : defect == "control-sheet" ? "Data\n" : "Data";
                    name.Address = defect == "missing-address" ? null : "A1:B4";
                    RuntimeThrows<ExcelReadBackendException>(() => gateway.List(session, "excel", ExcelResourceProvider.NameKind, null, 20));
                }
                name.Sheet = "Data";
                adapter.ExcelNamesForTest.Add(name);
                RuntimeThrows<ResourceRequestException>(() => gateway.List(session, "excel", ExcelResourceProvider.NameKind, null, 20));
                adapter.ExcelNamesForTest.Clear();
                for (var index = 0; index <= ExcelReadService.MaxInspectItems; index++)
                    adapter.ExcelNamesForTest.Add(new ExcelNameSnapshot { Name = "Name" + index, RefersTo = "=42" });
                AssertTrue(gateway.List(session, "excel", ExcelResourceProvider.NameKind, null, 20).Truncated,
                    "incomplete name catalog is never advertised as complete");
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
            internal Func<ExcelRangeReadRequest, ExcelRangeSnapshot> ReadRangeOverride { get; set; }

            public ExcelInspectSnapshot Inspect(ExcelInspectRequest request) { return InspectResult; }
            public ExcelRangeSnapshot ReadRange(ExcelRangeReadRequest request)
            { return ReadRangeOverride == null ? RangeResult : ReadRangeOverride(request); }
        }
    }
}
