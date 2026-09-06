using System;
using System.Collections.Generic;
using System.Globalization;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.OfficeHosts.Identity;
using Excel = Microsoft.Office.Interop.Excel;

namespace RNAssistant.OfficeHosts
{
    // Direct production backend for one retained workbook. Document selection is
    // completed before construction; operations never resolve another workbook.
    internal sealed class ExcelInteropBackend : IExcelReadBackend, IExcelWriteBackend
    {
        private readonly ExcelDocumentSession _session;
        private readonly Excel.Workbook _workbook;

        internal ExcelInteropBackend(ExcelDocumentSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _workbook = session.BoundDocumentObject as Excel.Workbook;
            if (_workbook == null)
                throw new InvalidOperationException("The bound Excel workbook is unavailable.");
        }

        public ExcelInspectSnapshot Inspect(ExcelInspectRequest request)
        {
            try
            {
                if (request == null) throw new ArgumentNullException(nameof(request));
                RequireReadWorkbook();
                if (request.MaxItems < 1 || request.MaxItems > ExcelReadService.MaxInspectItems ||
                    request.MaxSeries < 1 || request.MaxSeries > ExcelReadService.MaxChartSeries)
                    throw ReadFailure("Excel inspection bounds are invalid.",
                        "excel_inspect_bound_invalid", false);
                switch ((request.Kind ?? string.Empty).Trim().ToLowerInvariant())
                {
                    case "workbook": return WorkbookSummary(request.MaxItems);
                    case "sheets": return ListSheets(request.MaxItems);
                    case "charts":
                        return string.IsNullOrWhiteSpace(request.ChartName)
                            ? ListCharts(request.Sheet, request.MaxItems, request.MaxSeries)
                            : GetChart(request.Sheet, request.ChartName, request.MaxItems, request.MaxSeries);
                    case "tables": return ListTables(request.Sheet, request.MaxItems);
                    case "names": return ListNames(request.MaxItems);
                    case "shapes": return ListShapes(request.Sheet, request.MaxItems);
                    default:
                        throw ReadFailure(
                            "kind must be workbook, sheets, charts, tables, names, or shapes.",
                            "excel_inspect_kind_invalid", false);
                }
            }
            catch (ExcelReadBackendException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw ReadFailure(ex.Message, "office_tool_error", true);
            }
        }

        public ExcelRangeSnapshot ReadRange(ExcelRangeReadRequest request)
        {
            try
            {
                if (request == null) throw new ArgumentNullException(nameof(request));
                var workbook = RequireReadWorkbook();
                var content = (request.Content ?? "values").Trim().ToLowerInvariant();
                if (request.MaxCells < 1 || request.MaxCells > ExcelReadService.MaxReadCells)
                    throw ReadFailure("Excel range ceiling is invalid.",
                        "excel_range_bound_invalid", false);
                if (content != "values" && content != "formulas" && content != "profile")
                    throw ReadFailure("content must be values, formulas, or profile.",
                        "excel_range_content_invalid", false);

                var sheet = ResolveSheet(workbook, request.Sheet);
                var address = request.Address ?? string.Empty;
                var range = content == "profile" && string.IsNullOrWhiteSpace(address)
                    ? (!string.IsNullOrWhiteSpace(request.Sheet)
                        ? sheet.UsedRange
                        : ResolveSelectionRange(workbook) ?? sheet.UsedRange)
                    : sheet.Range[string.IsNullOrWhiteSpace(address) ? "A1" : address];
                if (range == null)
                    throw ReadFailure("Excel range is unavailable.",
                        "excel_range_unavailable", false);
                if (range.Areas.Count != 1)
                    throw ReadFailure(
                        "Non-contiguous Excel ranges are not supported; read each area separately.",
                        "excel_range_non_contiguous", false);
                var cellCount = RangeCellCount(range);
                if (cellCount > request.MaxCells)
                    throw ReadFailure(
                        "Excel range is too large: " + cellCount + " cells. Limit is " +
                        request.MaxCells + "; split the request into smaller ranges.",
                        "excel_range_too_large", true,
                        "{\"cellCount\":" + cellCount + ",\"maxCells\":" + request.MaxCells + "}");

                var rows = Convert.ToInt32(range.Rows.Count);
                var columns = Convert.ToInt32(range.Columns.Count);
                var rangeSheet = range.Worksheet as Excel.Worksheet;
                if (!BelongsToSession(rangeSheet))
                    throw ReadFailure(
                        "Excel range resolved outside the bound workbook.",
                        "excel_range_unavailable", false);
                var snapshot = new ExcelRangeSnapshot
                {
                    Sheet = rangeSheet.Name,
                    Address = range.Address[false, false],
                    Rows = rows,
                    Columns = columns,
                    CellCount = (long)rows * columns
                };
                if (content == "values" || content == "profile")
                    snapshot.Values = RangeToRows(range);
                if (content == "formulas" || content == "profile")
                    snapshot.Formulas = RangeToFormulaRows(range);
                return snapshot;
            }
            catch (ExcelReadBackendException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw ReadFailure(ex.Message, "office_tool_error", true);
            }
        }

