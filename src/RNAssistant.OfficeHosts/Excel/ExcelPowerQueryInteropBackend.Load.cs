using System;
using System.Linq;
using System.Text.RegularExpressions;
using RNAssistant.Office.Domains.Excel;
using Excel = Microsoft.Office.Interop.Excel;

namespace RNAssistant.OfficeHosts
{
    internal sealed partial class ExcelPowerQueryInteropBackend
    {
        public ExcelPowerQueryLoadResult CreateWorksheetLoad(
            ExcelPowerQueryLoadRequest request, Action markDispatchPossible)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (markDispatchPossible == null)
                throw new ArgumentNullException(nameof(markDispatchPossible));
            RequireWritableWorkbook();
            EnsureNoNativeRefresh();
            RequireSourceVersion(request.QueryName, request.ExpectedQueryIdentity,
                request.ExpectedSourceVersion);
            var bindings = ReadBindings(request.QueryName);
            if (!bindings.IsComplete || bindings.IsAmbiguous ||
                !string.Equals(bindings.BindingVersion, request.ExpectedBindingVersion,
                    StringComparison.Ordinal))
                throw Failure("Power Query bindings changed or cannot be fully inspected.",
                    "excel_query_binding_changed");
            ValidateDestinationNames(request);
            Excel.Worksheet sheet = null;
            foreach (Excel.Worksheet existing in _workbook.Worksheets)
                if (string.Equals(existing.Name, request.Sheet, StringComparison.OrdinalIgnoreCase))
                    sheet = existing;
            if (request.CreateSheet)
            {
                if (sheet != null)
                    throw Failure("The requested new worksheet already exists.",
                        "excel_query_sheet_already_exists");
                if (_workbook.ProtectStructure)
                    throw Failure("Workbook structure protection prevents creating a worksheet.",
                        "excel_query_sheet_protected");
            }
            else
            {
                if (sheet == null)
                    throw Failure("The requested worksheet was not found.",
                        "excel_query_sheet_not_found");
                RequireEmptyLoadSheet(sheet);
            }
            foreach (Excel.Worksheet existing in _workbook.Worksheets)
                foreach (Excel.ListObject table in existing.ListObjects)
                    if (string.Equals(table.Name, request.Table, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(table.DisplayName, request.Table, StringComparison.OrdinalIgnoreCase))
                        throw Failure("The requested table name is already in use.",
                            "excel_query_table_already_exists");
            RequireSourceVersion(request.QueryName, request.ExpectedQueryIdentity,
                request.ExpectedSourceVersion);

            var result = new ExcelPowerQueryLoadResult
            {
                Sheet = request.Sheet,
                Table = request.Table
            };
            Excel.ListObject created = null;
            try
            {
                markDispatchPossible();
                if (request.CreateSheet)
                {
                    // Add belongs to the bound workbook collection, never ActiveWorkbook.
                    sheet = (Excel.Worksheet)_workbook.Worksheets.Add(
                        Type.Missing, _workbook.Worksheets[_workbook.Worksheets.Count],
                        1, Excel.XlSheetType.xlWorksheet);
                    result.SheetCreated = true;
                    result.Sheet = sheet.Name;
                    if (!BelongsToSession(sheet))
                        throw Failure("Excel created a worksheet outside the bound session.",
                            "excel_query_binding_changed");
                    sheet.Name = request.Sheet;
                    result.Sheet = sheet.Name;
                }
                RequireEmptyLoadSheet(sheet);
                var destination = sheet.Range[request.Address];
                if (destination.Areas.Count != 1 || Convert.ToDouble(destination.Cells.CountLarge) != 1)
                    throw Failure("Power Query load destination must be a single cell.",
                        "excel_query_load_destination_invalid");
                created = sheet.ListObjects.Add(
                    Excel.XlListObjectSourceType.xlSrcExternal,
                    ExcelPowerQueryConnection.BuildMashupConnection(request.QueryName),
                    Type.Missing,
                    Excel.XlYesNoGuess.xlYes,
                    destination);
                if (created == null)
                    throw Failure("Excel did not return a new worksheet load object.",
                        "excel_query_load_create_unresolved");
                result.TableCreated = true;
                result.Table = created.Name;
                created.Name = request.Table;
                result.Table = created.Name;
                var queryTable = created.QueryTable;
                if (queryTable == null)
                    throw Failure("The created table has no query-table binding.",
                        "excel_query_load_create_unresolved");
                queryTable.CommandType = Excel.XlCmdType.xlCmdSql;
                queryTable.CommandText = ExcelPowerQueryConnection.BuildSelectCommand(request.QueryName);
                // The destination is empty, so expansion cannot replace adjacent data.
                queryTable.RefreshStyle = Excel.XlCellInsertionMode.xlOverwriteCells;
                queryTable.RefreshOnFileOpen = false;
                queryTable.BackgroundQuery = false;
                var afterBindings = ReadBindings(request.QueryName);
                var binding = RequireBinding(afterBindings, sheet.Name, created.Name);
                if (afterBindings.Connections.Any(connection => connection.ConnectionIdentity == binding.ConnectionIdentity && connection.InDataModel))
                    throw Failure("The created worksheet load uses a connection shared with the Data Model, outside this load scope.",
                        "excel_query_model_refresh_unsupported");
                Excel.Worksheet observedSheet;
                Excel.QueryTable observedQueryTable;
                var observedTable = ResolveLoad(binding, out observedSheet, out observedQueryTable);
                if (!string.Equals(Identity(observedTable), Identity(created), StringComparison.Ordinal))
                    throw Failure("The new load no longer refers to the created table.",
                        "excel_query_binding_changed");
                result.Binding = binding;
                result.BindingVerified = true;
                RequireSourceVersion(request.QueryName, request.ExpectedQueryIdentity,
                    request.ExpectedSourceVersion);
                result.Refresh = RefreshExactLoad(new ExcelPowerQueryRefreshRequest
                {
                    QueryName = request.QueryName,
                    ExpectedQueryIdentity = request.ExpectedQueryIdentity,
                    ExpectedSourceVersion = request.ExpectedSourceVersion,
                    ExpectedBindingVersion = afterBindings.BindingVersion,
                    Sheet = binding.Sheet,
                    Table = binding.Table
                }, binding, observedSheet, observedTable, observedQueryTable);
                result.Binding = result.Refresh.Binding;
                result.NativeRunning = result.Refresh.NativeRunning;
                result.BindingVerified = result.Refresh.BindingStillCurrent;
                result.ErrorCode = result.Refresh.ErrorCode;
                result.Message = result.Refresh.Message;
                return result;
            }
            catch (Exception ex)
            {
                // Keep partial objects visible for inspection. Never delete a sheet,
                // connection or same-named table as an automatic rollback/fallback.
                var known = ex as ExcelPowerQueryBackendException;
                result.ErrorCode = known == null ? "excel_query_load_partial" : known.ErrorCode;
                var detail = ex.Message ?? string.Empty;
                if (detail.Length > 1000) detail = detail.Substring(0, 1000) + "…";
                result.Message = "Worksheet load creation did not complete. Reported created objects were retained for inspection. " + detail;
                try
                {
                    if (sheet != null) result.Sheet = sheet.Name;
                    if (created != null)
                    {
                        result.Table = created.Name;
                        var queryTable = created.QueryTable;
                        if (queryTable != null)
                        {
                            result.NativeRunning = TryReadRefreshing(queryTable);
                            result.Binding = ReadLoad(sheet, created, queryTable,
                                queryTable.WorkbookConnection);
                        }
                    }
                    // A partial definition/load can be an observable mutation even
                    // though no refresh was started. Establish stopped state from
                    // Excel before releasing the separate native reservation.
                    EnsureNoNativeRefresh();
                    if (result.NativeRunning != true) result.NativeRunning = false;
                }
                catch (Exception) { }
                return result;
            }
        }

