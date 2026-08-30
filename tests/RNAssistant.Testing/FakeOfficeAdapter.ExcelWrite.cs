using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal sealed partial class FakeOfficeAdapter
    {
        public void SetExcelCell(string sheetName, string address, object value, bool formula = false)
        {
            var sheet = EnsureSheet(sheetName);
            var cell = ParseAddress(address);
            var key = CellKey(cell.Row, cell.Column);
            if (value == null) sheet.Cells.Remove(key);
            else sheet.Cells[key] = value;
            if (formula) sheet.FormulaCells.Add(key);
            else sheet.FormulaCells.Remove(key);
        }

        private ToolResult ExecuteExcelWriteReadBackend(ToolCommand command)
        {
            string kind;
            string sheetName;
            FakeSheet sheet;
            FakeRange range;
            ToolResult error;
            if (!TryResolveExcelWriteTarget(command, out kind, out sheetName, out sheet, out range, out error))
                return error;
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
            return ToolResult.Ok("fake Excel write state", JsonConvert.SerializeObject(new ExcelWriteSnapshot
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
            }));
        }

        private ToolResult ExecuteExcelWriteApplyBackend(ToolCommand command)
        {
            string kind;
            string sheetName;
            FakeSheet sheet;
            FakeRange range;
            ToolResult error;
            if (!TryResolveExcelWriteTarget(command, out kind, out sheetName, out sheet, out range, out error))
                return error;

            var rows = range.End.Row - range.Start.Row + 1;
            var columns = range.End.Column - range.Start.Column + 1;
            object scalar = null;
            JArray table = null;
            if (kind == "value")
            {
                if (!command.Arguments.TryGetValue("value", out scalar))
                    return ToolResult.Fail("value is required", null, "excel_write_value_invalid", false);
            }
            else if (kind == "formula")
            {
                scalar = Argument(command, "formula", string.Empty);
                if (string.IsNullOrWhiteSpace(Convert.ToString(scalar)))
                    return ToolResult.Fail("formula is required", null, "excel_write_formula_invalid", false);
            }
            else
            {
                var raw = command.Arguments.ContainsKey("values") ? command.Arguments["values"] : null;
                table = raw as JArray;
                if (table == null)
                {
                    try { table = raw == null ? null : JArray.FromObject(raw); }
                    catch (JsonException) { table = null; }
                }
                if (table == null || table.Count != rows ||
                    table.Any(item => !(item is JArray) || ((JArray)item).Count != columns))
                    return ToolResult.Fail("table payload mismatch", null, "excel_write_target_mismatch", false);
            }

            object rawBoundary;
            var boundary = command.Arguments.TryGetValue("dispatchBoundary", out rawBoundary)
                ? rawBoundary as IExcelWriteDispatchBoundary : null;
            if (boundary == null)
                return ToolResult.Fail("dispatch boundary missing", null,
                    "excel_write_dispatch_boundary_missing", false);

            boundary.Mark();
            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    var key = CellKey(range.Start.Row + row, range.Start.Column + column);
                    object value = kind == "table" ? CellTokenValue(((JArray)table[row])[column]) : scalar;
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
            return ToolResult.Ok("fake Excel write applied");
        }

        private bool TryResolveExcelWriteTarget(ToolCommand command, out string kind, out string sheetName,
            out FakeSheet sheet, out FakeRange range, out ToolResult error)
        {
            kind = Argument(command, "kind", string.Empty).Trim().ToLowerInvariant();
            sheetName = Argument(command, "sheet", "Sheet1");
            range = null;
            error = null;
            if (kind != "value" && kind != "formula" && kind != "table")
            {
                sheet = null;
                error = ToolResult.Fail("invalid kind", null, "excel_write_kind_invalid", false);
                return false;
            }
            if (!_sheets.TryGetValue(sheetName, out sheet))
            {
                error = ToolResult.Fail("worksheet not found", null, "excel_write_sheet_not_found", false);
                return false;
            }
            var requested = ParseRange(Argument(command, "address", "A1"));
            var rows = kind == "table" ? ArgumentInt(command, "rows", 0)
                : requested.End.Row - requested.Start.Row + 1;
            var columns = kind == "table" ? ArgumentInt(command, "columns", 0)
                : requested.End.Column - requested.Start.Column + 1;
            var maxCells = ArgumentInt(command, "maxCells", ExcelWriteService.MaxWriteCells);
            if (rows < 1 || columns < 1 || rows > ExcelWriteService.MaxWriteRows ||
                columns > ExcelWriteService.MaxWriteColumns || (long)rows * columns > maxCells ||
                maxCells < 1 || maxCells > ExcelWriteService.MaxWriteCells)
            {
                error = ToolResult.Fail("write target too large", null, "excel_write_too_large", false);
                return false;
            }
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
            var expectedRows = ArgumentInt(command, "rows", 0);
            var expectedColumns = ArgumentInt(command, "columns", 0);
            if (kind != "table" && (expectedRows > 0 || expectedColumns > 0) &&
                (rows != expectedRows || columns != expectedColumns))
            {
                error = ToolResult.Fail("write target changed", null, "excel_write_target_mismatch", false);
                return false;
            }
            return true;
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

        private static object CellTokenValue(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined) return null;
            var value = token as JValue;
            return value == null ? token.ToString(Formatting.None) : value.Value;
        }
    }
}
