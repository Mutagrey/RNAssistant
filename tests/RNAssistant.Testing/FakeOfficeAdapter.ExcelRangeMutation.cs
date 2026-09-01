using RNAssistant.Core.Tools;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Domains.Excel;

namespace RNAssistant.Harness
{
    internal sealed partial class FakeOfficeAdapter
    {
        public ExcelRangeMutationSnapshot Read(
            ExcelRangeMutationReadRequest request)
        {
            BeginExcelBackendCall(ExcelRangeMutationReadOperation);
            var snapshot = CreateRangeMutationSnapshot(request);
            var transform = ExcelRangeMutationReadTransform;
            return transform == null ? snapshot : transform(snapshot);
        }

        public void Apply(
            ExcelRangeMutationApplyRequest request,
            Action markDispatchPossible)
        {
            BeginExcelBackendCall(ExcelRangeMutationApplyOperation);
            if (request == null || request.Spec == null)
                throw new ArgumentNullException(nameof(request));
            if (markDispatchPossible == null)
                throw new ArgumentNullException(nameof(markDispatchPossible));
            ExcelRangeMutationRequests.Add(RangeMutationCommand(request.Spec));
            ThrowQueuedExcelRangeMutationFailure();
            var current = CreateRangeMutationSnapshot(
                new ExcelRangeMutationReadRequest
                {
                    Spec = request.Spec,
                    Sheet = request.Sheet,
                    Address = request.Address,
                    ExpectedRows = request.Rows,
                    ExpectedColumns = request.Columns,
                    MaxCells = request.MaxCells
                });
            if (!string.Equals(
                current.StateToken, request.ExpectedStateToken,
                StringComparison.Ordinal))
                throw RangeMutationFailure(
                    "range changed", "excel_range_target_changed");

            markDispatchPossible();
            var sheet = ResolveFakeRangeSheet(request.Sheet);
            var bounds = ParseRange(request.Address);
            switch (request.Spec.Kind)
            {
                case ExcelRangeMutationKind.Clear:
                    ApplyFakeClear(sheet, bounds, request.Address, request.Spec);
                    break;
                case ExcelRangeMutationKind.Sort:
                    ApplyFakeSort(sheet, bounds, request.Spec);
                    break;
                case ExcelRangeMutationKind.Filter:
                    _excelRangeFilters[RangeMutationKey(
                        sheet.Name, request.Address)] =
                        FilterState(request.Spec);
                    break;
                case ExcelRangeMutationKind.Format:
                    ApplyFakeFormat(sheet.Name, request.Address, request.Spec);
                    break;
                default:
                    throw RangeMutationFailure(
                        "unsupported mutation", "excel_range_mutation_invalid");
            }
            if (ExcelRangeMutationThrowAfterMutation)
            {
                ExcelRangeMutationThrowAfterMutation = false;
                throw new InvalidOperationException(
                    "scripted failure after Excel range mutation");
            }
        }

        internal void SetExcelCellForTest(
            string sheetName, string address, object value)
        {
            var sheet = EnsureSheet(sheetName);
            var cell = ParseAddress(address);
            var key = CellKey(cell.Row, cell.Column);
            if (value == null) sheet.Cells.Remove(key);
            else sheet.Cells[key] = value;
            sheet.FormulaCells.Remove(key);
        }

        internal string ExcelCellText(string sheetName, string address)
        {
            var sheet = EnsureSheet(sheetName);
            var cell = ParseAddress(address);
            object value;
            return sheet.Cells.TryGetValue(CellKey(cell.Row, cell.Column), out value)
                ? Convert.ToString(value, CultureInfo.InvariantCulture)
                : string.Empty;
        }

        internal bool HasExcelRangeFormat(string sheetName, string address)
        {
            return _excelRangeFormats.ContainsKey(
                RangeMutationKey(sheetName, address));
        }

