using RNAssistant.Core.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Domains.Excel;

namespace RNAssistant.Harness
{
    internal sealed partial class FakeOfficeAdapter
    {
        public ExcelChatChartSourceSnapshot ReadChatSource(
            ExcelChatChartSourceRequest request)
        {
            BeginExcelBackendCall(ExcelChartSourceReadOperation);
            if (request == null) throw new ArgumentNullException(nameof(request));
            var sheet = ResolveFakeChartSheet(request.Sheet);
            var address = string.IsNullOrWhiteSpace(request.Address)
                ? "A1:B4" : request.Address;
            var range = ValidateChartRange(
                address, request.MaxCells, "chat chart source");
            var values = ReadRange(sheet.Name, FormatRange(range))
                .Select(row => (IReadOnlyList<object>)row
                    .Select(value => (object)value).ToList())
                .ToList();
            var rows = range.End.Row - range.Start.Row + 1;
            var columns = range.End.Column - range.Start.Column + 1;
            return new ExcelChatChartSourceSnapshot
            {
                Workbook = DocumentTitle,
                Sheet = sheet.Name,
                Address = FormatRange(range),
                SourceMode = string.IsNullOrWhiteSpace(request.Address)
                    ? "selection" : "range",
                Rows = rows,
                Columns = columns,
                CellCount = (long)rows * columns,
                Values = values
            };
        }

        public ExcelChartCollectionSnapshot Read(ExcelChartReadRequest request)
        {
            BeginExcelBackendCall(ExcelChartReadOperation);
            var snapshot = CreateExcelChartSnapshot(request);
            var transform = ExcelChartReadTransform;
            return transform == null ? snapshot : transform(snapshot);
        }

        public void Apply(
            ExcelChartApplyRequest request,
            Action markDispatchPossible)
        {
            BeginExcelBackendCall(ExcelChartApplyOperation);
            if (request == null || request.Plan == null)
                throw new ArgumentNullException(nameof(request));
            if (markDispatchPossible == null)
                throw new ArgumentNullException(nameof(markDispatchPossible));
            ExcelChartRequests.Add(ChartCommand(request.Plan));
            ThrowQueuedExcelChartFailure();
            var current = CreateExcelChartSnapshot(new ExcelChartReadRequest
            {
                Plan = request.Plan,
                MaxCharts = request.MaxCharts,
                MaxSeries = request.MaxSeries,
                MaxSourceCells = request.MaxSourceCells
            });
            if (!string.Equals(
                current.StateToken, request.ExpectedStateToken,
                StringComparison.Ordinal))
                throw ChartFailure(
                    "chart target or source changed",
                    "excel_chart_target_changed");

            var plan = request.Plan;
            FakeSheet sheet;
            FakeChart chart;
            if (plan.Kind == ExcelChartMutationKind.Delete)
            {
                FindExactFakeChart(plan.Sheet, plan.ChartName, out sheet, out chart);
                markDispatchPossible();
                sheet.Charts.Remove(chart);
                ThrowAfterExcelChartMutation();
                return;
            }

            sheet = ResolveFakeChartSheet(plan.Sheet);
            if (plan.HasSourceRange)
                ValidateChartRange(
                    plan.SourceRange, request.MaxSourceCells,
                    "chart source");
            if (plan.HasCategoryLabelsRange)
                ValidateChartRange(
                    plan.CategoryLabelsRange, request.MaxSourceCells,
                    "chart category labels");
            if (plan.Kind == ExcelChartMutationKind.Create)
            {
                if (current.Charts.Count >= request.MaxCharts)
                    throw ChartFailure(
                        "chart collection limit reached",
                        "excel_chart_limit_reached");
                if (!string.IsNullOrWhiteSpace(plan.ChartName) &&
                    sheet.Charts.Any(item => string.Equals(
                        item.Name, plan.ChartName,
                        StringComparison.OrdinalIgnoreCase)))
                    throw ChartFailure(
                        "chart already exists", "chart_already_exists");
                markDispatchPossible();
                chart = new FakeChart
                {
                    Name = string.IsNullOrWhiteSpace(plan.ChartName)
                        ? NextFakeChartName(sheet) : plan.ChartName,
                    SourceRange = CanonicalChartRange(plan.SourceRange),
                    ChartType = plan.ChartType,
                    HasTitle = plan.ExpectedHasTitle,
                    Title = plan.Title ?? string.Empty,
                    CategoryLabelsRange = plan.HasCategoryLabelsRange
                        ? CanonicalChartRange(plan.CategoryLabelsRange)
                        : string.Empty,
                    HasXAxisTitle = plan.ExpectedHasXAxisTitle,
                    XAxisTitle = plan.XAxisTitle ?? string.Empty,
                    HasYAxisTitle = plan.ExpectedHasYAxisTitle,
                    YAxisTitle = plan.YAxisTitle ?? string.Empty,
                    Left = plan.Left,
                    Top = plan.Top,
                    Width = plan.Width,
                    Height = plan.Height,
                    SeriesCount = 1
                };
                sheet.Charts.Add(chart);
                ThrowAfterExcelChartMutation();
                return;
            }

            FindExactFakeChart(plan.Sheet, plan.ChartName, out sheet, out chart);
            markDispatchPossible();
            if (plan.HasSourceRange)
                chart.SourceRange = CanonicalChartRange(plan.SourceRange);
            if (plan.HasChartType) chart.ChartType = plan.ChartType;
            if (plan.HasTitle)
            {
                chart.HasTitle = plan.ExpectedHasTitle;
                chart.Title = plan.Title ?? string.Empty;
            }
            if (plan.HasCategoryLabelsRange)
                chart.CategoryLabelsRange =
                    CanonicalChartRange(plan.CategoryLabelsRange);
            if (plan.HasXAxisTitle)
            {
                chart.HasXAxisTitle = plan.ExpectedHasXAxisTitle;
                chart.XAxisTitle = plan.XAxisTitle ?? string.Empty;
            }
            if (plan.HasYAxisTitle)
            {
                chart.HasYAxisTitle = plan.ExpectedHasYAxisTitle;
                chart.YAxisTitle = plan.YAxisTitle ?? string.Empty;
            }
            if (plan.HasLeft) chart.Left = plan.Left;
            if (plan.HasTop) chart.Top = plan.Top;
            if (plan.HasWidth) chart.Width = plan.Width;
            if (plan.HasHeight) chart.Height = plan.Height;
            ThrowAfterExcelChartMutation();
        }

