using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using RNAssistant.Office.Domains.Excel;
using Excel = Microsoft.Office.Interop.Excel;

namespace RNAssistant.OfficeHosts
{
    internal sealed partial class ExcelRangeMutationInteropBackend
    {
        public ExcelDeletionSnapshot Read(ExcelDeletionReadRequest request)
        {
            ExcelDeleteRowsColumnsService.ValidateRequest(request?.Selection);
            var selection = request.Selection;
            var sheet = ResolveSheet(RequireWorkbook(), selection.Sheet);
            var snapshot = new ExcelDeletionSnapshot { Sheet = sheet.Name, Table = selection.Table };
            if (selection.Scope == ExcelDeletionScope.Table)
            {
                var table = ResolveDeletionTable(sheet, selection.Table);
                snapshot.Table = table.Name;
                snapshot.HeaderRows = table.ShowHeaders ? 1 : 0;
                snapshot.TotalRows = table.ShowTotals ? 1 : 0;
                snapshot.DataRows = table.ListRows.Count;
                snapshot.Window = new ExcelDeletionWindow
                {
                    Row = table.Range.Row, Column = table.Range.Column,
                    Rows = snapshot.HeaderRows + snapshot.DataRows + snapshot.TotalRows,
                    Columns = table.ListColumns.Count
                };
                ExcelDeleteRowsColumnsService.ValidateWindow(snapshot.Window, true);
                snapshot.ColumnNames = new List<string>();
                foreach (Excel.ListColumn column in table.ListColumns) snapshot.ColumnNames.Add(column.Name);
                snapshot.Cells = new List<List<ExcelDeletionCell>>();
                if (snapshot.HeaderRows > 0) snapshot.Cells.AddRange(ReadDeletionCells(table.HeaderRowRange));
                if (snapshot.DataRows > 0) snapshot.Cells.AddRange(ReadDeletionCells(table.DataBodyRange));
                if (snapshot.TotalRows > 0) snapshot.Cells.AddRange(ReadDeletionCells(table.TotalsRowRange));
            }
            else
            {
                var used = sheet.UsedRange;
                snapshot.UsedWindow = new ExcelDeletionWindow
                {
                    Row = used.Row, Column = used.Column, Rows = used.Rows.Count, Columns = used.Columns.Count
                };
                snapshot.Window = request.Window ?? ExcelDeleteRowsColumnsService.WorksheetWindow(snapshot.UsedWindow, selection);
                ExcelDeleteRowsColumnsService.ValidateWindow(snapshot.Window, false);
                var window = snapshot.Window;
                var range = sheet.Range[sheet.Cells[window.Row, window.Column],
                    sheet.Cells[window.Row + window.Rows - 1, window.Column + window.Columns - 1]];
                snapshot.Cells = ReadDeletionCells(range);
            }
            return snapshot;
        }

        public void Delete(ExcelDeletionApplyRequest request, Action markDispatchPossible)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (markDispatchPossible == null) throw new ArgumentNullException(nameof(markDispatchPossible));
            var selection = request.Selection;
            var before = Read(new ExcelDeletionReadRequest { Selection = selection,
                Window = selection.Scope == ExcelDeletionScope.Worksheet ? request.Window : null });
            if (!string.Equals(ExcelDeleteRowsColumnsService.StateToken(before), request.ExpectedStateToken, StringComparison.Ordinal))
                throw Failure("The deletion target changed before dispatch.", "excel_delete_target_changed", false);
            ExcelDeleteRowsColumnsService.ValidateSelection(selection, before);
            var sheet = ResolveSheet(RequireWorkbook(), before.Sheet);
            if (selection.Scope == ExcelDeletionScope.Table)
            {
                var table = ResolveDeletionTable(sheet, before.Table);
                markDispatchPossible();
                for (var index = selection.Start + selection.Count - 1; index >= selection.Start; index--)
                {
                    if (selection.Axis == ExcelDeletionAxis.Rows) table.ListRows[index].Delete();
                    else table.ListColumns[index].Delete();
                }
            }
            else
            {
                var range = selection.Axis == ExcelDeletionAxis.Rows
                    ? ((Excel.Range)sheet.Rows[selection.Start]).Resize[selection.Count, Type.Missing]
                    : ((Excel.Range)sheet.Columns[selection.Start]).Resize[Type.Missing, selection.Count];
                markDispatchPossible();
                if (selection.Axis == ExcelDeletionAxis.Rows) range.EntireRow.Delete();
                else range.EntireColumn.Delete();
            }
        }

        private static Excel.ListObject ResolveDeletionTable(Excel.Worksheet sheet, string name)
        {
            foreach (Excel.ListObject table in sheet.ListObjects)
                if (string.Equals(table.Name, name, StringComparison.OrdinalIgnoreCase)) return table;
            throw Failure("Table not found on the selected sheet: " + name, "excel_delete_table_not_found", false);
        }

        private static List<List<ExcelDeletionCell>> ReadDeletionCells(Excel.Range range)
        {
            var values = Matrix(range.Value2);
            var formulas = Matrix(range.Formula);
            var result = new List<List<ExcelDeletionCell>>();
            for (var r = 0; r < values.Count; r++)
            {
                var row = new List<ExcelDeletionCell>();
                for (var c = 0; c < values[r].Count; c++)
                {
                    var formula = formulas[r][c] as string;
                    var hasFormula = IsFormula(formula) &&
                        Convert.ToBoolean(((Excel.Range)range.Cells[r + 1, c + 1]).HasFormula, CultureInfo.InvariantCulture);
                    row.Add(new ExcelDeletionCell
                    {
                        Formula = hasFormula ? formula : null,
                        ValueJson = hasFormula ? null : Canonical(values[r][c]).ToString(Formatting.None)
                    });
                }
                result.Add(row);
            }
            return result;
        }
    }
}