        private ExcelRangeMutationSnapshot CreateRangeMutationSnapshot(
            ExcelRangeMutationReadRequest request)
        {
            if (request == null || request.Spec == null)
                throw new ArgumentNullException(nameof(request));
            if (request.MaxCells < 1 ||
                request.MaxCells > ExcelRangeMutationService.MaxMutationCells)
                throw RangeMutationFailure(
                    "invalid range bound", "excel_range_bound_invalid");
            var sheet = ResolveFakeRangeSheet(request.Sheet);
            var address = ResolveFakeMutationAddress(
                sheet, request.Address, request.Spec);
            var bounds = ParseRange(address);
            var rows = bounds.End.Row - bounds.Start.Row + 1;
            var columns = bounds.End.Column - bounds.Start.Column + 1;
            var cellCount = (long)rows * columns;
            if (rows < 1 || columns < 1 || cellCount > request.MaxCells)
                throw RangeMutationFailure(
                    "range is too large", "excel_range_too_large");
            if ((request.ExpectedRows > 0 || request.ExpectedColumns > 0) &&
                (rows != request.ExpectedRows || columns != request.ExpectedColumns))
                throw RangeMutationFailure(
                    "range dimensions changed", "excel_range_target_changed");
            return new ExcelRangeMutationSnapshot
            {
                Kind = request.Spec.Kind,
                Sheet = sheet.Name,
                Address = address,
                Rows = rows,
                Columns = columns,
                CellCount = cellCount,
                StateToken = FakeRangeMutationState(
                    sheet, bounds, address, request.Spec),
                Satisfied = FakeRangeMutationSatisfied(
                    sheet, bounds, address, request.Spec)
            };
        }

        private FakeSheet ResolveFakeRangeSheet(string sheetName)
        {
            if (string.IsNullOrWhiteSpace(sheetName))
            {
                FakeSheet active;
                if (!string.IsNullOrWhiteSpace(_activeExcelSheetName) &&
                    _sheets.TryGetValue(_activeExcelSheetName, out active))
                    return active;
                return _sheets.Values.FirstOrDefault() ?? EnsureSheet("Sheet1");
            }
            FakeSheet sheet;
            if (!_sheets.TryGetValue(sheetName, out sheet))
                throw RangeMutationFailure(
                    "worksheet not found", "excel_sheet_not_found");
            return sheet;
        }

        private static string ResolveFakeMutationAddress(
            FakeSheet sheet,
            string address,
            ExcelRangeMutationSpec spec)
        {
            if (!string.IsNullOrWhiteSpace(address)) return address;
            if (spec.Kind != ExcelRangeMutationKind.Format ||
                HasFakeEffectiveFormatting(spec) ||
                string.IsNullOrWhiteSpace(spec.AutoFit)) return "A1";
            if (sheet.Cells.Count == 0) return "A1";
            var cells = sheet.Cells.Keys.Select(ParseCellKey).ToArray();
            var maxRow = cells.Max(cell => cell.Row);
            var maxColumn = cells.Max(cell => cell.Column);
            return "A1:" + ColumnLetters(maxColumn) + maxRow;
        }

        private string FakeRangeMutationState(
            FakeSheet sheet,
            FakeRange bounds,
            string address,
            ExcelRangeMutationSpec spec)
        {
            var root = new JObject
            {
                ["kind"] = spec.Kind.ToString(),
                ["sheet"] = sheet.Name,
                ["address"] = address
            };
            var key = RangeMutationKey(sheet.Name, address);
            if (spec.Kind == ExcelRangeMutationKind.Clear ||
                spec.Kind == ExcelRangeMutationKind.Sort)
                root["cells"] = FakeCellState(sheet, bounds);
            if (spec.Kind == ExcelRangeMutationKind.Clear &&
                (spec.ClearWhat == "formats" || spec.ClearWhat == "all") ||
                spec.Kind == ExcelRangeMutationKind.Format)
            {
                string format;
                root["format"] = _excelRangeFormats.TryGetValue(key, out format)
                    ? format : string.Empty;
            }
            if (spec.Kind == ExcelRangeMutationKind.Filter)
            {
                string filter;
                root["filter"] = _excelRangeFilters.TryGetValue(key, out filter)
                    ? filter : string.Empty;
            }
            if (spec.Kind == ExcelRangeMutationKind.Format)
            {
                string autoFit;
                root["autoFit"] = _excelRangeAutoFits.TryGetValue(key, out autoFit)
                    ? autoFit : string.Empty;
            }
            return root.ToString(Formatting.None);
        }

