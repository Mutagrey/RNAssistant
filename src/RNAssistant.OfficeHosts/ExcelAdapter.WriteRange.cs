using System;
using System.Collections.Generic;
using System.Linq;
using Excel = Microsoft.Office.Interop.Excel;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.Office.Tools;

namespace RNAssistant.OfficeHosts
{
    public sealed partial class ExcelAdapter
    {
        private ToolResult ReadWriteRangeBackend(ToolCommand command)
        {
            Excel.Worksheet sheet;
            var kind = WriteKind(command);
            var range = ResolveWriteRange(command, kind, out sheet);
            var rows = Convert.ToInt32(range.Rows.Count);
            var columns = Convert.ToInt32(range.Columns.Count);
            var snapshot = new ExcelWriteSnapshot
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
            return ToolResult.Ok("Excel write state collected.", JsonConvert.SerializeObject(snapshot));
        }

        private ToolResult ApplyWriteRangeBackend(ToolCommand command)
        {
            Excel.Worksheet sheet;
            var kind = WriteKind(command);
            var range = ResolveWriteRange(command, kind, out sheet);
            object payload;
            if (kind == "value")
            {
                if (!command.Arguments.TryGetValue("value", out payload))
                    throw new ExcelWriteHostException("value is required when kind is value.", "excel_write_value_invalid", false);
            }
            else if (kind == "formula")
            {
                payload = ToolArgumentReader.String(command.Arguments, "formula", null);
                if (string.IsNullOrWhiteSpace((string)payload))
                    throw new ExcelWriteHostException("formula is required when kind is formula.", "excel_write_formula_invalid", false);
            }
            else
            {
                var valuesJson = ToolArgumentReader.String(command.Arguments, "values", "[]");
                var values = JArray.Parse(valuesJson);
                var rows = Convert.ToInt32(range.Rows.Count);
                var columns = Convert.ToInt32(range.Columns.Count);
                if (values.Count != rows || values.Any(item => !(item is JArray) || ((JArray)item).Count != columns))
                    throw new ExcelWriteHostException("Table payload does not match the resolved target.", "excel_write_target_mismatch", false);
                var data = new object[rows, columns];
                for (var row = 0; row < rows; row++)
                {
                    var source = (JArray)values[row];
                    for (var column = 0; column < columns; column++)
                        data[row, column] = ToCellValue(source[column]);
                }
                payload = data;
            }

            object rawBoundary;
            var boundary = command.Arguments.TryGetValue("dispatchBoundary", out rawBoundary)
                ? rawBoundary as IExcelWriteDispatchBoundary : null;
            if (boundary == null)
                throw new ExcelWriteHostException("Excel write dispatch boundary is missing.",
                    "excel_write_dispatch_boundary_missing", false);

            boundary.Mark();
            if (kind == "formula") range.Formula = payload;
            else range.Value2 = payload;
            return ToolResult.Ok("Excel write dispatched to " + sheet.Name + "!" + range.Address[false, false] + ".");
        }

        private Excel.Range ResolveWriteRange(ToolCommand command, string kind, out Excel.Worksheet sheet)
        {
            var maxCells = ToolArgumentReader.Int32(command.Arguments, "maxCells", ExcelWriteService.MaxWriteCells);
            if (maxCells < 1 || maxCells > ExcelWriteService.MaxWriteCells)
                throw new ExcelWriteHostException("Excel write ceiling is invalid.", "excel_write_bound_invalid", false);
            sheet = ResolveSheet(ToolArgumentReader.String(command.Arguments, "sheet", null));
            var address = ToolArgumentReader.String(command.Arguments, "address", "A1");
            var requested = sheet.Range[address];
            if (requested == null || requested.Areas.Count != 1)
                throw new ExcelWriteHostException("Excel write target must be one contiguous range.",
                    "excel_write_target_invalid", false);

            Excel.Range range = requested;
            var expectedRows = ToolArgumentReader.Int32(command.Arguments, "rows", 0);
            var expectedColumns = ToolArgumentReader.Int32(command.Arguments, "columns", 0);
            if (kind == "table")
            {
                if (expectedRows < 1 || expectedRows > ExcelWriteService.MaxWriteRows ||
                    expectedColumns < 1 || expectedColumns > ExcelWriteService.MaxWriteColumns ||
                    (long)expectedRows * expectedColumns > maxCells)
                    throw new ExcelWriteHostException("Excel table dimensions exceed the write bound.",
                        "excel_write_too_large", false);
                var start = requested.Cells[1, 1] as Excel.Range;
                if (start == null || (long)start.Row + expectedRows - 1 > sheet.Rows.Count ||
                    (long)start.Column + expectedColumns - 1 > sheet.Columns.Count)
                    throw new ExcelWriteHostException("Excel table target exceeds worksheet bounds.",
                        "excel_write_target_invalid", false);
                range = start.Resize[expectedRows, expectedColumns];
            }

            var rows = Convert.ToInt32(range.Rows.Count);
            var columns = Convert.ToInt32(range.Columns.Count);
            var cellCount = (long)rows * columns;
            if (rows < 1 || columns < 1 || cellCount > maxCells)
                throw new ExcelWriteHostException("Excel write target is too large: " + cellCount +
                    " cells. Limit is " + maxCells + ".", "excel_write_too_large", false);
            if (kind != "table" && (expectedRows > 0 || expectedColumns > 0) &&
                (rows != expectedRows || columns != expectedColumns))
                throw new ExcelWriteHostException("Excel write target dimensions changed.",
                    "excel_write_target_mismatch", false);
            var rangeSheet = range.Worksheet as Excel.Worksheet;
            if (rangeSheet == null || !SameWorkbook(rangeSheet.Parent as Excel.Workbook, RequireWorkbook()))
                throw new ExcelWriteHostException("Excel write target resolved outside the bound workbook.",
                    "excel_write_target_invalid", false);
            sheet = rangeSheet;
            return range;
        }

        private static string WriteKind(ToolCommand command)
        {
            var kind = ToolArgumentReader.String(command.Arguments, "kind", string.Empty).Trim().ToLowerInvariant();
            if (kind != "value" && kind != "formula" && kind != "table")
                throw new ExcelWriteHostException("kind must be value, formula, or table.",
                    "excel_write_kind_invalid", false);
            return kind;
        }

        private static List<List<bool>> RangeFormulaFlags(Excel.Range range, int rows, int columns)
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

        private sealed class ExcelWriteHostException : InvalidOperationException
        {
            internal string ErrorCode { get; private set; }
            internal bool Retryable { get; private set; }

            internal ExcelWriteHostException(string message, string errorCode, bool retryable)
                : base(message)
            {
                ErrorCode = errorCode;
                Retryable = retryable;
            }
        }
    }
}
