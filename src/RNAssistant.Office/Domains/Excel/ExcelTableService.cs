using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Office.Domains.Excel
{
    public sealed class ExcelTableService
    {
        public const int MaxTableCells = 100000;
        public const int MaxWorkbookTables = 1000;

        private readonly IExcelTableBackend _backend;

        public ExcelTableService(IExcelTableBackend backend)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        public ExcelTableOutcome Add(
            ExcelAddTableRequest request,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            request = request ?? new ExcelAddTableRequest();
            var sourceRange = string.IsNullOrWhiteSpace(request.SourceRange)
                ? "A1:B2" : request.SourceRange.Trim();
            var name = request.Name == null ? string.Empty : request.Name.Trim();
            var style = request.Style == null ? string.Empty : request.Style.Trim();
            var dispatched = false;
            Action mark = delegate
            {
                if (dispatched) return;
                dispatched = true;
                if (markDispatchPossible != null) markDispatchPossible();
            };
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var before = _backend.Read(ReadRequest(
                    request.Sheet, sourceRange, 0, 0));
                ValidateSnapshot(before, null);
                if (!string.IsNullOrWhiteSpace(name) && before.Tables.Any(table =>
                    string.Equals(table.Name, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(table.DisplayName, name,
                        StringComparison.OrdinalIgnoreCase)))
                    return Failure(
                        "Excel table already exists: " + name,
                        "excel_table_already_exists", false);

                cancellationToken.ThrowIfCancellationRequested();
                _backend.Add(new ExcelTableApplyRequest
                {
                    Sheet = before.Sheet,
                    SourceRange = before.SourceRange,
                    Name = name,
                    HasHeaders = request.HasHeaders,
                    Style = style,
                    Rows = before.Rows,
                    Columns = before.Columns,
                    MaxCells = MaxTableCells,
                    MaxTables = MaxWorkbookTables,
                    ExpectedStateToken = before.StateToken
                }, mark);
                if (!dispatched)
                {
                    mark();
                    return Unknown(
                        "Excel table backend returned without a dispatch boundary.",
                        "excel_table_dispatch_boundary_missing", before);
                }

                cancellationToken.ThrowIfCancellationRequested();
                var after = _backend.Read(ReadRequest(
                    before.Sheet, before.SourceRange,
                    before.Rows, before.Columns));
                ValidateSnapshot(after, before);
                var created = FindCreated(before.Tables, after.Tables);
                if (created == null || !MatchesRequest(
                    created, before, name, request.HasHeaders, style))
                    return Unknown(
                        "Excel table may have been added, but exact read-back diverged.",
                        "excel_table_verification_failed", after);
                var data = new JObject
                {
                    ["sheet"] = created.Sheet,
                    ["name"] = created.Name,
                    ["displayName"] = created.DisplayName,
                    ["range"] = created.Range,
                    ["rows"] = created.Rows,
                    ["columns"] = created.Columns,
                    ["hasHeaders"] = created.HasHeaders,
                    ["style"] = created.Style,
                    ["verification"] = "changed"
                };
                return ExcelTableOutcome.Ok(
                    "Table added: " + created.Name,
                    data.ToString(Formatting.None));
            }
            catch (OperationCanceledException)
            {
                if (!dispatched) throw;
                return Unknown(
                    "Cancellation was observed after the Excel table dispatch boundary; inspect the target before retrying.",
                    "excel_table_effect_unknown", null);
            }
            catch (ExcelTableBackendException ex)
            {
                return dispatched
                    ? Unknown(
                        "Excel table final state is unknown. " + ex.Message,
                        "excel_table_effect_unknown", null, ex.DetailsJson)
                    : Failure(ex.Message, ex.ErrorCode, ex.Retryable, ex.DetailsJson);
            }
            catch (Exception ex)
            {
                return dispatched
                    ? Unknown(
                        "Excel table final state is unknown. " + ex.Message,
                        "excel_table_effect_unknown", null)
                    : Failure(
                        "Excel table add failed before dispatch: " + ex.Message,
                        "excel_table_failed", true);
            }
        }

        private static ExcelTableReadRequest ReadRequest(
            string sheet, string sourceRange,
            int expectedRows, int expectedColumns)
        {
            return new ExcelTableReadRequest
            {
                Sheet = sheet ?? string.Empty,
                SourceRange = sourceRange,
                ExpectedRows = expectedRows,
                ExpectedColumns = expectedColumns,
                MaxCells = MaxTableCells,
                MaxTables = MaxWorkbookTables
            };
        }

        private static ExcelTableState FindCreated(
            IReadOnlyList<ExcelTableState> before,
            IReadOnlyList<ExcelTableState> after)
        {
            before = before ?? new ExcelTableState[0];
            after = after ?? new ExcelTableState[0];
            if (after.Count != before.Count + 1) return null;
            var remaining = new List<ExcelTableState>(after);
            foreach (var expected in before)
            {
                var index = remaining.FindIndex(actual => SameTable(expected, actual));
                if (index < 0) return null;
                remaining.RemoveAt(index);
            }
            return remaining.Count == 1 ? remaining[0] : null;
        }

        private static bool MatchesRequest(
            ExcelTableState table,
            ExcelTableCollectionSnapshot target,
            string name,
            bool hasHeaders,
            string style)
        {
            return table != null &&
                string.Equals(table.Sheet, target.Sheet,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(table.Range, target.SourceRange,
                    StringComparison.OrdinalIgnoreCase) &&
                table.Rows == target.Rows && table.Columns == target.Columns &&
                table.HasHeaders == hasHeaders &&
                (string.IsNullOrWhiteSpace(name) ||
                    string.Equals(table.Name, name, StringComparison.Ordinal)) &&
                (string.IsNullOrWhiteSpace(style) ||
                    string.Equals(table.Style, style,
                        StringComparison.OrdinalIgnoreCase));
        }

        private static bool SameTable(
            ExcelTableState left, ExcelTableState right)
        {
            return left != null && right != null &&
                string.Equals(left.Sheet, right.Sheet, StringComparison.Ordinal) &&
                string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
                string.Equals(left.DisplayName, right.DisplayName,
                    StringComparison.Ordinal) &&
                string.Equals(left.Range, right.Range, StringComparison.Ordinal) &&
                left.Rows == right.Rows && left.Columns == right.Columns &&
                left.HasHeaders == right.HasHeaders &&
                string.Equals(left.Style, right.Style, StringComparison.Ordinal);
        }

        private static void ValidateSnapshot(
            ExcelTableCollectionSnapshot snapshot,
            ExcelTableCollectionSnapshot expectedTarget)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Sheet) ||
                string.IsNullOrWhiteSpace(snapshot.SourceRange) ||
                string.IsNullOrWhiteSpace(snapshot.StateToken) ||
                snapshot.Tables == null)
                throw InvalidBackend(
                    "Excel table backend returned incomplete target state.");
            if (snapshot.Rows < 1 || snapshot.Columns < 1 ||
                snapshot.CellCount != (long)snapshot.Rows * snapshot.Columns ||
                snapshot.CellCount > MaxTableCells ||
                snapshot.Tables.Count > MaxWorkbookTables ||
                snapshot.Tables.Any(table => table == null ||
                    string.IsNullOrWhiteSpace(table.Sheet) ||
                    string.IsNullOrWhiteSpace(table.Name) ||
                    string.IsNullOrWhiteSpace(table.Range)))
                throw InvalidBackend(
                    "Excel table backend returned invalid target state.");
            if (expectedTarget != null &&
                (!string.Equals(snapshot.Sheet, expectedTarget.Sheet,
                    StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(snapshot.SourceRange, expectedTarget.SourceRange,
                    StringComparison.OrdinalIgnoreCase) ||
                 snapshot.Rows != expectedTarget.Rows ||
                 snapshot.Columns != expectedTarget.Columns))
                throw InvalidBackend(
                    "Excel table read-back resolved a different source range.");
        }

        private static ExcelTableOutcome Failure(
            string message, string code, bool retryable,
            string detailsJson = null)
        {
            return ExcelTableOutcome.Error(message,
                ErrorData(code, retryable, detailsJson).ToString(Formatting.None),
                code, retryable);
        }

        private static ExcelTableOutcome Unknown(
            string message, string code, ExcelTableCollectionSnapshot snapshot,
            string detailsJson = null)
        {
            var data = ErrorData(code, false, detailsJson);
            if (snapshot != null)
                data["target"] = new JObject
                {
                    ["sheet"] = snapshot.Sheet,
                    ["range"] = snapshot.SourceRange,
                    ["rows"] = snapshot.Rows,
                    ["columns"] = snapshot.Columns
                };
            return ExcelTableOutcome.Unknown(
                message, data.ToString(Formatting.None), code);
        }

        private static JObject ErrorData(
            string code, bool retryable, string detailsJson)
        {
            var data = new JObject
            {
                ["code"] = code,
                ["retryable"] = retryable
            };
            if (!string.IsNullOrWhiteSpace(detailsJson))
            {
                try { data["details"] = JToken.Parse(detailsJson); }
                catch (JsonException) { data["details"] = detailsJson; }
            }
            return data;
        }

        private static ExcelTableBackendException InvalidBackend(string message)
        {
            return new ExcelTableBackendException(
                message, "excel_table_snapshot_invalid", false);
        }
    }
}