        internal void AddExcelChartForTest(
            string sheetName, string sourceRange, string name,
            string chartType = "line", string title = "Chart",
            int seriesCount = 1)
        {
            var sheet = ResolveFakeChartSheet(sheetName);
            sheet.Charts.Add(new FakeChart
            {
                Name = name,
                SourceRange = CanonicalChartRange(sourceRange),
                ChartType = NormalizeFakeChartType(chartType),
                HasTitle = true,
                Title = title ?? string.Empty,
                Left = 300,
                Top = 20,
                Width = 480,
                Height = 300,
                SeriesCount = seriesCount
            });
        }

        internal ExcelChartState ExcelChartForTest(
            string sheetName, string name)
        {
            FakeSheet sheet;
            FakeChart chart;
            return TryFindChart(sheetName, name, out sheet, out chart)
                ? FakeChartState(sheet, chart, null) : null;
        }

        private ExcelChartCollectionSnapshot CreateExcelChartSnapshot(
            ExcelChartReadRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.MaxCharts < 1 ||
                request.MaxCharts > ExcelChartService.MaxWorkbookCharts ||
                request.MaxSeries < 1 ||
                request.MaxSeries > ExcelChartService.MaxChartSeries ||
                request.MaxSourceCells < 1 ||
                request.MaxSourceCells > ExcelChartService.MaxChatChartCells)
                throw ChartFailure(
                    "invalid chart bound", "excel_chart_bound_invalid");
            var plan = request.Plan;
            FakeSheet planSheet = null;
            FakeRange source = null;
            FakeRange labels = null;
            if (plan != null && plan.Kind != ExcelChartMutationKind.Delete)
            {
                planSheet = ResolveFakeChartSheet(plan.Sheet);
                if (plan.HasSourceRange)
                    source = ValidateChartRange(
                        plan.SourceRange, request.MaxSourceCells,
                        "chart source");
                if (plan.HasCategoryLabelsRange)
                    labels = ValidateChartRange(
                        plan.CategoryLabelsRange, request.MaxSourceCells,
                        "chart category labels");
            }
            var charts = _excelSheetOrder
                .Where(name => _sheets.ContainsKey(name))
                .SelectMany(name => _sheets[name].Charts.Select(chart =>
                    FakeChartState(_sheets[name], chart, plan)))
                .ToList();
            if (charts.Count > request.MaxCharts)
                throw ChartFailure(
                    "chart collection is too large",
                    "excel_chart_collection_too_large");
            if (charts.Any(chart => chart.Series.Count > request.MaxSeries))
                throw ChartFailure(
                    "chart series collection is too large",
                    "excel_chart_series_too_large");
            var token = new JObject
            {
                ["activeSheet"] = ActiveFakeChartSheet().Name,
                ["charts"] = JArray.FromObject(charts)
            };
            if (source != null)
                token["source"] = FakeChartRangeState(planSheet, source);
            if (labels != null)
                token["labels"] = FakeChartRangeState(planSheet, labels);
            return new ExcelChartCollectionSnapshot
            {
                ActiveSheet = ActiveFakeChartSheet().Name,
                Charts = charts,
                StateToken = token.ToString(Formatting.None)
            };
        }

