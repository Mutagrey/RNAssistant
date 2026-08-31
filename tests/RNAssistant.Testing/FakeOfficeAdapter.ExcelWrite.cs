using System;
using System.Collections.Generic;
using RNAssistant.Office.Domains.Excel;

namespace RNAssistant.Harness
{
    internal sealed partial class FakeOfficeAdapter
    {
        internal void SetExcelCell(string sheetName, string address, object value)
        {
            FakeSheet sheet;
            if (!_sheets.TryGetValue(sheetName ?? string.Empty, out sheet))
                throw new InvalidOperationException("Worksheet not found: " + sheetName);
            var range = ParseRange(address);
            if (range.Start.Row != range.End.Row || range.Start.Column != range.End.Column)
                throw new InvalidOperationException("A single-cell address is required.");
            var key = CellKey(range.Start.Row, range.Start.Column);
            if (value == null) sheet.Cells.Remove(key);
            else sheet.Cells[key] = value;
            sheet.FormulaCells.Remove(key);
        }

        public ExcelWriteSnapshot Read(ExcelWriteReadRequest request)
        {
            BeginExcelBackendCall(ExcelWriteReadOperation);
            if (request == null) throw new ArgumentNullException(nameof(request));
            string kind;
            string sheetName;
            FakeSheet sheet;
            FakeRange range;
            ResolveExcelWriteTarget(
                request.Kind, request.Sheet, request.Address,
                request.Rows, request.Columns, request.MaxCells,
                out kind, out sheetName, out sheet, out range);
            var rows = range.End.Row - range.Start.Row + 1;
            var columns = range.End.Column - range.Start.Column + 1;
            var values = new List<List<object>>(rows);
            var formulas = new List<List<object>>(rows);
            var hasFormulas = new List<List<bool>>(rows);
            for (var row = range.Start.Row; row <= range.End.Row; row++)
            {
                var valueLine = new List<object>(columns);
                var formulaLine = new List<object>(columns);
                var formulaFlagLine = new List<bool>(columns);
                for (var column = range.Start.Column; column <= range.End.Column; column++)
                {
                    var key = CellKey(row, column);
                    object value;
                    if (!sheet.Cells.TryGetValue(key, out value)) value = null;
                    var hasFormula = sheet.FormulaCells.Contains(key);
                    valueLine.Add(value);
                    formulaLine.Add(value);
                    formulaFlagLine.Add(hasFormula);
                }
                values.Add(valueLine);
                formulas.Add(formulaLine);
                hasFormulas.Add(formulaFlagLine);
            }
            return new ExcelWriteSnapshot
            {
                Kind = kind,
                Sheet = sheetName,
                Address = FormatRange(range),
                Rows = rows,
                Columns = columns,
                CellCount = (long)rows * columns,
                Values = values,
                Formulas = formulas,
                HasFormulas = hasFormulas
            };
        }