        public ExcelWriteSnapshot Read(ExcelWriteReadRequest request)
        {
            try
            {
                if (request == null) throw new ArgumentNullException(nameof(request));
                Excel.Worksheet sheet;
                var kind = WriteKind(request.Kind);
                var range = ResolveWriteRange(
                    kind, request.Sheet, request.Address, request.Rows,
                    request.Columns, request.MaxCells, out sheet);
                var rows = Convert.ToInt32(range.Rows.Count);
                var columns = Convert.ToInt32(range.Columns.Count);
                return new ExcelWriteSnapshot
                {
                    Kind = kind,
                    Sheet = sheet.Name,
                    Address = range.Address[false, false],
                    Rows = rows,
                    Columns = columns,
                    CellCount = (long)rows * columns,
                    Values = RangeToRows(range),
                    Formulas = RangeToFormulaRows(range),
                    HasFormulas = RangeFormulaFlags(range, rows, columns)
                };
            }
            catch (ExcelWriteBackendException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw WriteFailure(ex.Message, "office_tool_error", true);
            }
        }

        public void Apply(ExcelWriteApplyRequest request, Action markDispatchPossible)
        {
            try
            {
                if (request == null) throw new ArgumentNullException(nameof(request));
                if (markDispatchPossible == null)
                    throw new ArgumentNullException(nameof(markDispatchPossible));
                Excel.Worksheet sheet;
                var kind = WriteKind(request.Kind);
                var range = ResolveWriteRange(
                    kind, request.Sheet, request.Address, request.Rows,
                    request.Columns, request.MaxCells, out sheet);
                object payload;
                if (kind == "value")
                {
                    payload = request.Value;
                }
                else if (kind == "formula")
                {
                    if (string.IsNullOrWhiteSpace(request.Formula))
                        throw WriteFailure("formula is required when kind is formula.",
                            "excel_write_formula_invalid", false);
                    payload = request.Formula;
                }
                else
                {
                    var rows = Convert.ToInt32(range.Rows.Count);
                    var columns = Convert.ToInt32(range.Columns.Count);
                    if (request.Values == null || request.Values.Count != rows)
                        throw WriteFailure(
                            "Table payload does not match the resolved target.",
                            "excel_write_target_mismatch", false);
                    var data = new object[rows, columns];
                    for (var row = 0; row < rows; row++)
                    {
                        var source = request.Values[row];
                        if (source == null || source.Count != columns)
                            throw WriteFailure(
                                "Table payload does not match the resolved target.",
                                "excel_write_target_mismatch", false);
                        for (var column = 0; column < columns; column++)
                            data[row, column] = ExcelTableCellValue(source[column]);
                    }
                    payload = data;
                }

                markDispatchPossible();
                if (kind == "formula") range.Formula = payload;
                else range.Value2 = payload;
            }
            catch (ExcelWriteBackendException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw WriteFailure(ex.Message, "office_tool_error", true);
            }
        }