        private static ToolInvocation ChartCommand(ExcelChartMutationPlan plan)
        {
            return new ToolInvocation
            {
                ToolId = plan.Kind == ExcelChartMutationKind.Delete
                    ? "excel.delete_chart" : "excel.upsert_chart",
                Arguments = new Dictionary<string, object>
                {
                    { "sheet", plan.Sheet ?? string.Empty },
                    { "chartName", plan.ChartName ?? string.Empty }
                }
            };
        }

        private ExcelChartState FakeChartState(
            FakeSheet sheet, FakeChart chart,
            ExcelChartMutationPlan plan)
        {
            var target = plan != null && string.Equals(
                    sheet.Name, plan.Sheet,
                    StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(plan.ChartName) || string.Equals(
                    chart.Name, plan.ChartName,
                    StringComparison.OrdinalIgnoreCase));
            return new ExcelChartState
            {
                Sheet = sheet.Name,
                Name = chart.Name,
                HasTitle = chart.HasTitle,
                Title = chart.HasTitle ? chart.Title ?? string.Empty : string.Empty,
                ChartType = NormalizeFakeChartType(chart.ChartType),
                HasXAxisTitle = chart.HasXAxisTitle,
                XAxisTitle = chart.HasXAxisTitle
                    ? chart.XAxisTitle ?? string.Empty : string.Empty,
                HasYAxisTitle = chart.HasYAxisTitle,
                YAxisTitle = chart.HasYAxisTitle
                    ? chart.YAxisTitle ?? string.Empty : string.Empty,
                Left = chart.Left,
                Top = chart.Top,
                Width = chart.Width,
                Height = chart.Height,
                Series = FakeChartSeries(sheet, chart),
                SourceRangeSatisfied = target && plan.HasSourceRange &&
                    string.Equals(
                        CanonicalChartRange(chart.SourceRange),
                        CanonicalChartRange(plan.SourceRange),
                        StringComparison.OrdinalIgnoreCase),
                CategoryLabelsRangeSatisfied = target &&
                    plan.HasCategoryLabelsRange && string.Equals(
                        CanonicalChartRange(chart.CategoryLabelsRange),
                        CanonicalChartRange(plan.CategoryLabelsRange),
                        StringComparison.OrdinalIgnoreCase)
            };
        }

        private static IReadOnlyList<ExcelChartSeriesState> FakeChartSeries(
            FakeSheet sheet, FakeChart chart)
        {
            var count = Math.Max(0, chart.SeriesCount);
            var result = new List<ExcelChartSeriesState>(count);
            for (var index = 1; index <= count; index++)
                result.Add(new ExcelChartSeriesState
                {
                    Name = (chart.Title ?? string.Empty) + " " + index,
                    Formula = string.Join("|", new[]
                    {
                        sheet.Name,
                        CanonicalChartRange(chart.SourceRange),
                        CanonicalChartRange(chart.CategoryLabelsRange),
                        NormalizeFakeChartType(chart.ChartType),
                        Convert.ToString(index)
                    })
                });
            return result;
        }