        private bool FakeRangeMutationSatisfied(
            FakeSheet sheet,
            FakeRange bounds,
            string address,
            ExcelRangeMutationSpec spec)
        {
            var key = RangeMutationKey(sheet.Name, address);
            switch (spec.Kind)
            {
                case ExcelRangeMutationKind.Clear:
                    var valuesClear = spec.ClearWhat == "formats" ||
                        FakeRangeIsEmpty(sheet, bounds);
                    var formatsClear = spec.ClearWhat == "values" ||
                        !_excelRangeFormats.ContainsKey(key);
                    return valuesClear && formatsClear;
                case ExcelRangeMutationKind.Sort:
                    return FakeRangeIsSorted(sheet, bounds, spec);
                case ExcelRangeMutationKind.Filter:
                    string filter;
                    return _excelRangeFilters.TryGetValue(key, out filter) &&
                        string.Equals(
                            filter, FilterState(spec), StringComparison.Ordinal);
                case ExcelRangeMutationKind.Format:
                    return FakeFormatSatisfied(key, spec);
                default:
                    return false;
            }
        }

        private static JArray FakeCellState(FakeSheet sheet, FakeRange range)
        {
            var rows = new JArray();
            for (var row = range.Start.Row; row <= range.End.Row; row++)
            {
                var line = new JArray();
                for (var column = range.Start.Column;
                    column <= range.End.Column; column++)
                {
                    object value;
                    var key = CellKey(row, column);
                    var cell = new JObject
                    {
                        ["value"] = sheet.Cells.TryGetValue(key, out value)
                            ? JToken.FromObject(value ?? string.Empty)
                            : JValue.CreateNull(),
                        ["formula"] = sheet.FormulaCells.Contains(key)
                    };
                    line.Add(cell);
                }
                rows.Add(line);
            }
            return rows;
        }

        private static bool FakeRangeIsEmpty(FakeSheet sheet, FakeRange range)
        {
            for (var row = range.Start.Row; row <= range.End.Row; row++)
                for (var column = range.Start.Column;
                    column <= range.End.Column; column++)
                {
                    object value;
                    var key = CellKey(row, column);
                    if (sheet.FormulaCells.Contains(key) ||
                        sheet.Cells.TryGetValue(key, out value) &&
                        !string.IsNullOrEmpty(Convert.ToString(
                            value, CultureInfo.InvariantCulture))) return false;
                }
            return true;
        }

        private static bool FakeRangeIsSorted(
            FakeSheet sheet,
            FakeRange range,
            ExcelRangeMutationSpec spec)
        {
            var keyColumn = range.Start.Column + spec.KeyColumn - 1;
            if (keyColumn > range.End.Column) return false;
            var startRow = range.Start.Row + (spec.HasHeaders ? 1 : 0);
            object previous = null;
            var hasPrevious = false;
            for (var row = startRow; row <= range.End.Row; row++)
            {
                object current;
                sheet.Cells.TryGetValue(CellKey(row, keyColumn), out current);
                if (hasPrevious && CompareFakeValues(
                    previous, current, spec.Descending) > 0) return false;
                previous = current;
                hasPrevious = true;
            }
            return true;
        }

        private static int CompareFakeValues(
            object left, object right, bool descending)
        {
            var leftBlank = left == null || string.IsNullOrEmpty(Convert.ToString(left));
            var rightBlank = right == null || string.IsNullOrEmpty(Convert.ToString(right));
            if (leftBlank && rightBlank) return 0;
            if (leftBlank) return 1;
            if (rightBlank) return -1;
            decimal leftNumber;
            decimal rightNumber;
            int comparison;
            if (decimal.TryParse(Convert.ToString(left), out leftNumber) &&
                decimal.TryParse(Convert.ToString(right), out rightNumber))
                comparison = leftNumber.CompareTo(rightNumber);
            else comparison = string.Compare(
                Convert.ToString(left), Convert.ToString(right),
                StringComparison.OrdinalIgnoreCase);
            return descending ? -comparison : comparison;
        }

