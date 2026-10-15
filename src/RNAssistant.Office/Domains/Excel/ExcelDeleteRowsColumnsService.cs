using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Office.Domains.Excel
{
    public sealed class ExcelDeleteRowsColumnsService
    {
        public const int MaxCells = 100000;
        public const int WorksheetRows = 1048576;
        public const int WorksheetColumns = 16384;
        private readonly IExcelRangeMutationBackend _backend;

        public ExcelDeleteRowsColumnsService(IExcelRangeMutationBackend backend)
        { _backend = backend ?? throw new ArgumentNullException(nameof(backend)); }

        public ExcelDeletionPreparedState Prepare(ExcelDeleteRowsColumnsRequest request, CancellationToken cancellationToken)
        {
            ValidateRequest(request);
            cancellationToken.ThrowIfCancellationRequested();
            var before = _backend.Read(new ExcelDeletionReadRequest { Selection = request });
            ValidateSnapshot(before, request);
            ValidateSelection(request, before);
            return new ExcelDeletionPreparedState
            {
                Selection = new ExcelDeleteRowsColumnsRequest { Sheet = before.Sheet, Table = before.Table,
                    Scope = request.Scope, Axis = request.Axis, Start = request.Start, Count = request.Count },
                Window = before.Window, ExpectedStateToken = StateToken(before)
            };
        }

        public DomainOutcome Delete(ExcelDeleteRowsColumnsRequest request,
            Action markDispatchPossible, CancellationToken cancellationToken, ExcelDeletionPreparedState preparation)
        {
            var dispatched = false;
            try
            {
                ValidateRequest(request);
                if (preparation?.Selection == null || string.IsNullOrWhiteSpace(preparation.ExpectedStateToken) ||
                    preparation.Selection.Scope != request.Scope || preparation.Selection.Axis != request.Axis ||
                    preparation.Selection.Start != request.Start || preparation.Selection.Count != request.Count ||
                    !string.Equals(preparation.Selection.Sheet, request.Sheet, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(preparation.Selection.Table ?? string.Empty, request.Table ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                    return Failure("Deletion requires matching prepared target state.", "excel_delete_preparation_invalid");
                cancellationToken.ThrowIfCancellationRequested();
                var before = _backend.Read(new ExcelDeletionReadRequest { Selection = preparation.Selection,
                    Window = request.Scope == ExcelDeletionScope.Worksheet ? preparation.Window : null });
                ValidateSnapshot(before, request);
                if (StateToken(before) != preparation.ExpectedStateToken)
                    return Failure("The deletion target changed while awaiting confirmation. Inspect it and request a new deletion.", "excel_delete_target_changed");
                ValidateSelection(request, before);
                var selection = new ExcelDeleteRowsColumnsRequest
                {
                    Sheet = before.Sheet, Table = before.Table, Scope = request.Scope,
                    Axis = request.Axis, Start = request.Start, Count = request.Count
                };
                var beforeToken = StateToken(before);
                cancellationToken.ThrowIfCancellationRequested();
                _backend.Delete(new ExcelDeletionApplyRequest
                {
                    Selection = selection, Window = before.Window, ExpectedStateToken = beforeToken
                }, () =>
                {
                    if (dispatched) return;
                    dispatched = true;
                    markDispatchPossible?.Invoke();
                });
                if (!dispatched)
                {
                    dispatched = true;
                    markDispatchPossible?.Invoke();
                    return Unknown("Deletion backend returned without a dispatch boundary.");
                }
                cancellationToken.ThrowIfCancellationRequested();
                var after = _backend.Read(new ExcelDeletionReadRequest
                {
                    Selection = selection,
                    Window = selection.Scope == ExcelDeletionScope.Worksheet ? before.Window : null
                });
                ValidateSnapshot(after, selection);
                if (!MatchesDeletion(before, after, selection))
                    return Unknown("Deletion read-back did not match the selected rows or columns. Inspect the target before another mutation.");
                var changed = beforeToken != StateToken(after);
                var data = new JObject
                {
                    ["sheet"] = after.Sheet, ["scope"] = selection.Scope == ExcelDeletionScope.Table ? "table" : "worksheet",
                    ["table"] = after.Table, ["axis"] = selection.Axis == ExcelDeletionAxis.Rows ? "rows" : "columns",
                    ["start"] = selection.Start, ["count"] = selection.Count,
                    ["verification"] = changed ? "changed" : "no_change"
                };
                if (selection.Scope == ExcelDeletionScope.Table)
                {
                    data["dataRows"] = after.DataRows;
                    data["columns"] = new JArray(after.ColumnNames);
                }
                return DomainOutcome.Verified("Deleted " + selection.Count + " " + data["axis"] + " in " +
                    (selection.Scope == ExcelDeletionScope.Table ? "table '" + after.Table + "' on " : string.Empty) +
                    after.Sheet + ".", data.ToString(Formatting.None),
                    changed ? DomainEffect.VerifiedChange : DomainEffect.VerifiedNoChange);
            }
            catch (OperationCanceledException)
            {
                if (!dispatched) throw;
                return Unknown("Cancellation after deletion dispatch; inspect the target before another mutation.");
            }
            catch (ExcelRangeMutationBackendException ex)
            {
                return dispatched ? Unknown(ex.Message) : Failure(ex.Message, ex.ErrorCode);
            }
            catch (Exception ex)
            {
                return dispatched ? Unknown(ex.Message) : Failure(ex.Message, "excel_delete_failed");
            }
        }

        public static void ValidateRequest(ExcelDeleteRowsColumnsRequest request)
        {
            if (request == null || !Enum.IsDefined(typeof(ExcelDeletionScope), request.Scope) ||
                !Enum.IsDefined(typeof(ExcelDeletionAxis), request.Axis) || string.IsNullOrWhiteSpace(request.Sheet) ||
                request.Start < 1 || request.Count < 1 ||
                (long)request.Start + request.Count - 1 > (request.Axis == ExcelDeletionAxis.Rows ? WorksheetRows : WorksheetColumns) ||
                (request.Scope == ExcelDeletionScope.Table ? string.IsNullOrWhiteSpace(request.Table) : !string.IsNullOrEmpty(request.Table)))
                throw Error("Specify a sheet, scope, axis, positive start/count, and a table name only for table scope.", "invalid_arguments");
        }

        public static void ValidateSelection(ExcelDeleteRowsColumnsRequest request, ExcelDeletionSnapshot snapshot)
        {
            ValidateRequest(request);
            if (request.Scope != ExcelDeletionScope.Table) return;
            var available = request.Axis == ExcelDeletionAxis.Rows ? snapshot.DataRows : snapshot.Window.Columns;
            if ((long)request.Start + request.Count - 1 > available)
                throw Error("Deletion exceeds the table's " + request.Axis.ToString().ToLowerInvariant() +
                    ". Row indexes refer only to data rows, excluding headers and totals.", "excel_delete_out_of_range");
            if (request.Axis == ExcelDeletionAxis.Columns && request.Count == available)
                throw Error("A table must retain at least one column. Deleting the whole table is a separate operation.", "excel_delete_all_table_columns");
        }

        public static ExcelDeletionWindow WorksheetWindow(ExcelDeletionWindow used, ExcelDeleteRowsColumnsRequest request)
        {
            var row = used.Row;
            var column = used.Column;
            if (request.Axis == ExcelDeletionAxis.Rows && request.Start < row + used.Rows)
                row = Math.Min(row, Math.Max(request.Start, row - request.Count));
            if (request.Axis == ExcelDeletionAxis.Columns && request.Start < column + used.Columns)
                column = Math.Min(column, Math.Max(request.Start, column - request.Count));
            var window = new ExcelDeletionWindow
            {
                Row = row, Column = column, Rows = used.Row + used.Rows - row,
                Columns = used.Column + used.Columns - column
            };
            ValidateWindow(window, false);
            return window;
        }

        public static void ValidateWindow(ExcelDeletionWindow window, bool allowEmpty)
        {
            if (window == null || window.Row < 1 || window.Column < 1 || window.Columns < 1 ||
                window.Rows < (allowEmpty ? 0 : 1) || (long)window.Row + window.Rows - 1 > WorksheetRows ||
                (long)window.Column + window.Columns - 1 > WorksheetColumns)
                throw Error("Deletion observation has invalid dimensions.", "excel_delete_snapshot_invalid");
            if ((long)window.Rows * window.Columns > MaxCells)
                throw Error("Deletion affects more than " + MaxCells + " observable cells. Choose a smaller table scope.", "excel_delete_too_large");
        }

        private static void ValidateSnapshot(ExcelDeletionSnapshot snapshot, ExcelDeleteRowsColumnsRequest request)
        {
            if (snapshot == null || !string.Equals(snapshot.Sheet, request.Sheet, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(snapshot.Table ?? string.Empty, request.Table ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                throw Error("Deletion read-back resolved a different target.", "excel_delete_snapshot_invalid");
            ValidateWindow(snapshot.Window, request.Scope == ExcelDeletionScope.Table);
            if (snapshot.Cells == null || snapshot.Cells.Count != snapshot.Window.Rows ||
                snapshot.Cells.Any(row => row == null || row.Count != snapshot.Window.Columns ||
                    row.Any(cell => cell == null || cell.Formula == null && cell.ValueJson == null)))
                throw Error("Deletion backend returned incomplete cell evidence.", "excel_delete_snapshot_invalid");
            if (request.Scope == ExcelDeletionScope.Table && (snapshot.DataRows < 0 ||
                snapshot.HeaderRows < 0 || snapshot.HeaderRows > 1 || snapshot.TotalRows < 0 || snapshot.TotalRows > 1 ||
                snapshot.DataRows + snapshot.HeaderRows + snapshot.TotalRows != snapshot.Window.Rows ||
                snapshot.ColumnNames == null || snapshot.ColumnNames.Count != snapshot.Window.Columns))
                throw Error("Deletion backend returned incomplete table structure.", "excel_delete_snapshot_invalid");
        }

        public static string StateToken(ExcelDeletionSnapshot snapshot)
        {
            using (var hash = SHA256.Create())
                return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(snapshot))));
        }

        private static bool MatchesDeletion(ExcelDeletionSnapshot before, ExcelDeletionSnapshot after, ExcelDeleteRowsColumnsRequest selection)
        {
            var table = selection.Scope == ExcelDeletionScope.Table;
            var rows = selection.Axis == ExcelDeletionAxis.Rows;
            if (before.Window.Row != after.Window.Row || before.Window.Column != after.Window.Column ||
                after.Window.Rows != before.Window.Rows - (table && rows ? selection.Count : 0) ||
                after.Window.Columns != before.Window.Columns - (table && !rows ? selection.Count : 0)) return false;
            if (table && (after.DataRows != before.DataRows - (rows ? selection.Count : 0) ||
                before.HeaderRows != after.HeaderRows || before.TotalRows != after.TotalRows ||
                !after.ColumnNames.SequenceEqual(before.ColumnNames.Where((name, index) => rows ||
                    index < selection.Start - 1 || index >= selection.Start - 1 + selection.Count), StringComparer.Ordinal))) return false;
            for (var r = 0; r < after.Window.Rows; r++)
            for (var c = 0; c < after.Window.Columns; c++)
            {
                var sourceRow = r;
                var sourceColumn = c;
                if (rows && (table ? r >= selection.Start - 1 + before.HeaderRows : r + before.Window.Row >= selection.Start))
                    sourceRow += selection.Count;
                if (!rows && (table ? c >= selection.Start - 1 : c + before.Window.Column >= selection.Start))
                    sourceColumn += selection.Count;
                var expected = sourceRow < before.Window.Rows && sourceColumn < before.Window.Columns
                    ? before.Cells[sourceRow][sourceColumn] : new ExcelDeletionCell { ValueJson = "null" };
                var actual = after.Cells[r][c];
                // Excel owns reference rewriting/recalculation. Verify formula positions,
                // not an invented expected formula or its volatile computed value.
                if (expected.Formula != null ? actual.Formula == null : actual.Formula != null || expected.ValueJson != actual.ValueJson)
                    return false;
            }
            return true;
        }

        private static ExcelRangeMutationBackendException Error(string message, string code)
        { return new ExcelRangeMutationBackendException(message, code, false); }
        private static DomainOutcome Failure(string message, string code)
        { return DomainOutcome.Error(message, new JObject { ["code"] = code, ["retryable"] = false }.ToString(Formatting.None), code, false); }
        private static DomainOutcome Unknown(string message)
        {
            const string code = "excel_delete_effect_unknown";
            return DomainOutcome.Unknown(message, new JObject { ["code"] = code, ["retryable"] = false }.ToString(Formatting.None), code);
        }
    }
}
