using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.OfficeHosts.Identity;
using Excel = Microsoft.Office.Interop.Excel;

namespace RNAssistant.OfficeHosts
{
    internal sealed class ExcelTableInteropBackend : IExcelTableBackend
    {
        private readonly ExcelDocumentSession _session;
        private readonly Excel.Workbook _workbook;

        internal ExcelTableInteropBackend(ExcelDocumentSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _workbook = session.BoundDocumentObject as Excel.Workbook;
            if (_workbook == null)
                throw new InvalidOperationException(
                    "The bound Excel workbook is unavailable.");
        }

        public ExcelTableCollectionSnapshot Read(ExcelTableReadRequest request)
        {
            try
            {
                if (request == null) throw new ArgumentNullException(nameof(request));
                Excel.Worksheet sheet;
                var range = ResolveRange(
                    request.Sheet, request.SourceRange,
                    request.ExpectedRows, request.ExpectedColumns,
                    request.MaxCells, out sheet);
                var tables = ReadTables(request.MaxTables);
                var rows = Convert.ToInt32(range.Rows.Count);
                var columns = Convert.ToInt32(range.Columns.Count);
                var observation = new JObject
                {
                    ["sheet"] = sheet.Name,
                    ["range"] = range.Address[false, false],
                    ["rows"] = rows,
                    ["columns"] = columns,
                    ["values"] = MatrixToken(range.Value2),
                    ["formulas"] = MatrixToken(range.Formula),
                    ["tables"] = JArray.FromObject(tables)
                };
                return new ExcelTableCollectionSnapshot
                {
                    Sheet = sheet.Name,
                    SourceRange = range.Address[false, false],
                    Rows = rows,
                    Columns = columns,
                    CellCount = (long)rows * columns,
                    StateToken = Hash(observation.ToString(Formatting.None)),
                    Tables = tables
                };
            }
            catch (ExcelTableBackendException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw Failure(ex.Message, "office_tool_error", true);
            }
        }

        public void Add(
            ExcelTableApplyRequest request,
            Action markDispatchPossible)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (markDispatchPossible == null)
                throw new ArgumentNullException(nameof(markDispatchPossible));
            Excel.ListObject created = null;
            try
            {
                Excel.Worksheet sheet;
                var range = ResolveRange(
                    request.Sheet, request.SourceRange,
                    request.Rows, request.Columns,
                    request.MaxCells, out sheet);
                var current = Read(new ExcelTableReadRequest
                {
                    Sheet = sheet.Name,
                    SourceRange = range.Address[false, false],
                    ExpectedRows = request.Rows,
                    ExpectedColumns = request.Columns,
                    MaxCells = request.MaxCells,
                    MaxTables = request.MaxTables
                });
                if (!string.Equals(
                    current.StateToken, request.ExpectedStateToken,
                    StringComparison.Ordinal))
                    throw Failure(
                        "Excel table source or collection changed before dispatch.",
                        "excel_table_target_changed", false);
                if (current.Tables.Count >= request.MaxTables)
                    throw Failure(
                        "Excel workbook table limit for this operation was reached.",
                        "excel_table_limit_reached", false);
                if (!string.IsNullOrWhiteSpace(request.Name))
                    foreach (var table in current.Tables)
                        if (string.Equals(table.Name, request.Name,
                                StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(table.DisplayName, request.Name,
                                StringComparison.OrdinalIgnoreCase))
                            throw Failure(
                                "Excel table already exists: " + request.Name,
                                "excel_table_already_exists", false);

                markDispatchPossible();
                created = sheet.ListObjects.Add(
                    Excel.XlListObjectSourceType.xlSrcRange,
                    range,
                    Type.Missing,
                    request.HasHeaders
                        ? Excel.XlYesNoGuess.xlYes
                        : Excel.XlYesNoGuess.xlNo,
                    Type.Missing);
                if (created == null || !BelongsToSession(
                    created.Range == null
                        ? null : created.Range.Worksheet as Excel.Worksheet))
                    throw Failure(
                        "Excel created a table outside the bound workbook.",
                        "excel_table_target_invalid", false);
                if (!string.IsNullOrWhiteSpace(request.Name))
                    created.Name = request.Name;
                if (!string.IsNullOrWhiteSpace(request.Style))
                    created.TableStyle = request.Style;
            }
            catch (ExcelTableBackendException)
            {
                TryRollback(created);
                throw;
            }
            catch (Exception ex)
            {
                TryRollback(created);
                throw Failure(ex.Message, "office_tool_error", true);
            }
        }