        private void ApplyFakeClear(
            FakeSheet sheet,
            FakeRange bounds,
            string address,
            ExcelRangeMutationSpec spec)
        {
            if (spec.ClearWhat == "values" || spec.ClearWhat == "all")
            {
                for (var row = bounds.Start.Row; row <= bounds.End.Row; row++)
                    for (var column = bounds.Start.Column;
                        column <= bounds.End.Column; column++)
                    {
                        var cell = CellKey(row, column);
                        sheet.Cells.Remove(cell);
                        sheet.FormulaCells.Remove(cell);
                    }
            }
            if (spec.ClearWhat == "formats" || spec.ClearWhat == "all")
                _excelRangeFormats.Remove(
                    RangeMutationKey(sheet.Name, address));
        }

        private static void ApplyFakeSort(
            FakeSheet sheet,
            FakeRange range,
            ExcelRangeMutationSpec spec)
        {
            var startRow = range.Start.Row + (spec.HasHeaders ? 1 : 0);
            var rows = new List<FakeSortableRow>();
            for (var row = startRow; row <= range.End.Row; row++)
            {
                var item = new FakeSortableRow();
                for (var column = range.Start.Column;
                    column <= range.End.Column; column++)
                {
                    object value;
                    var key = CellKey(row, column);
                    sheet.Cells.TryGetValue(key, out value);
                    item.Values.Add(value);
                    item.Formulas.Add(sheet.FormulaCells.Contains(key));
                }
                rows.Add(item);
            }
            var keyIndex = spec.KeyColumn - 1;
            rows.Sort((left, right) => CompareFakeValues(
                left.Values[keyIndex], right.Values[keyIndex], spec.Descending));
            for (var index = 0; index < rows.Count; index++)
            {
                var row = startRow + index;
                for (var columnIndex = 0;
                    columnIndex < rows[index].Values.Count; columnIndex++)
                {
                    var key = CellKey(row, range.Start.Column + columnIndex);
                    var value = rows[index].Values[columnIndex];
                    if (value == null) sheet.Cells.Remove(key);
                    else sheet.Cells[key] = value;
                    if (rows[index].Formulas[columnIndex])
                        sheet.FormulaCells.Add(key);
                    else sheet.FormulaCells.Remove(key);
                }
            }
        }

        private void ApplyFakeFormat(
            string sheetName,
            string address,
            ExcelRangeMutationSpec spec)
        {
            var key = RangeMutationKey(sheetName, address);
            string stored;
            var state = _excelRangeFormats.TryGetValue(key, out stored) &&
                !string.IsNullOrWhiteSpace(stored)
                ? JObject.Parse(stored) : new JObject();
            if (spec.HasNumberFormat &&
                !string.IsNullOrWhiteSpace(spec.NumberFormat))
                state["numberFormat"] = spec.NumberFormat;
            if (spec.HasBold) state["bold"] = spec.Bold;
            if (spec.HasItalic) state["italic"] = spec.Italic;
            if (spec.HasFillColor && !string.IsNullOrWhiteSpace(spec.FillColor))
                state["fillColor"] = spec.FillColor;
            if (spec.HasFontColor && !string.IsNullOrWhiteSpace(spec.FontColor))
                state["fontColor"] = spec.FontColor;
            if (spec.HasHorizontalAlignment &&
                !string.IsNullOrWhiteSpace(spec.HorizontalAlignment))
                state["horizontalAlignment"] =
                    spec.HorizontalAlignment.ToLowerInvariant();
            if (state.HasValues)
                _excelRangeFormats[key] = state.ToString(Formatting.None);
            if (!string.IsNullOrWhiteSpace(spec.AutoFit))
                _excelRangeAutoFits[key] = spec.AutoFit;
        }