        private void RequireEmptyLoadSheet(Excel.Worksheet sheet)
        {
            if (sheet == null || !BelongsToSession(sheet))
                throw Failure("The load worksheet is outside the bound session.",
                    "excel_query_binding_changed");
            if (sheet.ProtectContents)
                throw Failure("The load worksheet is protected.", "excel_query_sheet_protected");
            if (sheet.ListObjects.Count != 0 || sheet.QueryTables.Count != 0)
                throw Failure("A new load requires a worksheet without existing tables or query tables.",
                    "excel_query_load_destination_occupied");
            var used = sheet.UsedRange;
            var merges = used.MergeCells;
            if (!(merges is bool) || (bool)merges)
                throw Failure("A new load requires a worksheet without merged cells.",
                    "excel_query_load_destination_occupied");
            // Find sees constants and formulas without reading a whole-sheet Value2 array.
            // Unknown result size means checking only the anchor would be unsafe.
            var occupied = sheet.Cells.Find("*", Type.Missing,
                Excel.XlFindLookIn.xlFormulas, Excel.XlLookAt.xlPart,
                Excel.XlSearchOrder.xlByRows, Excel.XlSearchDirection.xlNext,
                false, false, false);
            if (occupied != null)
                throw Failure("A new Power Query load requires an entirely empty worksheet.",
                    "excel_query_load_destination_occupied");
        }

        private static void ValidateDestinationNames(ExcelPowerQueryLoadRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Sheet) || request.Sheet.Length > 31 ||
                request.Sheet.IndexOfAny(new[] { ':', '\\', '/', '?', '*', '[', ']' }) >= 0 ||
                request.Sheet.StartsWith("'", StringComparison.Ordinal) ||
                request.Sheet.EndsWith("'", StringComparison.Ordinal))
                throw Failure("An explicit valid worksheet name is required.",
                    "excel_query_sheet_name_invalid");
            var address = request.Address ?? string.Empty;
            if (!Regex.IsMatch(address, @"^\$?[A-Za-z]{1,3}\$?[1-9][0-9]{0,6}$"))
                throw Failure("An explicit single-cell A1 address is required.",
                    "excel_query_load_destination_invalid");
            var bareAddress = address.Replace("$", string.Empty).ToUpperInvariant();
            var column = 0;
            var digit = 0;
            while (digit < bareAddress.Length && char.IsLetter(bareAddress[digit]))
                column = column * 26 + bareAddress[digit++] - 'A' + 1;
            int row;
            if (column > 16384 || !int.TryParse(bareAddress.Substring(digit), out row) ||
                row > 1048576)
                throw Failure("The destination lies outside Excel worksheet bounds.",
                    "excel_query_load_destination_invalid");
            var name = request.Table ?? string.Empty;
            if (name.Length < 1 || name.Length > 255 ||
                !(char.IsLetter(name[0]) || name[0] == '_') ||
                name.IndexOfAny(new[] { ' ', '\t', '\r', '\n', '[', ']', '!', ':', '$' }) >= 0 ||
                string.Equals(name, "R", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "C", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(name, @"^[A-Za-z]{1,3}[1-9][0-9]{0,6}$") ||
                Regex.IsMatch(name, @"^[Rr][0-9]+[Cc][0-9]+$"))
                throw Failure("An explicit valid non-cell-reference table name is required.",
                    "excel_query_table_name_invalid");
            foreach (var ch in name)
                if (!(char.IsLetterOrDigit(ch) || ch == '_' || ch == '.'))
                    throw Failure("The table name contains an unsupported character.",
                        "excel_query_table_name_invalid");
        }
    }
}