        private Excel.Range ResolveRange(
            string sheetName,
            string sourceRange,
            int expectedRows,
            int expectedColumns,
            int maxCells,
            out Excel.Worksheet sheet)
        {
            var workbook = RequireWorkbook();
            if (maxCells < 1 || maxCells > ExcelTableService.MaxTableCells)
                throw Failure(
                    "Excel table cell ceiling is invalid.",
                    "excel_table_bound_invalid", false);
            sheet = ResolveSheet(workbook, sheetName);
            var range = sheet.Range[string.IsNullOrWhiteSpace(sourceRange)
                ? "A1:B2" : sourceRange];
            if (range == null || range.Areas.Count != 1)
                throw Failure(
                    "Excel table source must be one contiguous range.",
                    "excel_table_target_invalid", false);
            var rangeSheet = range.Worksheet as Excel.Worksheet;
            if (!BelongsToSession(rangeSheet))
                throw Failure(
                    "Excel table source resolved outside the bound workbook.",
                    "excel_table_target_invalid", false);
            sheet = rangeSheet;
            var rows = Convert.ToInt32(range.Rows.Count);
            var columns = Convert.ToInt32(range.Columns.Count);
            var cells = (long)rows * columns;
            if (rows < 1 || columns < 1 || cells > maxCells)
                throw Failure(
                    "Excel table source is too large: " + cells +
                    " cells. Limit is " + maxCells + ".",
                    "excel_table_too_large", false);
            if ((expectedRows > 0 || expectedColumns > 0) &&
                (rows != expectedRows || columns != expectedColumns))
                throw Failure(
                    "Excel table source dimensions changed before dispatch.",
                    "excel_table_target_changed", false);
            return range;
        }

        private List<ExcelTableState> ReadTables(int maxTables)
        {
            if (maxTables < 1 || maxTables > ExcelTableService.MaxWorkbookTables)
                throw Failure(
                    "Excel table collection ceiling is invalid.",
                    "excel_table_bound_invalid", false);
            var result = new List<ExcelTableState>();
            foreach (Excel.Worksheet sheet in _workbook.Worksheets)
            {
                if (!BelongsToSession(sheet))
                    throw Failure(
                        "Excel worksheet resolved outside the bound workbook.",
                        "excel_table_target_invalid", false);
                foreach (Excel.ListObject table in sheet.ListObjects)
                {
                    if (result.Count >= maxTables)
                        throw Failure(
                            "Excel workbook has too many tables for exact verification.",
                            "excel_table_collection_too_large", false);
                    var range = table.Range;
                    var tableSheet = range == null
                        ? null : range.Worksheet as Excel.Worksheet;
                    if (!BelongsToSession(tableSheet))
                        throw Failure(
                            "Excel table resolved outside the bound workbook.",
                            "excel_table_target_invalid", false);
                    result.Add(new ExcelTableState
                    {
                        Sheet = tableSheet.Name,
                        Name = table.Name,
                        DisplayName = table.DisplayName,
                        Range = range.Address[false, false],
                        Rows = Convert.ToInt32(range.Rows.Count),
                        Columns = Convert.ToInt32(range.Columns.Count),
                        HasHeaders = table.ShowHeaders,
                        Style = Convert.ToString(
                            table.TableStyle, CultureInfo.InvariantCulture) ??
                            string.Empty
                    });
                }
            }
            return result;
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

        private static JArray MatrixToken(object value)
        {
            var result = new JArray();
            var array = value as object[,];
            if (array == null)
            {
                result.Add(new JArray(Canonical(value)));
                return result;
            }
            for (var row = array.GetLowerBound(0);
                row <= array.GetUpperBound(0); row++)
            {
                var line = new JArray();
                for (var column = array.GetLowerBound(1);
                    column <= array.GetUpperBound(1); column++)
                    line.Add(Canonical(array[row, column]));
                result.Add(line);
            }
            return result;
        }

        private static JToken Canonical(object value)
        {
            if (value == null || value == DBNull.Value) return JValue.CreateNull();
            if (value is DateTime)
                return ((DateTime)value).ToString("O", CultureInfo.InvariantCulture);
            if (value is string || value is bool || value is byte ||
                value is sbyte || value is short || value is ushort ||
                value is int || value is uint || value is long ||
                value is ulong || value is float || value is double ||
                value is decimal)
                return JToken.FromObject(value);
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(
                    sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)));
        }

        private static void TryRollback(Excel.ListObject table)
        {
            if (table == null) return;
            try { table.Unlist(); }
            catch
            {
            }
        }

        private static ExcelTableBackendException Failure(
            string message, string code, bool retryable)
        {
            return new ExcelTableBackendException(message, code, retryable);
        }
    }
}