        private ExcelInspectSnapshot WorkbookSummary(int maxItems)
        {
            var workbook = RequireReadWorkbook();
            var sheets = ReadSheets(workbook, maxItems, true);
            return new ExcelInspectSnapshot
            {
                Kind = "workbook",
                Workbook = new ExcelWorkbookSnapshot
                {
                    Name = workbook.Name,
                    FullName = workbook.FullName,
                    Sheets = sheets.Items
                },
                ReturnedCount = sheets.Items.Count,
                Truncated = sheets.Truncated
            };
        }

        private ExcelInspectSnapshot ListSheets(int maxItems)
        {
            var sheets = ReadSheets(RequireReadWorkbook(), maxItems, false);
            return new ExcelInspectSnapshot
            {
                Kind = "sheets",
                Sheets = sheets.Items,
                ReturnedCount = sheets.Items.Count,
                Truncated = sheets.Truncated
            };
        }

        private static BoundedItems<ExcelSheetSnapshot> ReadSheets(
            Excel.Workbook workbook, int maxItems, bool includeUsedRange)
        {
            var result = new List<ExcelSheetSnapshot>();
            var total = workbook.Worksheets.Count;
            var take = Math.Min(total, maxItems);
            foreach (Excel.Worksheet sheet in workbook.Worksheets)
            {
                if (result.Count >= take) break;
                result.Add(new ExcelSheetSnapshot
                {
                    Name = sheet.Name,
                    UsedRange = includeUsedRange
                        ? SafeString(delegate { return sheet.UsedRange.Address[false, false]; })
                        : null
                });
            }
            return new BoundedItems<ExcelSheetSnapshot>(result, total > take);
        }

        private ExcelInspectSnapshot ListCharts(
            string sheetFilter, int maxItems, int maxSeries)
        {
            var charts = new List<ExcelChartSnapshot>();
            var sheets = InspectSheets(sheetFilter, maxItems);
            var truncated = sheets.Truncated;
            for (var sheetIndex = 0; sheetIndex < sheets.Items.Count; sheetIndex++)
            {
                var sheet = sheets.Items[sheetIndex];
                var objects = (Excel.ChartObjects)sheet.ChartObjects(Type.Missing);
                var remaining = maxItems - charts.Count;
                var take = Math.Min(objects.Count, remaining);
                for (var index = 1; index <= take; index++)
                {
                    var detail = ReadChartDetails(
                        sheet, (Excel.ChartObject)objects.Item(index), maxSeries);
                    charts.Add(detail);
                    if (detail.SeriesTruncated) truncated = true;
                }
                if (objects.Count > take) { truncated = true; break; }
                if (charts.Count >= maxItems)
                {
                    if (sheetIndex + 1 < sheets.Items.Count) truncated = true;
                    break;
                }
            }
            return new ExcelInspectSnapshot
            {
                Kind = "charts",
                Charts = charts,
                ReturnedCount = charts.Count,
                Truncated = truncated
            };
        }

