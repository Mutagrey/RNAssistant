using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.OfficeHosts.Identity;
using Excel = Microsoft.Office.Interop.Excel;

namespace RNAssistant.OfficeHosts
{
    internal sealed class ExcelChartInteropBackend : IExcelChartBackend
    {
        private static readonly Regex FormulaRangePattern = new Regex(
            @"!\$?([A-Z]{1,3})\$?(\d+)(?::\$?([A-Z]{1,3})\$?(\d+))?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly ExcelDocumentSession _session;
        private readonly Excel.Workbook _workbook;

        internal ExcelChartInteropBackend(ExcelDocumentSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _workbook = session.BoundDocumentObject as Excel.Workbook;
            if (_workbook == null)
                throw new InvalidOperationException(
                    "The bound Excel workbook is unavailable.");
        }

        public ExcelChatChartSourceSnapshot ReadChatSource(
            ExcelChatChartSourceRequest request)
        {
            try
            {
                if (request == null) throw new ArgumentNullException(nameof(request));
                var workbook = RequireWorkbook();
                Excel.Range range;
                var sourceMode = "selection";
                if (!string.IsNullOrWhiteSpace(request.Address))
                {
                    var sheet = ResolveSheet(workbook, request.Sheet);
                    range = sheet.Range[request.Address];
                    sourceMode = "range";
                }
                else range = ResolveSelectionRange(workbook);
                if (range == null)
                    throw Failure(
                        "Select an Excel range first or provide sheet/address.",
                        "excel_chart_source_required", false);
                ValidateRange(range, request.MaxCells, "chat chart source");
                var rangeSheet = range.Worksheet as Excel.Worksheet;
                var rows = Convert.ToInt32(range.Rows.Count);
                var columns = Convert.ToInt32(range.Columns.Count);
                return new ExcelChatChartSourceSnapshot
                {
                    Workbook = workbook.Name,
                    Sheet = rangeSheet.Name,
                    Address = range.Address[false, false],
                    SourceMode = sourceMode,
                    Rows = rows,
                    Columns = columns,
                    CellCount = (long)rows * columns,
                    Values = RangeToRows(range)
                };
            }
            catch (ExcelChartBackendException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw Failure(ex.Message, "office_tool_error", true);
            }
        }

        public ExcelChartCollectionSnapshot Read(ExcelChartReadRequest request)
        {
            try
            {
                return CreateSnapshot(request);
            }
            catch (ExcelChartBackendException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw Failure(ex.Message, "office_tool_error", true);
            }
        }

        public void Apply(
            ExcelChartApplyRequest request,
            Action markDispatchPossible)
        {
            if (request == null || request.Plan == null)
                throw new ArgumentNullException(nameof(request));
            if (markDispatchPossible == null)
                throw new ArgumentNullException(nameof(markDispatchPossible));
            Excel.ChartObject created = null;
            try
            {
                var current = CreateSnapshot(new ExcelChartReadRequest
                {
                    Plan = request.Plan,
                    MaxCharts = request.MaxCharts,
                    MaxSeries = request.MaxSeries,
                    MaxSourceCells = request.MaxSourceCells
                });
                if (!string.Equals(
                    current.StateToken, request.ExpectedStateToken,
                    StringComparison.Ordinal))
                    throw Failure(
                        "Excel chart target or source changed before dispatch.",
                        "excel_chart_target_changed", false);
                var workbook = RequireWorkbook();
                var plan = request.Plan;
                if (plan.Kind == ExcelChartMutationKind.Delete)
                {
                    Excel.Worksheet ignored;
                    var chart = ResolveChart(
                        plan.Sheet, plan.ChartName, out ignored);
                    markDispatchPossible();
                    chart.Delete();
                    return;
                }

                var sheet = ResolveSheet(workbook, plan.Sheet);
                var source = plan.HasSourceRange
                    ? ResolveRange(
                        sheet, plan.SourceRange,
                        request.MaxSourceCells, "chart source")
                    : null;
                var labels = plan.HasCategoryLabelsRange
                    ? ResolveRange(
                        sheet, plan.CategoryLabelsRange,
                        request.MaxSourceCells, "chart category labels")
                    : null;
                if (plan.Kind == ExcelChartMutationKind.Create)
                {
                    if (current.Charts.Count >= request.MaxCharts)
                        throw Failure(
                            "Excel workbook chart limit for this operation was reached.",
                            "excel_chart_limit_reached", false);
                    if (!string.IsNullOrWhiteSpace(plan.ChartName) &&
                        current.Charts.Any(existingChart => string.Equals(
                            existingChart.Sheet, plan.Sheet,
                            StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(existingChart.Name, plan.ChartName,
                                StringComparison.OrdinalIgnoreCase)))
                        throw Failure(
                            "Chart already exists: " + plan.ChartName,
                            "chart_already_exists", false);
                    markDispatchPossible();
                    var chartObjects =
                        (Excel.ChartObjects)sheet.ChartObjects(Type.Missing);
                    created = chartObjects.Add(
                        Convert.ToSingle(plan.Left, CultureInfo.InvariantCulture),
                        Convert.ToSingle(plan.Top, CultureInfo.InvariantCulture),
                        Convert.ToSingle(plan.Width, CultureInfo.InvariantCulture),
                        Convert.ToSingle(plan.Height, CultureInfo.InvariantCulture));
                    var chart = created.Chart;
                    chart.SetSourceData(source);
                    chart.ChartType = ResolveChartType(plan.ChartType);
                    chart.HasTitle = plan.ExpectedHasTitle;
                    if (plan.ExpectedHasTitle)
                        chart.ChartTitle.Caption = plan.Title;
                    if (!string.IsNullOrWhiteSpace(plan.ChartName))
                        created.Name = plan.ChartName;
                    ApplyLabels(plan, chart, labels);
                    return;
                }

                Excel.Worksheet targetSheet;
                var target = ResolveChart(
                    plan.Sheet, plan.ChartName, out targetSheet);
                markDispatchPossible();
                ApplyUpdate(plan, targetSheet, target, source, labels);
            }
            catch (ExcelChartBackendException)
            {
                TryRollback(created);
                throw;
            }
            catch (Exception ex)
            {
                TryRollback(created);
                throw Failure(ex.Message, "office_tool_error", true);
            }
        }

        private ExcelChartCollectionSnapshot CreateSnapshot(
            ExcelChartReadRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.MaxCharts < 1 ||
                request.MaxCharts > ExcelChartService.MaxWorkbookCharts ||
                request.MaxSeries < 1 ||
                request.MaxSeries > ExcelChartService.MaxChartSeries ||
                request.MaxSourceCells < 1 ||
                request.MaxSourceCells > ExcelChartService.MaxChatChartCells)
                throw Failure(
                    "Excel chart bounds are invalid.",
                    "excel_chart_bound_invalid", false);
            var workbook = RequireWorkbook();
            Excel.Range source = null;
            Excel.Range labels = null;
            var plan = request.Plan;
            if (plan != null && plan.Kind != ExcelChartMutationKind.Delete)
            {
                var planSheet = ResolveSheet(workbook, plan.Sheet);
                if (plan.HasSourceRange)
                    source = ResolveRange(
                        planSheet, plan.SourceRange,
                        request.MaxSourceCells, "chart source");
                if (plan.HasCategoryLabelsRange)
                    labels = ResolveRange(
                        planSheet, plan.CategoryLabelsRange,
                        request.MaxSourceCells, "chart category labels");
            }
            var charts = new List<ExcelChartState>();
            foreach (Excel.Worksheet sheet in workbook.Worksheets)
            {
                if (!BelongsToSession(sheet))
                    throw Failure(
                        "Excel worksheet resolved outside the bound workbook.",
                        "excel_chart_target_invalid", false);
                var chartObjects =
                    (Excel.ChartObjects)sheet.ChartObjects(Type.Missing);
                foreach (Excel.ChartObject chartObject in chartObjects)
                {
                    if (charts.Count >= request.MaxCharts)
                        throw Failure(
                            "Excel workbook has too many charts for exact verification.",
                            "excel_chart_collection_too_large", false);
                    charts.Add(ReadChart(
                        sheet, chartObject, request.MaxSeries,
                        plan, source, labels));
                }
            }
            var activeSheet = ResolveSheet(workbook, null).Name;
            var observation = new JObject
            {
                ["activeSheet"] = activeSheet,
                ["charts"] = JArray.FromObject(charts)
            };
            if (source != null)
                observation["source"] = RangeObservation(source);
            if (labels != null)
                observation["labels"] = RangeObservation(labels);
            return new ExcelChartCollectionSnapshot
            {
                ActiveSheet = activeSheet,
                StateToken = Hash(observation.ToString(Formatting.None)),
                Charts = charts
            };
        }

        private ExcelChartState ReadChart(
            Excel.Worksheet sheet,
            Excel.ChartObject chartObject,
            int maxSeries,
            ExcelChartMutationPlan plan,
            Excel.Range source,
            Excel.Range labels)
        {
            var chart = chartObject.Chart;
            var series = ReadSeries(chart, maxSeries);
            var xAxis = PrimaryAxis(chart, Excel.XlAxisType.xlCategory);
            var yAxis = PrimaryAxis(chart, Excel.XlAxisType.xlValue);
            var xHasTitle = xAxis != null && Convert.ToBoolean(xAxis.HasTitle);
            var yHasTitle = yAxis != null && Convert.ToBoolean(yAxis.HasTitle);
            var target = plan != null &&
                string.Equals(sheet.Name, plan.Sheet,
                    StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(plan.ChartName) ||
                 string.Equals(chartObject.Name, plan.ChartName,
                    StringComparison.OrdinalIgnoreCase));
            return new ExcelChartState
            {
                Sheet = sheet.Name,
                Name = chartObject.Name,
                HasTitle = Convert.ToBoolean(chart.HasTitle),
                Title = ChartTitle(chart),
                ChartType = ChartTypeName(chart.ChartType),
                HasXAxisTitle = xHasTitle,
                XAxisTitle = xHasTitle
                    ? Convert.ToString(xAxis.AxisTitle.Caption) : string.Empty,
                HasYAxisTitle = yHasTitle,
                YAxisTitle = yHasTitle
                    ? Convert.ToString(yAxis.AxisTitle.Caption) : string.Empty,
                Left = chartObject.Left,
                Top = chartObject.Top,
                Width = chartObject.Width,
                Height = chartObject.Height,
                Series = series,
                SourceRangeSatisfied = target && source != null &&
                    SourceReferencesRange(
                        series, source,
                        plan != null && plan.HasCategoryLabelsRange),
                CategoryLabelsRangeSatisfied = target && labels != null &&
                    CategoryReferencesRange(series, labels)
            };
        }

        private static List<ExcelChartSeriesState> ReadSeries(
            Excel.Chart chart, int maxSeries)
        {
            var collection =
                (Excel.SeriesCollection)chart.SeriesCollection(Type.Missing);
            if (collection.Count > maxSeries)
                throw Failure(
                    "Excel chart has too many series for exact verification.",
                    "excel_chart_series_too_large", false);
            var result = new List<ExcelChartSeriesState>(collection.Count);
            for (var index = 1; index <= collection.Count; index++)
            {
                var series = (Excel.Series)collection.Item(index);
                result.Add(new ExcelChartSeriesState
                {
                    Name = Convert.ToString(
                        series.Name, CultureInfo.InvariantCulture) ?? string.Empty,
                    Formula = Convert.ToString(
                        series.Formula, CultureInfo.InvariantCulture) ?? string.Empty
                });
            }
            return result;
        }

        private static void ApplyUpdate(
            ExcelChartMutationPlan plan,
            Excel.Worksheet sheet,
            Excel.ChartObject chartObject,
            Excel.Range source,
            Excel.Range labels)
        {
            var chart = chartObject.Chart;
            if (plan.HasSourceRange) chart.SetSourceData(source);
            if (plan.HasChartType)
                chart.ChartType = ResolveChartType(plan.ChartType);
            if (plan.HasTitle)
            {
                chart.HasTitle = plan.ExpectedHasTitle;
                if (plan.ExpectedHasTitle)
                    chart.ChartTitle.Caption = plan.Title;
            }
            if (plan.HasLeft)
                chartObject.Left = Convert.ToSingle(
                    plan.Left, CultureInfo.InvariantCulture);
            if (plan.HasTop)
                chartObject.Top = Convert.ToSingle(
                    plan.Top, CultureInfo.InvariantCulture);
            if (plan.HasWidth)
                chartObject.Width = Convert.ToSingle(
                    plan.Width, CultureInfo.InvariantCulture);
            if (plan.HasHeight)
                chartObject.Height = Convert.ToSingle(
                    plan.Height, CultureInfo.InvariantCulture);
            ApplyLabels(plan, chart, labels);
        }

        private static void ApplyLabels(
            ExcelChartMutationPlan plan,
            Excel.Chart chart,
            Excel.Range labels)
        {
            if (plan.HasCategoryLabelsRange)
            {
                var collection =
                    (Excel.SeriesCollection)chart.SeriesCollection(Type.Missing);
                for (var index = 1; index <= collection.Count; index++)
                    ((Excel.Series)collection.Item(index)).XValues = labels;
            }
            ApplyAxisTitle(
                plan.HasXAxisTitle, plan.ExpectedHasXAxisTitle,
                plan.XAxisTitle, chart, Excel.XlAxisType.xlCategory,
                "xAxisTitle");
            ApplyAxisTitle(
                plan.HasYAxisTitle, plan.ExpectedHasYAxisTitle,
                plan.YAxisTitle, chart, Excel.XlAxisType.xlValue,
                "yAxisTitle");
        }

        private static void ApplyAxisTitle(
            bool requested,
            bool expectedHasTitle,
            string title,
            Excel.Chart chart,
            Excel.XlAxisType axisType,
            string argumentName)
        {
            if (!requested) return;
            var axis = PrimaryAxis(chart, axisType);
            if (axis == null)
            {
                if (!expectedHasTitle) return;
                throw Failure(
                    "Chart does not support the requested axis title: " +
                    argumentName,
                    "excel_chart_axis_unsupported", false);
            }
            axis.HasTitle = expectedHasTitle;
            if (expectedHasTitle)
            {
                if (axis.AxisTitle == null)
                    throw Failure(
                        "Excel did not create the requested axis title: " +
                        argumentName,
                        "excel_chart_axis_unsupported", false);
                axis.AxisTitle.Caption = title;
            }
        }

        private Excel.ChartObject ResolveChart(
            string sheetName,
            string chartName,
            out Excel.Worksheet resolvedSheet)
        {
            if (string.IsNullOrWhiteSpace(chartName))
                throw Failure(
                    "chartName is required.", "invalid_arguments", false);
            resolvedSheet = null;
            Excel.ChartObject found = null;
            foreach (Excel.Worksheet sheet in RequireWorkbook().Worksheets)
            {
                if (!string.IsNullOrWhiteSpace(sheetName) &&
                    !string.Equals(
                        sheet.Name, sheetName,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                var chartObjects =
                    (Excel.ChartObjects)sheet.ChartObjects(Type.Missing);
                foreach (Excel.ChartObject chart in chartObjects)
                    if (string.Equals(
                        chart.Name, chartName,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        if (found != null)
                            throw Failure(
                                "Chart name is ambiguous across worksheets: " +
                                chartName + ". Provide sheet.",
                                "excel_chart_ambiguous", false);
                        found = chart;
                        resolvedSheet = sheet;
                    }
            }
            if (found == null)
                throw Failure(
                    "Chart not found: " + chartName,
                    "chart_not_found", false);
            return found;
        }

        private Excel.Range ResolveRange(
            Excel.Worksheet sheet,
            string address,
            int maxCells,
            string purpose)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw Failure(
                    "Excel " + purpose + " is required.",
                    "excel_chart_source_invalid", false);
            var range = sheet.Range[address];
            ValidateRange(range, maxCells, purpose);
            return range;
        }

        private void ValidateRange(
            Excel.Range range, int maxCells, string purpose)
        {
            if (maxCells < 1 ||
                maxCells > ExcelChartService.MaxChatChartCells)
                throw Failure(
                    "Excel chart range ceiling is invalid.",
                    "excel_chart_bound_invalid", false);
            if (range == null || range.Areas.Count != 1)
                throw Failure(
                    "Excel " + purpose +
                    " must be one contiguous range.",
                    "excel_chart_source_invalid", false);
            var sheet = range.Worksheet as Excel.Worksheet;
            if (!BelongsToSession(sheet))
                throw Failure(
                    "Excel " + purpose +
                    " resolved outside the bound workbook.",
                    "excel_chart_target_invalid", false);
            var cells = (long)Convert.ToInt32(range.Rows.Count) *
                Convert.ToInt32(range.Columns.Count);
            if (cells < 1 || cells > maxCells)
                throw Failure(
                    "Excel " + purpose + " is too large: " + cells +
                    " cells. Limit is " + maxCells + ".",
                    "excel_chart_source_too_large", false);
        }

        private Excel.Workbook RequireWorkbook()
        {
            if (!_session.StaDispatcher.CheckAccess)
                throw Failure(
                    "Excel backend was called outside its owner STA.",
                    "document_session_thread_mismatch", false);
            if (!_session.IsAlive)
                throw Failure(
                    "The bound Excel workbook is closed.",
                    "active_document_changed", false);
            return _workbook;
        }

        private static Excel.Worksheet ResolveSheet(
            Excel.Workbook workbook, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                try
                {
                    var active = workbook.ActiveSheet as Excel.Worksheet;
                    if (active != null) return active;
                }
                catch
                {
                }
                foreach (Excel.Worksheet sheet in workbook.Worksheets) return sheet;
                throw Failure(
                    "Workbook has no worksheets.",
                    "excel_sheet_not_found", false);
            }
            foreach (Excel.Worksheet sheet in workbook.Worksheets)
                if (string.Equals(
                    sheet.Name, name, StringComparison.OrdinalIgnoreCase))
                    return sheet;
            throw Failure(
                "Worksheet not found: " + name,
                "excel_sheet_not_found", false);
        }

        private Excel.Range ResolveSelectionRange(Excel.Workbook workbook)
        {
            try
            {
                var range = workbook.Application.Selection as Excel.Range;
                if (RangeBelongsToSession(range)) return range;
            }
            catch
            {
            }
            try
            {
                var cell = workbook.Application.ActiveCell as Excel.Range;
                return RangeBelongsToSession(cell) ? cell : null;
            }
            catch
            {
                return null;
            }
        }

        private bool RangeBelongsToSession(Excel.Range range)
        {
            try
            {
                return range != null &&
                    BelongsToSession(range.Worksheet as Excel.Worksheet);
            }
            catch
            {
                return false;
            }
        }

        private bool BelongsToSession(Excel.Worksheet sheet)
        {
            try
            {
                var workbook = sheet == null
                    ? null : sheet.Parent as Excel.Workbook;
                return workbook != null && string.Equals(
                    DocumentIdentity.RuntimeKey("Excel", workbook),
                    _session.RuntimeDocumentId,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static JObject RangeObservation(Excel.Range range)
        {
            return new JObject
            {
                ["sheet"] = ((Excel.Worksheet)range.Worksheet).Name,
                ["address"] = range.Address[false, false],
                ["rows"] = Convert.ToInt32(range.Rows.Count),
                ["columns"] = Convert.ToInt32(range.Columns.Count),
                ["values"] = MatrixToken(range.Value2),
                ["formulas"] = MatrixToken(range.Formula)
            };
        }

        private static IReadOnlyList<IReadOnlyList<object>> RangeToRows(
            Excel.Range range)
        {
            var rows = new List<IReadOnlyList<object>>();
            var array = range.Value2 as object[,];
            if (array == null)
            {
                rows.Add(new List<object> { range.Value2 });
                return rows;
            }
            for (var row = array.GetLowerBound(0);
                row <= array.GetUpperBound(0); row++)
            {
                var line = new List<object>();
                for (var column = array.GetLowerBound(1);
                    column <= array.GetUpperBound(1); column++)
                    line.Add(array[row, column]);
                rows.Add(line);
            }
            return rows;
        }

        private static JArray MatrixToken(object value)
        {
            var result = new JArray();
            var array = value as object[,];
            if (array == null)
            {
                result.Add(new JArray(Canonical(value)));
                return result;
            }
            for (var row = array.GetLowerBound(0);
                row <= array.GetUpperBound(0); row++)
            {
                var line = new JArray();
                for (var column = array.GetLowerBound(1);
                    column <= array.GetUpperBound(1); column++)
                    line.Add(Canonical(array[row, column]));
                result.Add(line);
            }
            return result;
        }

        private static JToken Canonical(object value)
        {
            if (value == null || value == DBNull.Value)
                return JValue.CreateNull();
            if (value is DateTime)
                return ((DateTime)value).ToString(
                    "O", CultureInfo.InvariantCulture);
            if (value is string || value is bool || value is byte ||
                value is sbyte || value is short || value is ushort ||
                value is int || value is uint || value is long ||
                value is ulong || value is float || value is double ||
                value is decimal)
                return JToken.FromObject(value);
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static Excel.Axis PrimaryAxis(
            Excel.Chart chart, Excel.XlAxisType axisType)
        {
            if (!Convert.ToBoolean(
                chart.HasAxis[axisType, Excel.XlAxisGroup.xlPrimary]))
                return null;
            return chart.Axes(
                axisType, Excel.XlAxisGroup.xlPrimary) as Excel.Axis;
        }

        private static string ChartTitle(Excel.Chart chart)
        {
            return Convert.ToBoolean(chart.HasTitle) && chart.ChartTitle != null
                ? Convert.ToString(
                    chart.ChartTitle.Caption, CultureInfo.InvariantCulture) ??
                    string.Empty
                : string.Empty;
        }

        private static string ChartTypeName(Excel.XlChartType chartType)
        {
            switch (chartType)
            {
                case Excel.XlChartType.xlColumnClustered: return "column";
                case Excel.XlChartType.xlBarClustered: return "bar";
                case Excel.XlChartType.xlPie: return "pie";
                case Excel.XlChartType.xlLine:
                case Excel.XlChartType.xlLineMarkers: return "line";
                default: return chartType.ToString();
            }
        }

        private static Excel.XlChartType ResolveChartType(string value)
        {
            switch ((value ?? string.Empty).ToLowerInvariant())
            {
                case "column":
                case "col": return Excel.XlChartType.xlColumnClustered;
                case "bar": return Excel.XlChartType.xlBarClustered;
                case "pie": return Excel.XlChartType.xlPie;
                default: return Excel.XlChartType.xlLineMarkers;
            }
        }

        private static bool SourceReferencesRange(
            IReadOnlyList<ExcelChartSeriesState> series,
            Excel.Range range,
            bool categoriesOverridden)
        {
            var expressions = new List<string>();
            foreach (var item in series ?? new ExcelChartSeriesState[0])
            {
                var arguments = SeriesArguments(item.Formula);
                if (arguments.Count >= 3)
                {
                    expressions.Add(arguments[0]);
                    if (!categoriesOverridden) expressions.Add(arguments[1]);
                    expressions.Add(arguments[2]);
                }
                else expressions.Add(item.Formula);
            }
            return ReferencesRange(
                expressions, range, categoriesOverridden);
        }

        private static bool CategoryReferencesRange(
            IReadOnlyList<ExcelChartSeriesState> series,
            Excel.Range range)
        {
            var expressions = new List<string>();
            foreach (var item in series ?? new ExcelChartSeriesState[0])
            {
                var arguments = SeriesArguments(item.Formula);
                if (arguments.Count < 3) return false;
                expressions.Add(arguments[1]);
            }
            return ReferencesRange(expressions, range, false);
        }

        private static bool ReferencesRange(
            IEnumerable<string> expressions,
            Excel.Range range,
            bool allowLeftOmission)
        {
            var expectedSheet = ((Excel.Worksheet)range.Worksheet).Name;
            var positions = new List<CellBounds>();
            foreach (var expression in expressions ?? new string[0])
            {
                if (string.IsNullOrWhiteSpace(expression)) continue;
                if (!ContainsSheetReference(expression, expectedSheet))
                    return false;
                foreach (Match match in FormulaRangePattern.Matches(expression))
                {
                    var startColumn = ColumnNumber(match.Groups[1].Value);
                    var startRow = ParsePositive(match.Groups[2].Value);
                    var endColumn = match.Groups[3].Success
                        ? ColumnNumber(match.Groups[3].Value) : startColumn;
                    var endRow = match.Groups[4].Success
                        ? ParsePositive(match.Groups[4].Value) : startRow;
                    positions.Add(new CellBounds
                    {
                        StartRow = Math.Min(startRow, endRow),
                        EndRow = Math.Max(startRow, endRow),
                        StartColumn = Math.Min(startColumn, endColumn),
                        EndColumn = Math.Max(startColumn, endColumn)
                    });
                }
            }
            if (positions.Count == 0) return false;
            var expectedStartRow = Convert.ToInt32(range.Row);
            var expectedStartColumn = Convert.ToInt32(range.Column);
            var expectedEndRow = expectedStartRow +
                Convert.ToInt32(range.Rows.Count) - 1;
            var expectedEndColumn = expectedStartColumn +
                Convert.ToInt32(range.Columns.Count) - 1;
            if (positions.Any(item =>
                item.StartRow < expectedStartRow ||
                item.EndRow > expectedEndRow ||
                item.StartColumn < expectedStartColumn ||
                item.EndColumn > expectedEndColumn))
                return false;
            var minRow = positions.Min(item => item.StartRow);
            var maxRow = positions.Max(item => item.EndRow);
            var minColumn = positions.Min(item => item.StartColumn);
            var maxColumn = positions.Max(item => item.EndColumn);
            return minRow == expectedStartRow && maxRow == expectedEndRow &&
                maxColumn == expectedEndColumn &&
                (allowLeftOmission
                    ? minColumn >= expectedStartColumn &&
                        minColumn <= expectedEndColumn
                    : minColumn == expectedStartColumn);
        }

        private static bool ContainsSheetReference(
            string formula, string sheetName)
        {
            var plain = (sheetName ?? string.Empty) + "!";
            var quoted = "'" + (sheetName ?? string.Empty)
                .Replace("'", "''") + "'!";
            return formula.IndexOf(
                    plain, StringComparison.OrdinalIgnoreCase) >= 0 ||
                formula.IndexOf(
                    quoted, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static List<string> SeriesArguments(string formula)
        {
            formula = formula ?? string.Empty;
            var open = formula.IndexOf('(');
            var close = formula.LastIndexOf(')');
            if (open < 0 || close <= open)
                return new List<string>();
            var source = formula.Substring(open + 1, close - open - 1);
            var result = new List<string>();
            var start = 0;
            var depth = 0;
            var quoted = false;
            for (var index = 0; index < source.Length; index++)
            {
                var current = source[index];
                if (current == '"') quoted = !quoted;
                if (quoted) continue;
                if (current == '(') depth++;
                else if (current == ')') depth--;
                else if (current == ',' && depth == 0)
                {
                    result.Add(source.Substring(start, index - start));
                    start = index + 1;
                }
            }
            result.Add(source.Substring(start));
            return result;
        }

        private static int ColumnNumber(string letters)
        {
            var result = 0;
            foreach (var value in (letters ?? string.Empty).ToUpperInvariant())
                result = result * 26 + value - 'A' + 1;
            return result;
        }

        private static int ParsePositive(string value)
        {
            int parsed;
            return int.TryParse(
                value, NumberStyles.None, CultureInfo.InvariantCulture,
                out parsed) && parsed > 0 ? parsed : 1;
        }

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(
                    sha.ComputeHash(
                        Encoding.UTF8.GetBytes(value ?? string.Empty)));
        }

        private static void TryRollback(Excel.ChartObject chart)
        {
            if (chart == null) return;
            try { chart.Delete(); }
            catch
            {
            }
        }

        private static ExcelChartBackendException Failure(
            string message, string code, bool retryable)
        {
            return new ExcelChartBackendException(message, code, retryable);
        }

        private sealed class CellBounds
        {
            internal int StartRow { get; set; }
            internal int EndRow { get; set; }
            internal int StartColumn { get; set; }
            internal int EndColumn { get; set; }
        }
    }
}