        private JObject FakeChartRangeState(
            FakeSheet sheet, FakeRange range)
        {
            var cells = new JArray();
            for (var row = range.Start.Row; row <= range.End.Row; row++)
            {
                var line = new JArray();
                for (var column = range.Start.Column;
                    column <= range.End.Column; column++)
                {
                    var key = CellKey(row, column);
                    object value;
                    line.Add(new JObject
                    {
                        ["value"] = sheet.Cells.TryGetValue(key, out value)
                            ? JToken.FromObject(value ?? string.Empty)
                            : JValue.CreateNull(),
                        ["formula"] = sheet.FormulaCells.Contains(key)
                    });
                }
                cells.Add(line);
            }
            return new JObject
            {
                ["sheet"] = sheet.Name,
                ["address"] = FormatRange(range),
                ["cells"] = cells
            };
        }

        private FakeRange ValidateChartRange(
            string address, int maxCells, string purpose)
        {
            if (maxCells < 1 || maxCells > ExcelChartService.MaxChatChartCells)
                throw ChartFailure(
                    "invalid chart range bound", "excel_chart_bound_invalid");
            if (string.IsNullOrWhiteSpace(address) || address.IndexOf(',') >= 0)
                throw ChartFailure(
                    purpose + " must be one contiguous range",
                    "excel_chart_source_invalid");
            var range = ParseRange(address);
            var rows = range.End.Row - range.Start.Row + 1;
            var columns = range.End.Column - range.Start.Column + 1;
            var cells = (long)rows * columns;
            if (rows < 1 || columns < 1 || cells > maxCells)
                throw ChartFailure(
                    purpose + " is too large",
                    "excel_chart_source_too_large");
            return range;
        }

        private FakeSheet ResolveFakeChartSheet(string sheetName)
        {
            if (string.IsNullOrWhiteSpace(sheetName))
                return ActiveFakeChartSheet();
            FakeSheet sheet;
            if (!_sheets.TryGetValue(sheetName, out sheet))
                throw ChartFailure(
                    "worksheet not found", "excel_sheet_not_found");
            return sheet;
        }

        private FakeSheet ActiveFakeChartSheet()
        {
            FakeSheet sheet;
            if (!string.IsNullOrWhiteSpace(_activeExcelSheetName) &&
                _sheets.TryGetValue(_activeExcelSheetName, out sheet))
                return sheet;
            return _sheets.Values.FirstOrDefault() ?? EnsureSheet("Sheet1");
        }

        private void FindExactFakeChart(
            string sheetName, string chartName,
            out FakeSheet sheet, out FakeChart chart)
        {
            var matches = _sheets.Values.SelectMany(candidate =>
                    candidate.Charts.Where(item =>
                        (string.IsNullOrWhiteSpace(sheetName) || string.Equals(
                            candidate.Name, sheetName,
                            StringComparison.OrdinalIgnoreCase)) &&
                        string.Equals(item.Name, chartName,
                            StringComparison.OrdinalIgnoreCase))
                        .Select(item => new { Sheet = candidate, Chart = item }))
                .ToList();
            if (matches.Count != 1)
                throw ChartFailure(
                    matches.Count == 0 ? "chart not found" :
                        "chart name is ambiguous",
                    matches.Count == 0 ? "chart_not_found" :
                        "excel_chart_ambiguous");
            sheet = matches[0].Sheet;
            chart = matches[0].Chart;
        }

        private static string NextFakeChartName(FakeSheet sheet)
        {
            for (var index = 1; ; index++)
            {
                var candidate = "Chart " + index;
                if (!sheet.Charts.Any(chart => string.Equals(
                    chart.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                    return candidate;
            }
        }

        private static string CanonicalChartRange(string address)
        {
            return string.IsNullOrWhiteSpace(address)
                ? string.Empty : FormatRange(ParseRange(address));
        }

        private static string NormalizeFakeChartType(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "column":
                case "col": return "column";
                case "bar": return "bar";
                case "pie": return "pie";
                default: return "line";
            }
        }

        private void ThrowQueuedExcelChartFailure()
        {
            if (_nextExcelChartApplyFailure == null) return;
            var failure = _nextExcelChartApplyFailure;
            _nextExcelChartApplyFailure = null;
            throw failure;
        }

        private void ThrowAfterExcelChartMutation()
        {
            if (!ExcelChartThrowAfterMutation) return;
            ExcelChartThrowAfterMutation = false;
            throw new InvalidOperationException(
                "scripted failure after Excel chart mutation");
        }

        private static ExcelChartBackendException ChartFailure(
            string message, string code)
        {
            return new ExcelChartBackendException(message, code, false);
        }
    }
}