        public void Apply(ExcelWriteApplyRequest request, Action markDispatchPossible)
        {
            BeginExcelBackendCall(ExcelWriteApplyOperation);
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (markDispatchPossible == null) throw new ArgumentNullException(nameof(markDispatchPossible));
            if (_nextExcelWriteApplyFailure != null)
            {
                var failure = _nextExcelWriteApplyFailure;
                _nextExcelWriteApplyFailure = null;
                throw failure;
            }

            string kind;
            string sheetName;
            FakeSheet sheet;
            FakeRange range;
            ResolveExcelWriteTarget(
                request.Kind, request.Sheet, request.Address,
                request.Rows, request.Columns, request.MaxCells,
                out kind, out sheetName, out sheet, out range);
            var rows = range.End.Row - range.Start.Row + 1;
            var columns = range.End.Column - range.Start.Column + 1;
            if (kind == "value" && request.Value == null)
            {
                // Null is an explicit scalar clear.
            }
            else if (kind == "formula" && string.IsNullOrWhiteSpace(request.Formula))
            {
                throw Failure("formula is required", "excel_write_formula_invalid");
            }
            else if (kind == "table" &&
                (request.Values == null || request.Values.Count != rows))
            {
                throw Failure("table payload mismatch", "excel_write_target_mismatch");
            }
            if (kind == "table")
            {
                for (var row = 0; row < rows; row++)
                {
                    if (request.Values[row] == null || request.Values[row].Count != columns)
                        throw Failure("table payload mismatch", "excel_write_target_mismatch");
                }
            }

            markDispatchPossible();
            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    var key = CellKey(range.Start.Row + row, range.Start.Column + column);
                    object value = kind == "table" ? request.Values[row][column] :
                        kind == "formula" ? (object)request.Formula : request.Value;
                    if (value == null) sheet.Cells.Remove(key);
                    else sheet.Cells[key] = value;
                    if (kind == "formula") sheet.FormulaCells.Add(key);
                    else sheet.FormulaCells.Remove(key);
                }
            }
            if (ExcelWriteThrowAfterMutation)
            {
                ExcelWriteThrowAfterMutation = false;
                throw new InvalidOperationException("scripted failure after Excel write mutation");
            }
        }

        private void ResolveExcelWriteTarget(
            string requestedKind,
            string requestedSheet,
            string requestedAddress,
            int expectedRows,
            int expectedColumns,
            int maxCells,
            out string kind,
            out string sheetName,
            out FakeSheet sheet,
            out FakeRange range)
        {
            kind = (requestedKind ?? string.Empty).Trim().ToLowerInvariant();
            sheetName = string.IsNullOrWhiteSpace(requestedSheet) ? "Sheet1" : requestedSheet;
            if (kind != "value" && kind != "formula" && kind != "table")
                throw Failure("invalid kind", "excel_write_kind_invalid");
            if (!_sheets.TryGetValue(sheetName, out sheet))
                throw Failure("worksheet not found", "excel_write_sheet_not_found");

            var requested = ParseRange(string.IsNullOrWhiteSpace(requestedAddress) ? "A1" : requestedAddress);
            var rows = kind == "table" ? expectedRows :
                requested.End.Row - requested.Start.Row + 1;
            var columns = kind == "table" ? expectedColumns :
                requested.End.Column - requested.Start.Column + 1;
            if (rows < 1 || columns < 1 || rows > ExcelWriteService.MaxWriteRows ||
                columns > ExcelWriteService.MaxWriteColumns || maxCells < 1 ||
                maxCells > ExcelWriteService.MaxWriteCells || (long)rows * columns > maxCells)
                throw Failure("write target too large", "excel_write_too_large");
            range = new FakeRange
            {
                Start = requested.Start,
                End = kind == "table"
                    ? new FakeCellAddress
                    {
                        Row = requested.Start.Row + rows - 1,
                        Column = requested.Start.Column + columns - 1
                    }
                    : requested.End
            };
            if (kind != "table" && (expectedRows > 0 || expectedColumns > 0) &&
                (rows != expectedRows || columns != expectedColumns))
                throw Failure("write target changed", "excel_write_target_mismatch");
        }

        private static ExcelWriteBackendException Failure(string message, string code)
        {
            return new ExcelWriteBackendException(message, code, false);
        }

        private static string FormatRange(FakeRange range)
        {
            var start = FormatAddress(range.Start);
            var end = FormatAddress(range.End);
            return string.Equals(start, end, StringComparison.Ordinal) ? start : start + ":" + end;
        }

        private static string FormatAddress(FakeCellAddress address)
        {
            var column = Math.Max(1, address == null ? 1 : address.Column);
            var letters = string.Empty;
            while (column > 0)
            {
                column--;
                letters = (char)('A' + column % 26) + letters;
                column /= 26;
            }
            return letters + Math.Max(1, address == null ? 1 : address.Row);
        }
    }
}
