using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.OfficeHosts.Identity;
using Excel = Microsoft.Office.Interop.Excel;

namespace RNAssistant.OfficeHosts
{
    internal sealed class ExcelRangeMutationInteropBackend :
        IExcelRangeMutationBackend
    {
        private readonly ExcelDocumentSession _session;
        private readonly Excel.Workbook _workbook;
        private string _lastAutoFitKey;
        private string _lastAutoFitToken;

        internal ExcelRangeMutationInteropBackend(ExcelDocumentSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _workbook = session.BoundDocumentObject as Excel.Workbook;
            if (_workbook == null)
                throw new InvalidOperationException(
                    "The bound Excel workbook is unavailable.");
        }

        public ExcelRangeMutationSnapshot Read(
            ExcelRangeMutationReadRequest request)
        {
            try
            {
                if (request == null || request.Spec == null)
                    throw new ArgumentNullException(nameof(request));
                Excel.Worksheet sheet;
                var range = ResolveRange(
                    request.Spec, request.Sheet, request.Address,
                    request.ExpectedRows, request.ExpectedColumns,
                    request.MaxCells, out sheet);
                return Snapshot(range, sheet, request.Spec);
            }
            catch (ExcelRangeMutationBackendException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw Failure(ex.Message, "office_tool_error", true);
            }
        }

        public void Apply(
            ExcelRangeMutationApplyRequest request,
            Action markDispatchPossible)
        {
            try
            {
                if (request == null || request.Spec == null)
                    throw new ArgumentNullException(nameof(request));
                if (markDispatchPossible == null)
                    throw new ArgumentNullException(nameof(markDispatchPossible));
                Excel.Worksheet sheet;
                var range = ResolveRange(
                    request.Spec, request.Sheet, request.Address,
                    request.Rows, request.Columns, request.MaxCells, out sheet);
                var current = Snapshot(range, sheet, request.Spec);
                if (!string.Equals(
                    current.StateToken, request.ExpectedStateToken,
                    StringComparison.Ordinal))
                    throw Failure(
                        "Excel range changed before dispatch.",
                        "excel_range_target_changed", false);
                ValidateSelectors(range, request.Spec);

                markDispatchPossible();
                switch (request.Spec.Kind)
                {
                    case ExcelRangeMutationKind.Clear:
                        ApplyClear(range, request.Spec);
                        break;
                    case ExcelRangeMutationKind.Sort:
                        ApplySort(range, request.Spec);
                        break;
                    case ExcelRangeMutationKind.Filter:
                        ApplyFilter(range, request.Spec);
                        break;
                    case ExcelRangeMutationKind.Format:
                        ApplyFormat(range, request.Spec);
                        RememberAutoFit(range, sheet, request.Spec);
                        break;
                    default:
                        throw Failure(
                            "Unsupported Excel range mutation.",
                            "excel_range_mutation_invalid", false);
                }
            }
            catch (ExcelRangeMutationBackendException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw Failure(ex.Message, "office_tool_error", true);
            }
        }

        private ExcelRangeMutationSnapshot Snapshot(
            Excel.Range range,
            Excel.Worksheet sheet,
            ExcelRangeMutationSpec spec)
        {
            var rows = Convert.ToInt32(range.Rows.Count);
            var columns = Convert.ToInt32(range.Columns.Count);
            var observation = new JObject
            {
                ["kind"] = spec.Kind.ToString(),
                ["sheet"] = sheet.Name,
                ["address"] = range.Address[false, false],
                ["rows"] = rows,
                ["columns"] = columns
            };
            bool satisfied;
            switch (spec.Kind)
            {
                case ExcelRangeMutationKind.Clear:
                    satisfied = ObserveClear(observation, range, spec);
                    break;
                case ExcelRangeMutationKind.Sort:
                    satisfied = ObserveSort(observation, range, spec);
                    break;
                case ExcelRangeMutationKind.Filter:
                    satisfied = ObserveFilter(observation, range, sheet, spec);
                    break;
                case ExcelRangeMutationKind.Format:
                    satisfied = ObserveFormat(observation, range, sheet, spec);
                    break;
                default:
                    throw Failure(
                        "Unsupported Excel range mutation.",
                        "excel_range_mutation_invalid", false);
            }
            return new ExcelRangeMutationSnapshot
            {
                Kind = spec.Kind,
                Sheet = sheet.Name,
                Address = range.Address[false, false],
                Rows = rows,
                Columns = columns,
                CellCount = (long)rows * columns,
                StateToken = Hash(observation.ToString(Formatting.None)),
                Satisfied = satisfied
            };
        }

        private Excel.Range ResolveRange(
            ExcelRangeMutationSpec spec,
            string sheetName,
            string address,
            int expectedRows,
            int expectedColumns,
            int maxCells,
            out Excel.Worksheet sheet)
        {
            var workbook = RequireWorkbook();
            if (maxCells < 1 ||
                maxCells > ExcelRangeMutationService.MaxMutationCells)
                throw Failure(
                    "Excel range mutation ceiling is invalid.",
                    "excel_range_bound_invalid", false);
            sheet = ResolveSheet(workbook, sheetName);
            Excel.Range range;
            if (string.IsNullOrWhiteSpace(address) &&
                spec.Kind == ExcelRangeMutationKind.Format &&
                !HasEffectiveFormatting(spec) &&
                !string.IsNullOrWhiteSpace(spec.AutoFit))
                range = sheet.UsedRange;
            else
                range = sheet.Range[string.IsNullOrWhiteSpace(address)
                    ? "A1" : address];
            if (range == null || range.Areas.Count != 1)
                throw Failure(
                    "Excel range mutation target must be one contiguous range.",
                    "excel_range_target_invalid", false);
            var rangeSheet = range.Worksheet as Excel.Worksheet;
            if (!BelongsToSession(rangeSheet))
                throw Failure(
                    "Excel range resolved outside the bound workbook.",
                    "excel_range_target_invalid", false);
            sheet = rangeSheet;
            var rows = Convert.ToInt32(range.Rows.Count);
            var columns = Convert.ToInt32(range.Columns.Count);
            var cellCount = (long)rows * columns;
            if (rows < 1 || columns < 1 || cellCount > maxCells)
                throw Failure(
                    "Excel range is too large: " + cellCount +
                    " cells. Limit is " + maxCells + ".",
                    "excel_range_too_large", false);
            if ((expectedRows > 0 || expectedColumns > 0) &&
                (rows != expectedRows || columns != expectedColumns))
                throw Failure(
                    "Excel range dimensions changed before dispatch.",
                    "excel_range_target_changed", false);
            ValidateAutoFitDimensions(range, spec);
            return range;
        }

        private bool ObserveClear(
            JObject observation,
            Excel.Range range,
            ExcelRangeMutationSpec spec)
        {
            var clearValues = spec.ClearWhat == "values" || spec.ClearWhat == "all";
            var clearFormats = spec.ClearWhat == "formats" || spec.ClearWhat == "all";
            var contentCleared = true;
            var formatsCleared = true;
            observation["clearWhat"] = spec.ClearWhat;
            if (clearValues)
            {
                var values = Matrix(range.Value2);
                var formulas = Matrix(range.Formula);
                observation["values"] = MatrixToken(values);
                observation["formulas"] = MatrixToken(formulas);
                contentCleared = values.All(row => row.All(IsBlank)) &&
                    formulas.All(row => row.All(value =>
                        IsBlank(value) || !IsFormula(value)));
            }
            if (clearFormats)
            {
                observation["format"] = FormatObservation(range, null, true);
                formatsCleared = HasNormalStyle(range);
            }
            return contentCleared && formatsCleared;
        }

        private static bool ObserveSort(
            JObject observation,
            Excel.Range range,
            ExcelRangeMutationSpec spec)
        {
            var values = Matrix(range.Value2);
            observation["keyColumn"] = spec.KeyColumn;
            observation["descending"] = spec.Descending;
            observation["hasHeaders"] = spec.HasHeaders;
            observation["values"] = MatrixToken(values);
            observation["formulas"] = MatrixToken(Matrix(range.Formula));
            return IsSorted(
                values, spec.KeyColumn - 1,
                spec.Descending, spec.HasHeaders);
        }

        private static bool ObserveFilter(
            JObject observation,
            Excel.Range range,
            Excel.Worksheet sheet,
            ExcelRangeMutationSpec spec)
        {
            observation["field"] = spec.Field;
            observation["criteria"] = spec.Criteria ?? string.Empty;
            var filterRange = string.Empty;
            var enabled = false;
            var fieldOn = false;
            object criteria1 = null;
            try
            {
                enabled = sheet.AutoFilterMode;
                var autoFilter = sheet.AutoFilter;
                var observedRange = autoFilter == null
                    ? null : autoFilter.Range as Excel.Range;
                filterRange = observedRange == null
                    ? string.Empty : observedRange.Address[false, false];
                if (autoFilter != null && spec.Field > 0 &&
                    spec.Field <= autoFilter.Filters.Count)
                {
                    var filter = autoFilter.Filters[spec.Field] as Excel.Filter;
                    if (filter != null)
                    {
                        fieldOn = filter.On;
                        if (fieldOn) criteria1 = filter.Criteria1;
                    }
                }
            }
            catch
            {
                enabled = false;
                filterRange = string.Empty;
                fieldOn = false;
                criteria1 = null;
            }
            observation["filterEnabled"] = enabled;
            observation["filterRange"] = filterRange;
            observation["fieldOn"] = fieldOn;
            observation["criteria1"] = Canonical(criteria1);
            var exactRange = string.Equals(
                filterRange, range.Address[false, false],
                StringComparison.OrdinalIgnoreCase);
            return enabled && exactRange &&
                (string.IsNullOrWhiteSpace(spec.Criteria)
                    ? !fieldOn
                    : fieldOn && CriteriaMatches(criteria1, spec.Criteria));
        }

        private bool ObserveFormat(
            JObject observation,
            Excel.Range range,
            Excel.Worksheet sheet,
            ExcelRangeMutationSpec spec)
        {
            observation["format"] = FormatObservation(range, spec, false);
            var dimensions = DimensionObservation(range, spec.AutoFit);
            observation["dimensions"] = dimensions;
            var formattingSatisfied = FormatSatisfied(range, spec);
            var autoFitSatisfied = string.IsNullOrWhiteSpace(spec.AutoFit) ||
                string.Equals(
                    _lastAutoFitKey, AutoFitKey(range, sheet, spec),
                    StringComparison.Ordinal) &&
                string.Equals(
                    _lastAutoFitToken,
                    Hash(dimensions.ToString(Formatting.None)),
                    StringComparison.Ordinal);
            return formattingSatisfied && autoFitSatisfied;
        }

        private static JToken FormatObservation(
            Excel.Range range,
            ExcelRangeMutationSpec spec,
            bool includeAll)
        {
            var result = new JObject();
            if (includeAll || spec != null && spec.HasNumberFormat)
                result["numberFormat"] = Canonical(range.NumberFormat);
            if (includeAll || spec != null && spec.HasBold)
                result["bold"] = Canonical(range.Font.Bold);
            if (includeAll || spec != null && spec.HasItalic)
                result["italic"] = Canonical(range.Font.Italic);
            if (includeAll || spec != null && spec.HasFillColor)
                result["fillColor"] = Canonical(range.Interior.Color);
            if (includeAll || spec != null && spec.HasFontColor)
                result["fontColor"] = Canonical(range.Font.Color);
            if (includeAll || spec != null && spec.HasHorizontalAlignment)
                result["horizontalAlignment"] =
                    Canonical(range.HorizontalAlignment);
            if (includeAll) result["style"] = Canonical(range.Style);
            return result;
        }

        private static JArray DimensionObservation(
            Excel.Range range, string autoFit)
        {
            var result = new JArray();
            if (autoFit == "columns" || autoFit == "both")
            {
                var columns = Convert.ToInt32(range.Columns.Count);
                for (var index = 1; index <= columns; index++)
                {
                    var column = range.Columns[index] as Excel.Range;
                    result.Add(new JObject
                    {
                        ["axis"] = "column",
                        ["index"] = index,
                        ["size"] = Canonical(column == null
                            ? null : column.ColumnWidth)
                    });
                }
            }
            if (autoFit == "rows" || autoFit == "both")
            {
                var rows = Convert.ToInt32(range.Rows.Count);
                for (var index = 1; index <= rows; index++)
                {
                    var row = range.Rows[index] as Excel.Range;
                    result.Add(new JObject
                    {
                        ["axis"] = "row",
                        ["index"] = index,
                        ["size"] = Canonical(row == null
                            ? null : row.RowHeight)
                    });
                }
            }
            return result;
        }

        private bool FormatSatisfied(
            Excel.Range range, ExcelRangeMutationSpec spec)
        {
            if (spec.HasNumberFormat &&
                !string.IsNullOrWhiteSpace(spec.NumberFormat) &&
                !ScalarEquals(range.NumberFormat, spec.NumberFormat)) return false;
            if (spec.HasBold && !ScalarEquals(range.Font.Bold, spec.Bold)) return false;
            if (spec.HasItalic && !ScalarEquals(range.Font.Italic, spec.Italic)) return false;
            if (spec.HasFillColor && !string.IsNullOrWhiteSpace(spec.FillColor) &&
                !ScalarEquals(range.Interior.Color, OleColor(spec.FillColor))) return false;
            if (spec.HasFontColor && !string.IsNullOrWhiteSpace(spec.FontColor) &&
                !ScalarEquals(range.Font.Color, OleColor(spec.FontColor))) return false;
            if (spec.HasHorizontalAlignment &&
                !string.IsNullOrWhiteSpace(spec.HorizontalAlignment) &&
                !ScalarEquals(
                    range.HorizontalAlignment,
                    (int)ResolveHorizontalAlignment(spec.HorizontalAlignment))) return false;
            return true;
        }

        private bool HasNormalStyle(Excel.Range range)
        {
            var observed = Convert.ToString(range.Style, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(observed)) return false;
            try
            {
                var normal = _workbook.Styles["Normal"] as Excel.Style;
                return normal != null &&
                    (string.Equals(observed, normal.Name,
                        StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(observed, normal.NameLocal,
                        StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return string.Equals(
                    observed, "Normal", StringComparison.OrdinalIgnoreCase);
            }
        }

        private static void ApplyClear(
            Excel.Range range, ExcelRangeMutationSpec spec)
        {
            if (spec.ClearWhat == "formats") range.ClearFormats();
            else if (spec.ClearWhat == "all") range.Clear();
            else range.ClearContents();
        }

        private static void ApplySort(
            Excel.Range range, ExcelRangeMutationSpec spec)
        {
            var key = range.Columns[spec.KeyColumn] as Excel.Range;
            if (key == null)
                throw Failure(
                    "Sort key column could not be resolved.",
                    "excel_sort_key_unavailable", false);
            range.Sort(
                Key1: key,
                Order1: spec.Descending
                    ? Excel.XlSortOrder.xlDescending
                    : Excel.XlSortOrder.xlAscending,
                Header: spec.HasHeaders
                    ? Excel.XlYesNoGuess.xlYes
                    : Excel.XlYesNoGuess.xlNo,
                Orientation: Excel.XlSortOrientation.xlSortColumns);
        }

        private static void ApplyFilter(
            Excel.Range range, ExcelRangeMutationSpec spec)
        {
            range.AutoFilter(
                spec.Field,
                string.IsNullOrWhiteSpace(spec.Criteria)
                    ? Type.Missing : (object)spec.Criteria,
                Excel.XlAutoFilterOperator.xlAnd,
                Type.Missing,
                true);
        }

        private static void ApplyFormat(
            Excel.Range range, ExcelRangeMutationSpec spec)
        {
            if (spec.HasNumberFormat &&
                !string.IsNullOrWhiteSpace(spec.NumberFormat))
                range.NumberFormat = spec.NumberFormat;
            if (spec.HasBold) range.Font.Bold = spec.Bold;
            if (spec.HasItalic) range.Font.Italic = spec.Italic;
            if (spec.HasFillColor &&
                !string.IsNullOrWhiteSpace(spec.FillColor))
                range.Interior.Color = OleColor(spec.FillColor);
            if (spec.HasFontColor &&
                !string.IsNullOrWhiteSpace(spec.FontColor))
                range.Font.Color = OleColor(spec.FontColor);
            if (spec.HasHorizontalAlignment &&
                !string.IsNullOrWhiteSpace(spec.HorizontalAlignment))
                range.HorizontalAlignment =
                    ResolveHorizontalAlignment(spec.HorizontalAlignment);
            if (spec.AutoFit == "columns" || spec.AutoFit == "both")
                range.Columns.AutoFit();
            if (spec.AutoFit == "rows" || spec.AutoFit == "both")
                range.Rows.AutoFit();
        }

        private void RememberAutoFit(
            Excel.Range range,
            Excel.Worksheet sheet,
            ExcelRangeMutationSpec spec)
        {
            if (string.IsNullOrWhiteSpace(spec.AutoFit)) return;
            _lastAutoFitKey = AutoFitKey(range, sheet, spec);
            _lastAutoFitToken = Hash(
                DimensionObservation(range, spec.AutoFit)
                    .ToString(Formatting.None));
        }

        private static void ValidateSelectors(
            Excel.Range range, ExcelRangeMutationSpec spec)
        {
            var columns = Convert.ToInt32(range.Columns.Count);
            if (spec.Kind == ExcelRangeMutationKind.Sort &&
                (spec.KeyColumn < 1 || spec.KeyColumn > columns))
                throw Failure(
                    "keyColumn is outside the sort range.",
                    "excel_sort_key_out_of_range", false);
            if (spec.Kind == ExcelRangeMutationKind.Filter &&
                (spec.Field < 1 || spec.Field > columns))
                throw Failure(
                    "field is outside the filter range.",
                    "excel_filter_field_out_of_range", false);
        }

        private static void ValidateAutoFitDimensions(
            Excel.Range range, ExcelRangeMutationSpec spec)
        {
            if (spec.Kind != ExcelRangeMutationKind.Format) return;
            var columns = Convert.ToInt32(range.Columns.Count);
            var rows = Convert.ToInt32(range.Rows.Count);
            if ((spec.AutoFit == "columns" || spec.AutoFit == "both") &&
                columns > ExcelRangeMutationService.MaxAutoFitDimensions ||
                (spec.AutoFit == "rows" || spec.AutoFit == "both") &&
                rows > ExcelRangeMutationService.MaxAutoFitDimensions)
                throw Failure(
                    "Excel autoFit target has too many row or column dimensions.",
                    "excel_range_autofit_too_large", false);
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

        private static bool HasEffectiveFormatting(ExcelRangeMutationSpec spec)
        {
            return spec.HasBold || spec.HasItalic ||
                spec.HasNumberFormat && !string.IsNullOrWhiteSpace(spec.NumberFormat) ||
                spec.HasFillColor && !string.IsNullOrWhiteSpace(spec.FillColor) ||
                spec.HasFontColor && !string.IsNullOrWhiteSpace(spec.FontColor) ||
                spec.HasHorizontalAlignment &&
                    !string.IsNullOrWhiteSpace(spec.HorizontalAlignment);
        }

        private static List<List<object>> Matrix(object value)
        {
            var rows = new List<List<object>>();
            var array = value as object[,];
            if (array == null)
            {
                rows.Add(new List<object> { value });
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

        private static JArray MatrixToken(
            IReadOnlyList<List<object>> matrix)
        {
            var result = new JArray();
            foreach (var row in matrix)
            {
                var line = new JArray();
                foreach (var value in row) line.Add(Canonical(value));
                result.Add(line);
            }
            return result;
        }

        private static bool IsSorted(
            IReadOnlyList<List<object>> values,
            int keyColumn,
            bool descending,
            bool hasHeaders)
        {
            var start = hasHeaders ? 1 : 0;
            if (values == null || values.Count <= start + 1) return true;
            for (var row = start + 1; row < values.Count; row++)
            {
                if (keyColumn < 0 || keyColumn >= values[row - 1].Count ||
                    keyColumn >= values[row].Count) return false;
                var leftBlank = IsBlank(values[row - 1][keyColumn]);
                var rightBlank = IsBlank(values[row][keyColumn]);
                if (leftBlank && !rightBlank) return false;
                if (rightBlank) continue;
                var comparison = CompareExcelValues(
                    values[row - 1][keyColumn], values[row][keyColumn]);
                if (comparison == int.MinValue) return false;
                if ((!descending && comparison > 0) ||
                    (descending && comparison < 0)) return false;
            }
            return true;
        }

        private static int CompareExcelValues(object left, object right)
        {
            if (IsBlank(left) && IsBlank(right)) return 0;
            if (IsBlank(left)) return 1;
            if (IsBlank(right)) return -1;
            decimal leftNumber;
            decimal rightNumber;
            if (decimal.TryParse(
                    Convert.ToString(left, CultureInfo.InvariantCulture),
                    NumberStyles.Float, CultureInfo.InvariantCulture,
                    out leftNumber) &&
                decimal.TryParse(
                    Convert.ToString(right, CultureInfo.InvariantCulture),
                    NumberStyles.Float, CultureInfo.InvariantCulture,
                    out rightNumber))
                return leftNumber.CompareTo(rightNumber);
            try
            {
                return string.Compare(
                    Convert.ToString(left, CultureInfo.CurrentCulture),
                    Convert.ToString(right, CultureInfo.CurrentCulture),
                    true, CultureInfo.CurrentCulture);
            }
            catch
            {
                return int.MinValue;
            }
        }

        private static bool IsFormula(object value)
        {
            var text = value as string;
            return text != null && text.StartsWith("=", StringComparison.Ordinal);
        }

        private static bool IsBlank(object value)
        {
            return value == null || value == DBNull.Value ||
                value is string && ((string)value).Length == 0;
        }

        private static bool CriteriaMatches(object observed, string expected)
        {
            var value = Convert.ToString(
                observed, CultureInfo.InvariantCulture) ?? string.Empty;
            expected = expected ?? string.Empty;
            if (string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
                return true;
            return value.StartsWith("=", StringComparison.Ordinal) &&
                !expected.StartsWith("=", StringComparison.Ordinal) &&
                string.Equals(value.Substring(1), expected,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool ScalarEquals(object observed, object expected)
        {
            if (observed == null || observed == DBNull.Value) return false;
            if (expected is bool)
            {
                try { return Convert.ToBoolean(observed) == (bool)expected; }
                catch { return false; }
            }
            if (expected is int)
            {
                try { return Convert.ToInt32(observed) == (int)expected; }
                catch { return false; }
            }
            return string.Equals(
                Convert.ToString(observed, CultureInfo.InvariantCulture),
                Convert.ToString(expected, CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
        }

        private static JToken Canonical(object value)
        {
            if (value == null || value == DBNull.Value) return JValue.CreateNull();
            if (value is DateTime)
                return new JObject
                {
                    ["type"] = "date",
                    ["value"] = ((DateTime)value).ToString(
                        "O", CultureInfo.InvariantCulture)
                };
            if (value is string || value is bool || value is byte ||
                value is sbyte || value is short || value is ushort ||
                value is int || value is uint || value is long ||
                value is ulong || value is float || value is double ||
                value is decimal)
                return JToken.FromObject(value);
            return new JObject
            {
                ["type"] = value.GetType().FullName,
                ["value"] = Convert.ToString(value, CultureInfo.InvariantCulture)
            };
        }

        private static string AutoFitKey(
            Excel.Range range,
            Excel.Worksheet sheet,
            ExcelRangeMutationSpec spec)
        {
            return sheet.Name + "!" + range.Address[false, false] + "|" +
                (spec.AutoFit ?? string.Empty);
        }

        private static Excel.XlHAlign ResolveHorizontalAlignment(string value)
        {
            switch ((value ?? string.Empty).ToLowerInvariant())
            {
                case "left": return Excel.XlHAlign.xlHAlignLeft;
                case "right": return Excel.XlHAlign.xlHAlignRight;
                case "center":
                case "centre": return Excel.XlHAlign.xlHAlignCenter;
                default: return Excel.XlHAlign.xlHAlignGeneral;
            }
        }

        private static int OleColor(string value)
        {
            var text = (value ?? string.Empty).Trim().TrimStart('#');
            int rgb;
            if (text.Length != 6 || !int.TryParse(
                text, NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out rgb)) return 0;
            var red = (rgb >> 16) & 0xFF;
            var green = (rgb >> 8) & 0xFF;
            var blue = rgb & 0xFF;
            return red + (green << 8) + (blue << 16);
        }

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(
                    sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)));
        }

        private static ExcelRangeMutationBackendException Failure(
            string message, string code, bool retryable)
        {
            return new ExcelRangeMutationBackendException(
                message, code, retryable);
        }
    }
}