        private ExcelInspectSnapshot GetChart(
            string sheetFilter, string chartName, int maxItems, int maxSeries)
        {
            if (string.IsNullOrWhiteSpace(chartName))
                throw ReadFailure(
                    "chartName is required.", "excel_chart_name_required", false);
            var scanned = 0;
            var sheets = InspectSheets(sheetFilter, maxItems);
            foreach (var sheet in sheets.Items)
            {
                var objects = (Excel.ChartObjects)sheet.ChartObjects(Type.Missing);
                for (var index = 1; index <= objects.Count; index++)
                {
                    if (scanned >= maxItems)
                        throw ReadFailure(
                            "Chart lookup reached the bounded inspection limit; provide sheet to narrow the lookup.",
                            "excel_inspect_limit_reached", false);
                    scanned++;
                    var chart = (Excel.ChartObject)objects.Item(index);
                    if (!string.Equals(chart.Name, chartName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var detail = ReadChartDetails(sheet, chart, maxSeries);
                    return new ExcelInspectSnapshot
                    {
                        Kind = "charts",
                        Chart = detail,
                        ReturnedCount = 1,
                        Truncated = detail.SeriesTruncated
                    };
                }
            }
            if (sheets.Truncated)
                throw ReadFailure(
                    "Chart lookup reached the bounded worksheet limit; provide sheet to narrow the lookup.",
                    "excel_inspect_limit_reached", false);
            throw ReadFailure(
                "Chart not found: " + chartName, "excel_chart_not_found", false);
        }

        private ExcelInspectSnapshot ListTables(string sheetFilter, int maxItems)
        {
            var tables = new List<ExcelTableSnapshot>();
            var sheets = InspectSheets(sheetFilter, maxItems);
            var truncated = sheets.Truncated;
            for (var sheetIndex = 0; sheetIndex < sheets.Items.Count; sheetIndex++)
            {
                var sheet = sheets.Items[sheetIndex];
                var collection = sheet.ListObjects;
                var remaining = maxItems - tables.Count;
                var take = Math.Min(collection.Count, remaining);
                var added = 0;
                foreach (Excel.ListObject table in collection)
                {
                    if (added >= take) break;
                    tables.Add(new ExcelTableSnapshot
                    {
                        Sheet = sheet.Name,
                        Name = table.Name,
                        DisplayName = table.DisplayName,
                        Range = table.Range == null
                            ? string.Empty : table.Range.Address[false, false],
                        Rows = table.ListRows.Count,
                        Columns = table.ListColumns.Count
                    });
                    added++;
                }
                if (collection.Count > take) { truncated = true; break; }
                if (tables.Count >= maxItems)
                {
                    if (sheetIndex + 1 < sheets.Items.Count) truncated = true;
                    break;
                }
            }
            return new ExcelInspectSnapshot
            {
                Kind = "tables",
                Tables = tables,
                ReturnedCount = tables.Count,
                Truncated = truncated
            };
        }

        private ExcelInspectSnapshot ListNames(int maxItems)
        {
            var workbook = RequireReadWorkbook();
            var names = new List<ExcelNameSnapshot>();
            var total = workbook.Names.Count;
            var take = Math.Min(total, maxItems);
            foreach (Excel.Name name in workbook.Names)
            {
                if (names.Count >= take) break;
                Excel.Range target = null;
                try { target = name.RefersToRange; } catch { }
                var sheet = target == null ? null : target.Worksheet as Excel.Worksheet;
                var targetKind = target == null ? ExcelNameTargetKind.Unresolved :
                    !BelongsToSession(sheet) ? ExcelNameTargetKind.ForeignRange :
                    target.Areas.Count != 1 ? ExcelNameTargetKind.MultipleAreas : ExcelNameTargetKind.BoundRange;
                names.Add(new ExcelNameSnapshot
                {
                    Name = name.Name,
                    RefersTo = Convert.ToString(name.RefersTo),
                    TargetKind = targetKind,
                    Sheet = targetKind == ExcelNameTargetKind.BoundRange ? sheet.Name : null,
                    Address = targetKind == ExcelNameTargetKind.BoundRange ? target.Address[false, false] : null
                });
            }
            return new ExcelInspectSnapshot
            {
                Kind = "names",
                Names = names,
                ReturnedCount = names.Count,
                Truncated = total > take
            };
        }

        private ExcelInspectSnapshot ListShapes(string sheetFilter, int maxItems)
        {
            var shapes = new List<ExcelShapeSnapshot>();
            var sheets = InspectSheets(sheetFilter, maxItems);
            var truncated = sheets.Truncated;
            for (var sheetIndex = 0; sheetIndex < sheets.Items.Count; sheetIndex++)
            {
                var sheet = sheets.Items[sheetIndex];
                var collection = sheet.Shapes;
                var remaining = maxItems - shapes.Count;
                var take = Math.Min(collection.Count, remaining);
                var added = 0;
                foreach (Excel.Shape shape in collection)
                {
                    if (added >= take) break;
                    shapes.Add(new ExcelShapeSnapshot
                    {
                        Sheet = sheet.Name,
                        Name = shape.Name,
                        Type = shape.Type.ToString(),
                        Left = shape.Left,
                        Top = shape.Top,
                        Width = shape.Width,
                        Height = shape.Height,
                        AlternativeText = SafeString(delegate { return shape.AlternativeText; })
                    });
                    added++;
                }
                if (collection.Count > take) { truncated = true; break; }
                if (shapes.Count >= maxItems)
                {
                    if (sheetIndex + 1 < sheets.Items.Count) truncated = true;
                    break;
                }
            }
            return new ExcelInspectSnapshot
            {
                Kind = "shapes",
                Shapes = shapes,
                ReturnedCount = shapes.Count,
                Truncated = truncated
            };
        }

        private BoundedItems<Excel.Worksheet> InspectSheets(
            string sheetFilter, int maxItems)
        {
            var workbook = RequireReadWorkbook();
            if (!string.IsNullOrWhiteSpace(sheetFilter))
                return new BoundedItems<Excel.Worksheet>(
                    new List<Excel.Worksheet> { ResolveSheet(workbook, sheetFilter) }, false);
            var total = workbook.Worksheets.Count;
            var take = Math.Min(total, maxItems);
            var result = new List<Excel.Worksheet>();
            foreach (Excel.Worksheet sheet in workbook.Worksheets)
            {
                if (result.Count >= take) break;
                result.Add(sheet);
            }
            return new BoundedItems<Excel.Worksheet>(result, total > take);
        }

        private static ExcelChartSnapshot ReadChartDetails(
            Excel.Worksheet sheet, Excel.ChartObject chartObject, int maxSeries)
        {
            var chart = chartObject.Chart;
            var series = new List<ExcelChartSeriesSnapshot>();
            var seriesTruncated = false;
            try
            {
                var collection = (Excel.SeriesCollection)chart.SeriesCollection(Type.Missing);
                var take = Math.Min(collection.Count, maxSeries);
                seriesTruncated = collection.Count > take;
                for (var index = 1; index <= take; index++)
                {
                    try
                    {
                        var item = (Excel.Series)collection.Item(index);
                        series.Add(new ExcelChartSeriesSnapshot
                        {
                            Name = Convert.ToString(item.Name),
                            Formula = Convert.ToString(item.Formula)
                        });
                    }
                    catch
                    {
                        seriesTruncated = true;
                    }
                }
            }
            catch
            {
                seriesTruncated = true;
            }
            return new ExcelChartSnapshot
            {
                Sheet = sheet == null ? string.Empty : sheet.Name,
                Name = chartObject.Name,
                Title = ChartTitle(chart),
                ChartType = chart.ChartType.ToString(),
                XAxisTitle = AxisTitle(chart, Excel.XlAxisType.xlCategory),
                YAxisTitle = AxisTitle(chart, Excel.XlAxisType.xlValue),
                Series = series,
                SeriesTruncated = seriesTruncated,
                Left = chartObject.Left,
                Top = chartObject.Top,
                Width = chartObject.Width,
                Height = chartObject.Height
            };
        }

        private Excel.Range ResolveWriteRange(
            string kind,
            string sheetName,
            string address,
            int expectedRows,
            int expectedColumns,
            int maxCells,
            out Excel.Worksheet sheet)
        {
            var workbook = RequireWriteWorkbook();
            if (maxCells < 1 || maxCells > ExcelWriteService.MaxWriteCells)
                throw WriteFailure(
                    "Excel write ceiling is invalid.", "excel_write_bound_invalid", false);
            sheet = ResolveSheet(workbook, sheetName);
            var requested = sheet.Range[string.IsNullOrWhiteSpace(address) ? "A1" : address];
            if (requested == null || requested.Areas.Count != 1)
                throw WriteFailure(
                    "Excel write target must be one contiguous range.",
                    "excel_write_target_invalid", false);

            Excel.Range range = requested;
            if (kind == "table")
            {
                if (expectedRows < 1 || expectedRows > ExcelWriteService.MaxWriteRows ||
                    expectedColumns < 1 || expectedColumns > ExcelWriteService.MaxWriteColumns ||
                    (long)expectedRows * expectedColumns > maxCells)
                    throw WriteFailure(
                        "Excel table dimensions exceed the write bound.",
                        "excel_write_too_large", false);
                var start = requested.Cells[1, 1] as Excel.Range;
                if (start == null ||
                    (long)start.Row + expectedRows - 1 > sheet.Rows.Count ||
                    (long)start.Column + expectedColumns - 1 > sheet.Columns.Count)
                    throw WriteFailure(
                        "Excel table target exceeds worksheet bounds.",
                        "excel_write_target_invalid", false);
                range = start.Resize[expectedRows, expectedColumns];
            }

            var rows = Convert.ToInt32(range.Rows.Count);
            var columns = Convert.ToInt32(range.Columns.Count);
            var cellCount = (long)rows * columns;
            if (rows < 1 || columns < 1 || cellCount > maxCells)
                throw WriteFailure(
                    "Excel write target is too large: " + cellCount +
                    " cells. Limit is " + maxCells + ".",
                    "excel_write_too_large", false);
            if (kind != "table" && (expectedRows > 0 || expectedColumns > 0) &&
                (rows != expectedRows || columns != expectedColumns))
                throw WriteFailure(
                    "Excel write target dimensions changed.",
                    "excel_write_target_mismatch", false);
            var rangeSheet = range.Worksheet as Excel.Worksheet;
            if (!BelongsToSession(rangeSheet))
                throw WriteFailure(
                    "Excel write target resolved outside the bound workbook.",
                    "excel_write_target_invalid", false);
            sheet = rangeSheet;
            return range;
        }

        private bool BelongsToSession(Excel.Worksheet sheet)
        {
            try
            {
                var workbook = sheet == null ? null : sheet.Parent as Excel.Workbook;
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

        private Excel.Workbook RequireReadWorkbook()
        {
            if (!_session.StaDispatcher.CheckAccess)
                throw ReadFailure(
                    "Excel backend was called outside its owner STA.",
                    "document_session_thread_mismatch", false);
            if (!_session.IsAlive)
                throw ReadFailure(
                    "The bound Excel workbook is closed.",
                    "active_document_changed", false);
            return _workbook;
        }

        private Excel.Workbook RequireWriteWorkbook()
        {
            if (!_session.StaDispatcher.CheckAccess)
                throw WriteFailure(
                    "Excel backend was called outside its owner STA.",
                    "document_session_thread_mismatch", false);
            if (!_session.IsAlive)
                throw WriteFailure(
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
                throw new InvalidOperationException("Workbook has no worksheets.");
            }
            foreach (Excel.Worksheet sheet in workbook.Worksheets)
            {
                if (string.Equals(
                    SafeString(delegate { return sheet.Name; }),
                    name,
                    StringComparison.OrdinalIgnoreCase))
                    return sheet;
            }
            throw new InvalidOperationException("Worksheet not found: " + name);
        }

        private Excel.Range ResolveSelectionRange(Excel.Workbook workbook)
        {
            try
            {
                var application = workbook.Application;
                var range = application == null ? null : application.Selection as Excel.Range;
                if (BelongsToSession(range == null ? null : range.Worksheet as Excel.Worksheet))
                    return range;

                var activeCell = application == null ? null : application.ActiveCell as Excel.Range;
                if (BelongsToSession(
                    activeCell == null ? null : activeCell.Worksheet as Excel.Worksheet))
                    return activeCell;
            }
            catch
            {
            }
            return null;
        }

        private static string WriteKind(string value)
        {
            var kind = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (kind != "value" && kind != "formula" && kind != "table")
                throw WriteFailure(
                    "kind must be value, formula, or table.",
                    "excel_write_kind_invalid", false);
            return kind;
        }

        private static object ExcelTableCellValue(object value)
        {
            if (value == null || value is string || value is bool) return value;
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }

        private static List<List<object>> RangeToRows(Excel.Range range)
        {
            return MatrixRows(range.Value2);
        }

        private static List<List<object>> RangeToFormulaRows(Excel.Range range)
        {
            return MatrixRows(range.Formula);
        }

        private static List<List<object>> MatrixRows(object value)
        {
            var rows = new List<List<object>>();
            var array = value as object[,];
            if (array == null)
            {
                rows.Add(new List<object> { value });
                return rows;
            }
            for (var row = array.GetLowerBound(0); row <= array.GetUpperBound(0); row++)
            {
                var line = new List<object>();
                for (var column = array.GetLowerBound(1);
                    column <= array.GetUpperBound(1); column++)
                    line.Add(array[row, column]);
                rows.Add(line);
            }
            return rows;
        }

        private static List<List<bool>> RangeFormulaFlags(
            Excel.Range range, int rows, int columns)
        {
            bool? uniform = null;
            try
            {
                var raw = range.HasFormula;
                if (raw is bool) uniform = (bool)raw;
            }
            catch
            {
            }
            var result = new List<List<bool>>(rows);
            for (var row = 1; row <= rows; row++)
            {
                var line = new List<bool>(columns);
                for (var column = 1; column <= columns; column++)
                {
                    if (uniform.HasValue) line.Add(uniform.Value);
                    else
                    {
                        var cell = range.Cells[row, column] as Excel.Range;
                        line.Add(cell != null && Convert.ToBoolean(cell.HasFormula));
                    }
                }
                result.Add(line);
            }
            return result;
        }

        private static long RangeCellCount(Excel.Range range)
        {
            if (range == null) return 0;
            long total = 0;
            foreach (Excel.Range area in range.Areas)
            {
                var count = (long)Convert.ToInt32(area.Rows.Count) *
                    Convert.ToInt32(area.Columns.Count);
                if (long.MaxValue - total < count) return long.MaxValue;
                total += count;
            }
            return total;
        }

        private static string ChartTitle(Excel.Chart chart)
        {
            try
            {
                return chart != null && chart.HasTitle && chart.ChartTitle != null
                    ? Convert.ToString(chart.ChartTitle.Caption)
                    : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string AxisTitle(Excel.Chart chart, Excel.XlAxisType axisType)
        {
            try
            {
                if (chart == null ||
                    !Convert.ToBoolean(chart.HasAxis[axisType, Excel.XlAxisGroup.xlPrimary]))
                    return string.Empty;
                var axis = chart.Axes(axisType, Excel.XlAxisGroup.xlPrimary) as Excel.Axis;
                return axis != null && axis.HasTitle && axis.AxisTitle != null
                    ? Convert.ToString(axis.AxisTitle.Caption)
                    : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string SafeString(Func<string> getter)
        {
            try { return getter(); }
            catch { return string.Empty; }
        }

        private static ExcelReadBackendException ReadFailure(
            string message, string code, bool retryable, string details = null)
        {
            return new ExcelReadBackendException(message, code, retryable, details);
        }

        private static ExcelWriteBackendException WriteFailure(
            string message, string code, bool retryable)
        {
            return new ExcelWriteBackendException(message, code, retryable);
        }

        private sealed class BoundedItems<T>
        {
            internal BoundedItems(List<T> items, bool truncated)
            {
                Items = items ?? new List<T>();
                Truncated = truncated;
            }

            internal List<T> Items { get; private set; }
            internal bool Truncated { get; private set; }
        }
    }
}