        private bool FakeFormatSatisfied(
            string key, ExcelRangeMutationSpec spec)
        {
            string stored;
            var state = _excelRangeFormats.TryGetValue(key, out stored) &&
                !string.IsNullOrWhiteSpace(stored)
                ? JObject.Parse(stored) : new JObject();
            if (spec.HasNumberFormat &&
                !string.IsNullOrWhiteSpace(spec.NumberFormat) &&
                !string.Equals((string)state["numberFormat"] ?? "General",
                    spec.NumberFormat, StringComparison.Ordinal)) return false;
            if (spec.HasBold &&
                ((bool?)state["bold"] ?? false) != spec.Bold) return false;
            if (spec.HasItalic &&
                ((bool?)state["italic"] ?? false) != spec.Italic) return false;
            if (spec.HasFillColor &&
                !string.IsNullOrWhiteSpace(spec.FillColor) &&
                !string.Equals((string)state["fillColor"] ?? string.Empty,
                    spec.FillColor, StringComparison.OrdinalIgnoreCase)) return false;
            if (spec.HasFontColor &&
                !string.IsNullOrWhiteSpace(spec.FontColor) &&
                !string.Equals((string)state["fontColor"] ?? string.Empty,
                    spec.FontColor, StringComparison.OrdinalIgnoreCase)) return false;
            if (spec.HasHorizontalAlignment &&
                !string.IsNullOrWhiteSpace(spec.HorizontalAlignment) &&
                !string.Equals(
                    (string)state["horizontalAlignment"] ?? "general",
                    spec.HorizontalAlignment,
                    StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrWhiteSpace(spec.AutoFit))
            {
                string autoFit;
                if (!_excelRangeAutoFits.TryGetValue(key, out autoFit) ||
                    !string.Equals(autoFit, spec.AutoFit,
                        StringComparison.Ordinal)) return false;
            }
            return true;
        }

        private static string FilterState(ExcelRangeMutationSpec spec)
        {
            return spec.Field + "|" + (spec.Criteria ?? string.Empty);
        }

        private static ToolInvocation RangeMutationCommand(
            ExcelRangeMutationSpec spec)
        {
            var toolId = spec.Kind == ExcelRangeMutationKind.Clear
                ? "excel.clear_range"
                : spec.Kind == ExcelRangeMutationKind.Sort
                    ? "excel.sort_range"
                    : spec.Kind == ExcelRangeMutationKind.Filter
                        ? "excel.filter_range"
                        : "excel.format_range";
            return new ToolInvocation
            {
                ToolId = toolId,
                Arguments = new Dictionary<string, object>
                {
                    { "sheet", spec.Sheet ?? string.Empty },
                    { "address", spec.Address ?? string.Empty }
                }
            };
        }

        private void ThrowQueuedExcelRangeMutationFailure()
        {
            if (_nextExcelRangeMutationApplyFailure == null) return;
            var failure = _nextExcelRangeMutationApplyFailure;
            _nextExcelRangeMutationApplyFailure = null;
            throw failure;
        }

        private static string RangeMutationKey(
            string sheetName, string address)
        {
            return (sheetName ?? string.Empty) + "!" + (address ?? string.Empty);
        }

        private static bool HasFakeEffectiveFormatting(
            ExcelRangeMutationSpec spec)
        {
            return spec.HasBold || spec.HasItalic ||
                spec.HasNumberFormat && !string.IsNullOrWhiteSpace(spec.NumberFormat) ||
                spec.HasFillColor && !string.IsNullOrWhiteSpace(spec.FillColor) ||
                spec.HasFontColor && !string.IsNullOrWhiteSpace(spec.FontColor) ||
                spec.HasHorizontalAlignment &&
                    !string.IsNullOrWhiteSpace(spec.HorizontalAlignment);
        }

        private static FakeCellAddress ParseCellKey(string key)
        {
            var parts = (key ?? string.Empty).Split(':');
            int row;
            int column;
            int.TryParse(parts.Length > 0 ? parts[0] : "1", out row);
            int.TryParse(parts.Length > 1 ? parts[1] : "1", out column);
            return new FakeCellAddress
            {
                Row = Math.Max(1, row),
                Column = Math.Max(1, column)
            };
        }

        private static string ColumnLetters(int column)
        {
            var builder = string.Empty;
            for (var value = Math.Max(1, column); value > 0; value = (value - 1) / 26)
                builder = (char)('A' + (value - 1) % 26) + builder;
            return builder;
        }

        private static ExcelRangeMutationBackendException RangeMutationFailure(
            string message, string code)
        {
            return new ExcelRangeMutationBackendException(
                message, code, false);
        }

        private sealed class FakeSortableRow
        {
            internal List<object> Values { get; private set; }
            internal List<bool> Formulas { get; private set; }

            internal FakeSortableRow()
            {
                Values = new List<object>();
                Formulas = new List<bool>();
            }
        }
    }
}
