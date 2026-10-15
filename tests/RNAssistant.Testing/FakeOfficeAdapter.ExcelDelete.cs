using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Office.Domains.Excel;

namespace RNAssistant.Harness
{
    internal sealed partial class FakeOfficeAdapter
    {
        internal const string ExcelDeletionReadOperation = "excel_delete_read";
        internal const string ExcelDeletionApplyOperation = "excel_delete_apply";
        internal bool ExcelDeletionThrowAfterMutation;
        internal bool ExcelDeletionSkipMutation;
        internal Func<ExcelDeletionSnapshot, ExcelDeletionSnapshot> ExcelDeletionReadTransform;

        public ExcelDeletionSnapshot Read(ExcelDeletionReadRequest request)
        {
            BeginExcelBackendCall(ExcelDeletionReadOperation);
            var snapshot = ReadFakeDeletion(request);
            return ExcelDeletionReadTransform == null ? snapshot : ExcelDeletionReadTransform(snapshot);
        }

        private ExcelDeletionSnapshot ReadFakeDeletion(ExcelDeletionReadRequest request)
        {
            ExcelDeleteRowsColumnsService.ValidateRequest(request?.Selection);
            var selection = request.Selection;
            var sheet = ResolveFakeRangeSheet(selection.Sheet);
            var result = new ExcelDeletionSnapshot { Sheet = sheet.Name, Table = selection.Table };
            if (selection.Scope == ExcelDeletionScope.Table)
            {
                var table = FakeDeletionTable(sheet, selection.Table);
                var range = ParseRange(table.Range);
                result.Table = table.Name;
                result.HeaderRows = table.HasHeaders ? 1 : 0;
                result.DataRows = table.Rows - result.HeaderRows;
                result.Window = new ExcelDeletionWindow { Row = range.Start.Row, Column = range.Start.Column,
                    Rows = table.Rows, Columns = table.Columns };
                result.ColumnNames = Enumerable.Range(0, table.Columns).Select(c => table.HasHeaders
                    ? ExcelCellText(sheet.Name, ColumnLetters(range.Start.Column + c) + range.Start.Row)
                    : "Column" + (c + 1)).ToList();
            }
            else
            {
                var cells = sheet.Cells.Keys.Select(ParseCellKey).ToList();
                var top = cells.Count == 0 ? 1 : cells.Min(cell => cell.Row);
                var left = cells.Count == 0 ? 1 : cells.Min(cell => cell.Column);
                var bottom = cells.Count == 0 ? 1 : cells.Max(cell => cell.Row);
                var right = cells.Count == 0 ? 1 : cells.Max(cell => cell.Column);
                result.UsedWindow = new ExcelDeletionWindow
                    { Row = top, Column = left, Rows = bottom - top + 1, Columns = right - left + 1 };
                result.Window = request.Window ?? ExcelDeleteRowsColumnsService.WorksheetWindow(result.UsedWindow, selection);
            }
            ExcelDeleteRowsColumnsService.ValidateWindow(result.Window, selection.Scope == ExcelDeletionScope.Table);
            result.Cells = new List<List<ExcelDeletionCell>>();
            for (var r = 0; r < result.Window.Rows; r++)
            {
                var row = new List<ExcelDeletionCell>();
                for (var c = 0; c < result.Window.Columns; c++)
                {
                    var key = CellKey(result.Window.Row + r, result.Window.Column + c);
                    object value;
                    sheet.Cells.TryGetValue(key, out value);
                    var formula = sheet.FormulaCells.Contains(key);
                    row.Add(new ExcelDeletionCell { Formula = formula ? Convert.ToString(value) : null,
                        ValueJson = formula ? null : JsonConvert.SerializeObject(value) });
                }
                result.Cells.Add(row);
            }
            return result;
        }

        public void Delete(ExcelDeletionApplyRequest request, Action markDispatchPossible)
        {
            BeginExcelBackendCall(ExcelDeletionApplyOperation);
            var selection = request.Selection;
            var before = ReadFakeDeletion(new ExcelDeletionReadRequest { Selection = selection,
                Window = selection.Scope == ExcelDeletionScope.Worksheet ? request.Window : null });
            if (ExcelDeleteRowsColumnsService.StateToken(before) != request.ExpectedStateToken)
                throw RangeMutationFailure("Deletion target changed.", "excel_delete_target_changed");
            ExcelDeleteRowsColumnsService.ValidateSelection(selection, before);
            markDispatchPossible();
            if (ExcelDeletionSkipMutation) return;
            var sheet = ResolveFakeRangeSheet(selection.Sheet);
            var rows = selection.Axis == ExcelDeletionAxis.Rows;
            var tableScope = selection.Scope == ExcelDeletionScope.Table;
            var start = selection.Start + (tableScope ? rows
                ? before.Window.Row + before.HeaderRows - 1 : before.Window.Column - 1 : 0);
            var oldCells = new Dictionary<string, object>(sheet.Cells, StringComparer.OrdinalIgnoreCase);
            var oldFormulas = new HashSet<string>(sheet.FormulaCells, StringComparer.OrdinalIgnoreCase);
            for (var r = before.Window.Row; r < before.Window.Row + before.Window.Rows; r++)
            for (var c = before.Window.Column; c < before.Window.Column + before.Window.Columns; c++)
            {
                var sourceRow = r + (rows && r >= start ? selection.Count : 0);
                var sourceColumn = c + (!rows && c >= start ? selection.Count : 0);
                var key = CellKey(r, c);
                var sourceKey = CellKey(sourceRow, sourceColumn);
                object value;
                sheet.Cells.Remove(key);
                sheet.FormulaCells.Remove(key);
                if (sourceRow >= before.Window.Row + before.Window.Rows ||
                    sourceColumn >= before.Window.Column + before.Window.Columns || !oldCells.TryGetValue(sourceKey, out value)) continue;
                sheet.Cells[key] = value;
                if (oldFormulas.Contains(sourceKey)) sheet.FormulaCells.Add(key);
            }
            if (tableScope)
            {
                var table = FakeDeletionTable(sheet, selection.Table);
                var range = ParseRange(table.Range);
                if (rows) { table.Rows -= selection.Count; range.End.Row -= selection.Count; }
                else { table.Columns -= selection.Count; range.End.Column -= selection.Count; }
                table.Range = FormatRange(range);
            }
            if (ExcelDeletionThrowAfterMutation)
            {
                ExcelDeletionThrowAfterMutation = false;
                throw new InvalidOperationException("scripted failure after deletion");
            }
        }

        private static FakeTable FakeDeletionTable(FakeSheet sheet, string name)
        {
            var table = sheet.Tables.SingleOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (table == null) throw RangeMutationFailure("Table not found.", "excel_delete_table_not_found");
            return table;
        }
    }
}
