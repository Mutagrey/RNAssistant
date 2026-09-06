using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Office.Domains.Excel
{
    public sealed class ExcelReadService
    {
        public const int MaxReadCells = 100000;
        public const int MaxInspectItems = 200;
        public const int MaxChartSeries = 100;

        private readonly IExcelReadBackend _backend;

        public ExcelReadService(IExcelReadBackend backend)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        public ExcelReadOutcome Inspect(string kind, string sheet, string chartName)
        {
            kind = (kind ?? string.Empty).Trim().ToLowerInvariant();
            if (!new[] { "workbook", "sheets", "charts", "tables", "names", "shapes" }.Contains(kind, StringComparer.Ordinal))
                return Failure("kind must be workbook, sheets, charts, tables, names, or shapes.", "excel_inspect_kind_invalid", false);
            if (!string.Equals(kind, "charts", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(chartName))
                return Failure("chartName is valid only when kind is charts.", "excel_inspect_arguments_invalid", false);
            if (kind != "charts" && kind != "tables" && kind != "shapes" && !string.IsNullOrWhiteSpace(sheet))
                return Failure("sheet is valid only when kind is charts, tables, or shapes.", "excel_inspect_arguments_invalid", false);

            try
            {
                var snapshot = _backend.Inspect(new ExcelInspectRequest
                {
                    Kind = kind,
                    Sheet = sheet ?? string.Empty,
                    ChartName = chartName ?? string.Empty,
                    MaxItems = MaxInspectItems,
                    MaxSeries = MaxChartSeries
                });
                ValidateInspect(snapshot, kind, !string.IsNullOrWhiteSpace(chartName));
                var output = InspectOutput(snapshot);
                return ExcelReadOutcome.Ok(InspectMessage(snapshot), output.ToString(Formatting.None));
            }
            catch (ExcelReadBackendException ex)
            {
                return Failure(ex.Message, ex.ErrorCode, ex.Retryable, ex.DetailsJson);
            }
            catch (Exception ex)
            {
                return Failure("Excel inspection failed: " + ex.Message, "excel_read_failed", true);
            }
        }

        internal ExcelRangeSnapshot CaptureRange(string sheet, string address, string content)
        {
            if (content != "values" && content != "formulas" && content != "profile")
                throw new ExcelReadBackendException("Unsupported range view.", "excel_range_content_invalid", false);
            var snapshot = _backend.ReadRange(new ExcelRangeReadRequest {
                Sheet = sheet ?? string.Empty, Address = address ?? string.Empty,
                Content = content, MaxCells = MaxReadCells });
            ValidateRange(snapshot, content);
            return snapshot;
        }

        internal ExcelInspectSnapshot CaptureStructure(string kind)
        {
            if (kind != "sheets" && kind != "tables")
                throw new ExcelReadBackendException("Unsupported structure view.", "excel_inspect_kind_invalid", false);
            var snapshot = _backend.Inspect(new ExcelInspectRequest { Kind = kind, MaxItems = MaxInspectItems, MaxSeries = MaxChartSeries });
            ValidateInspect(snapshot, kind, false);
            return snapshot;
        }

        private static void ValidateInspect(ExcelInspectSnapshot snapshot, string kind, bool chartDetail)
        {
            if (snapshot == null) throw InvalidBackend("Excel inspection returned no snapshot.");
            if (!string.Equals(snapshot.Kind, kind, StringComparison.Ordinal))
                throw InvalidBackend("Excel inspection returned a different selector.");
            var count = 0;
            switch (kind)
            {
                case "workbook":
                    if (snapshot.Workbook == null) throw InvalidBackend("Workbook inspection returned no workbook metadata.");
                    if (string.IsNullOrWhiteSpace(snapshot.Workbook.Name) || snapshot.Workbook.Sheets == null)
                        throw InvalidBackend("Workbook inspection returned incomplete metadata.");
                    ValidateItems(snapshot.Workbook.Sheets, item => item != null &&
                        !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.UsedRange));
                    count = Count(snapshot.Workbook.Sheets);
                    break;
                case "sheets":
                    if (snapshot.Sheets == null) throw InvalidBackend("Sheet inspection returned no collection.");
                    ValidateItems(snapshot.Sheets, item => item != null && !string.IsNullOrWhiteSpace(item.Name));
                    count = Count(snapshot.Sheets);
                    break;
                case "charts":
                    if (chartDetail)
                    {
                        if (snapshot.Chart == null) throw InvalidBackend("Chart inspection returned no chart metadata.");
                        ValidateSeries(snapshot.Chart);
                        count = 1;
                    }
                    else
                    {
                        if (snapshot.Charts == null) throw InvalidBackend("Chart inspection returned no collection.");
                        foreach (var chart in snapshot.Charts) ValidateSeries(chart);
                        count = Count(snapshot.Charts);
                    }
                    break;
                case "tables":
                    if (snapshot.Tables == null) throw InvalidBackend("Table inspection returned no collection.");
                    ValidateItems(snapshot.Tables, item => item != null &&
                        !string.IsNullOrWhiteSpace(item.Sheet) && !string.IsNullOrWhiteSpace(item.Name) &&
                        !string.IsNullOrWhiteSpace(item.Range) && item.Rows >= 0 && item.Columns >= 0);
                    count = Count(snapshot.Tables);
                    break;
                case "names":
                    if (snapshot.Names == null) throw InvalidBackend("Defined-name inspection returned no collection.");
                    ValidateItems(snapshot.Names, item => item != null &&
                        !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.RefersTo));
                    count = Count(snapshot.Names);
                    break;
                case "shapes":
                    if (snapshot.Shapes == null) throw InvalidBackend("Shape inspection returned no collection.");
                    ValidateItems(snapshot.Shapes, item => item != null &&
                        !string.IsNullOrWhiteSpace(item.Sheet) && !string.IsNullOrWhiteSpace(item.Name) &&
                        !string.IsNullOrWhiteSpace(item.Type));
                    count = Count(snapshot.Shapes);
                    break;
            }
            if (count > MaxInspectItems || snapshot.ReturnedCount != count)
                throw InvalidBackend("Excel inspection returned an inconsistent or oversized collection.");
        }

        private static void ValidateSeries(ExcelChartSnapshot chart)
        {
            if (chart == null) throw InvalidBackend("Excel inspection returned an empty chart item.");
            if (string.IsNullOrWhiteSpace(chart.Sheet) || string.IsNullOrWhiteSpace(chart.Name))
                throw InvalidBackend("Excel inspection returned incomplete chart metadata.");
            if (chart.Series == null) throw InvalidBackend("Excel inspection returned no chart-series collection.");
            ValidateItems(chart.Series, item => item != null);
            if (Count(chart.Series) > MaxChartSeries)
                throw InvalidBackend("Excel inspection returned too many chart series.");
        }

        private static void ValidateRange(ExcelRangeSnapshot snapshot, string content)
        {
            if (snapshot == null) throw InvalidBackend("Excel range read returned no snapshot.");
            if (string.IsNullOrWhiteSpace(snapshot.Sheet) || string.IsNullOrWhiteSpace(snapshot.Address))
                throw InvalidBackend("Excel range read returned incomplete coordinates.");
            if (snapshot.Rows < 0 || snapshot.Columns < 0 || snapshot.CellCount < 0 || snapshot.CellCount > MaxReadCells)
                throw InvalidBackend("Excel range read returned invalid dimensions.");
            var expected = (long)snapshot.Rows * snapshot.Columns;
            if (expected != snapshot.CellCount)
                throw InvalidBackend("Excel range dimensions do not match the reported cell count.");
            if ((content == "values" || content == "profile") && !MatrixMatches(snapshot.Values, snapshot.Rows, snapshot.Columns))
                throw InvalidBackend("Excel value matrix does not match the reported dimensions.");
            if ((content == "formulas" || content == "profile") && !MatrixMatches(snapshot.Formulas, snapshot.Rows, snapshot.Columns))
                throw InvalidBackend("Excel formula matrix does not match the reported dimensions.");
        }

        private static bool MatrixMatches(IReadOnlyList<List<object>> matrix, int rows, int columns)
        {
            if (matrix == null || matrix.Count != rows) return false;
            return matrix.All(row => row != null && row.Count == columns);
        }

        private static JObject InspectOutput(ExcelInspectSnapshot snapshot)
        {
            var root = new JObject
            {
                ["kind"] = snapshot.Kind,
                ["returnedCount"] = snapshot.ReturnedCount,
                ["truncated"] = snapshot.Truncated
            };
            switch (snapshot.Kind)
            {
                case "workbook": root["workbook"] = JToken.FromObject(snapshot.Workbook); break;
                case "sheets": root["items"] = JToken.FromObject(snapshot.Sheets ?? new List<ExcelSheetSnapshot>()); break;
                case "charts":
                    if (snapshot.Chart != null) root["item"] = JToken.FromObject(snapshot.Chart);
                    else root["items"] = JToken.FromObject(snapshot.Charts ?? new List<ExcelChartSnapshot>());
                    break;
                case "tables": root["items"] = JToken.FromObject(snapshot.Tables ?? new List<ExcelTableSnapshot>()); break;
                case "names": root["items"] = JToken.FromObject(snapshot.Names ?? new List<ExcelNameSnapshot>()); break;
                case "shapes": root["items"] = JToken.FromObject(snapshot.Shapes ?? new List<ExcelShapeSnapshot>()); break;
            }
            return root;
        }

        internal static JObject ProfileOutput(ExcelRangeSnapshot snapshot)
        {
            var root = new JObject
            {
                ["sheet"] = snapshot.Sheet ?? string.Empty,
                ["address"] = snapshot.Address ?? string.Empty,
                ["content"] = "profile",
                ["rows"] = snapshot.Rows,
                ["columns"] = snapshot.Columns,
                ["cellCount"] = snapshot.CellCount
            };
            AddProfile(root, snapshot);
            return root;
        }

        private static void AddProfile(JObject root, ExcelRangeSnapshot snapshot)
        {
            var blankCells = 0;
            var formulaCells = 0;
            var numericColumns = new JArray();
            for (var column = 0; column < snapshot.Columns; column++)
            {
                var numeric = 0;
                var nonBlank = 0;
                for (var row = 0; row < snapshot.Rows; row++)
                {
                    var value = snapshot.Values[row][column];
                    if (IsBlank(value)) blankCells++;
                    else
                    {
                        nonBlank++;
                        if (IsNumeric(value)) numeric++;
                    }
                    var formula = Convert.ToString(snapshot.Formulas[row][column]);
                    if (!string.IsNullOrWhiteSpace(formula) && formula.StartsWith("=", StringComparison.Ordinal)) formulaCells++;
                }
                if (nonBlank > 0 && numeric == nonBlank)
                {
                    numericColumns.Add(new JObject
                    {
                        ["index"] = column + 1,
                        ["header"] = snapshot.Rows == 0 ? string.Empty : Convert.ToString(snapshot.Values[0][column]),
                        ["nonBlank"] = nonBlank
                    });
                }
            }
            root["blankCells"] = blankCells;
            root["formulaCells"] = formulaCells;
            root["headers"] = snapshot.Rows == 0
                ? new JArray()
                : JToken.FromObject(snapshot.Values[0].Select(Convert.ToString).ToArray());
            root["numericColumns"] = numericColumns;
            root["sample"] = JToken.FromObject(snapshot.Values.Take(10).ToArray());
        }

        private static bool IsBlank(object value)
        {
            return value == null || string.IsNullOrWhiteSpace(Convert.ToString(value));
        }

        private static bool IsNumeric(object value)
        {
            return value is byte || value is sbyte || value is short || value is ushort ||
                value is int || value is uint || value is long || value is ulong ||
                value is float || value is double || value is decimal;
        }

        private static string InspectMessage(ExcelInspectSnapshot snapshot)
        {
            return "Excel " + snapshot.Kind + " inspected: " + snapshot.ReturnedCount +
                (snapshot.Truncated ? " item(s), truncated at the configured bound." : " item(s).");
        }

        private static ExcelReadBackendException InvalidBackend(string message)
        {
            return new ExcelReadBackendException(message, "excel_read_snapshot_invalid", false);
        }

        private static int Count<T>(ICollection<T> items)
        {
            return items == null ? 0 : items.Count;
        }

        private static void ValidateItems<T>(IEnumerable<T> items, Func<T, bool> valid)
        {
            if (items == null || valid == null || items.Any(item => !valid(item)))
                throw InvalidBackend("Excel inspection returned an incomplete collection item.");
        }

        private static ExcelReadOutcome Failure(string message, string code, bool retryable, string detailsJson = null)
        {
            var data = new JObject { ["code"] = code, ["retryable"] = retryable };
            if (!string.IsNullOrWhiteSpace(detailsJson))
            {
                try { data["details"] = JToken.Parse(detailsJson); }
                catch (JsonException) { data["details"] = detailsJson; }
            }
            return ExcelReadOutcome.Fail(message, data.ToString(Formatting.None), code, retryable);
        }
    }
}
