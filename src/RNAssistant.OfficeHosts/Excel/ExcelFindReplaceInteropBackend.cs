using System;
using System.Collections.Generic;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.OfficeHosts.Identity;
using Excel = Microsoft.Office.Interop.Excel;

namespace RNAssistant.OfficeHosts
{
    internal sealed class ExcelFindReplaceInteropBackend : IExcelFindReplaceBackend
    {
        private readonly ExcelDocumentSession _session;
        private readonly Excel.Workbook _workbook;

        internal ExcelFindReplaceInteropBackend(ExcelDocumentSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _workbook = session.BoundDocumentObject as Excel.Workbook;
            if (_workbook == null)
                throw new InvalidOperationException(
                    "The bound Excel workbook is unavailable.");
        }

        public void ReadScope(
            ExcelCellScopeRequest request,
            Action<ExcelCellSnapshot> visit)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (visit == null) throw new ArgumentNullException(nameof(visit));
            var workbook = RequireWorkbook();
            var ranges = ResolveScopeRanges(workbook, request);
            if (request.MaxCells > 0)
            {
                long count = 0;
                foreach (var range in ranges)
                {
                    var size = Convert.ToInt64(range.Cells.CountLarge);
                    if (size < 0 || size > request.MaxCells - count)
                        throw Failure("Choose a smaller Excel search scope.", "RESOURCE_SNAPSHOT_TOO_LARGE", false);
                    count += size;
                }
            }
            foreach (var range in ranges)
            {
                foreach (Excel.Range cell in range.Cells)
                {
                    var snapshot = Snapshot(cell);
                    if (snapshot == null)
                        throw Failure(
                            "Excel scope resolved outside the bound workbook.",
                            "excel_scope_invalid", false);
                    visit(snapshot);
                }
            }
        }

        public void Apply(
            ExcelReplaceApplyRequest request,
            Action markDispatchPossible)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (markDispatchPossible == null)
                throw new ArgumentNullException(nameof(markDispatchPossible));
            RequireWorkbook();
            var replacements = request.Replacements;
            if (replacements == null)
                throw Failure(
                    "Excel replacement payload is missing.",
                    "excel_replace_payload_invalid", false);

            var targets = new List<BoundReplacement>(replacements.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var replacement in replacements)
            {
                if (replacement == null || string.IsNullOrWhiteSpace(replacement.Sheet) ||
                    string.IsNullOrWhiteSpace(replacement.Address))
                    throw Failure(
                        "Excel replacement target is invalid.",
                        "excel_replace_target_invalid", false);
                var key = replacement.Sheet + "\n" + replacement.Address;
                if (!seen.Add(key))
                    throw Failure(
                        "Excel replacement target is duplicated.",
                        "excel_replace_target_invalid", false);
                var sheet = ResolveSheet(_workbook, replacement.Sheet);
                var range = sheet.Range[replacement.Address];
                if (range == null || range.Areas.Count != 1 ||
                    Convert.ToInt32(range.Rows.Count) != 1 ||
                    Convert.ToInt32(range.Columns.Count) != 1 ||
                    !BelongsToSession(range.Worksheet as Excel.Worksheet))
                    throw Failure(
                        "Excel replacement target must be one bound cell.",
                        "excel_replace_target_invalid", false);
                var current = Snapshot(range);
                if (current == null ||
                    current.HasFormula != replacement.ExpectedHasFormula ||
                    !string.Equals(
                        current.Value,
                        replacement.ExpectedValue ?? string.Empty,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        current.Formula,
                        replacement.ExpectedFormula ?? string.Empty,
                        StringComparison.Ordinal))
                    throw Failure(
                        "Excel replacement target changed before dispatch.",
                        "excel_replace_target_changed", false);
                targets.Add(new BoundReplacement
                {
                    Range = range,
                    Formula = replacement.Formula,
                    Text = replacement.Text ?? string.Empty
                });
            }

            if (targets.Count == 0) return;
            markDispatchPossible();
            foreach (var target in targets)
            {
                if (target.Formula) target.Range.Formula = target.Text;
                else target.Range.Value2 = target.Text;
            }
        }

        private List<Excel.Range> ResolveScopeRanges(
            Excel.Workbook workbook,
            ExcelCellScopeRequest request)
        {
            var ranges = new List<Excel.Range>();
            var scope = (request.Scope ?? string.Empty).Trim().ToLowerInvariant();
            if (scope == "selection")
            {
                var selection = ResolveSelectionRange(workbook);
                if (selection == null)
                    throw Failure(
                        "No Excel range is selected in the bound workbook.",
                        "excel_selection_unavailable", false);
                ranges.Add(selection);
                return ranges;
            }
            if (scope == "range")
            {
                if (string.IsNullOrWhiteSpace(request.Address))
                    throw Failure(
                        "address is required for range scope.",
                        "excel_scope_invalid", false);
                ranges.Add(ResolveSheet(workbook, request.Sheet).Range[request.Address]);
                return ranges;
            }
            if (scope == "sheet" || !string.IsNullOrWhiteSpace(request.Sheet))
            {
                ranges.Add(ResolveSheet(workbook, request.Sheet).UsedRange);
                return ranges;
            }
            foreach (Excel.Worksheet sheet in workbook.Worksheets)
                ranges.Add(sheet.UsedRange);
            return ranges;
        }

        private ExcelCellSnapshot Snapshot(Excel.Range cell)
        {
            if (cell == null) return null;
            var sheet = cell.Worksheet as Excel.Worksheet;
            if (!BelongsToSession(sheet)) return null;
            return new ExcelCellSnapshot
            {
                Sheet = sheet.Name,
                Address = cell.Address[false, false],
                Value = Convert.ToString(cell.Value2) ?? string.Empty,
                Formula = Convert.ToString(cell.Formula) ?? string.Empty,
                HasFormula = Convert.ToBoolean(cell.HasFormula)
            };
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

        private bool BelongsToSession(Excel.Worksheet sheet)
        {
            try
            {
                var workbook = sheet == null ? null : sheet.Parent as Excel.Workbook;
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

        private static Excel.Worksheet ResolveSheet(
            Excel.Workbook workbook,
            string name)
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
            {
                if (string.Equals(
                    SafeString(delegate { return sheet.Name; }),
                    name,
                    StringComparison.OrdinalIgnoreCase))
                    return sheet;
            }
            throw Failure(
                "Worksheet not found: " + name,
                "excel_sheet_not_found", false);
        }

        private Excel.Range ResolveSelectionRange(Excel.Workbook workbook)
        {
            try
            {
                var application = workbook.Application;
                var range = application == null
                    ? null : application.Selection as Excel.Range;
                if (BelongsToSession(
                    range == null ? null : range.Worksheet as Excel.Worksheet))
                    return range;
                var activeCell = application == null
                    ? null : application.ActiveCell as Excel.Range;
                if (BelongsToSession(
                    activeCell == null ? null : activeCell.Worksheet as Excel.Worksheet))
                    return activeCell;
            }
            catch
            {
            }
            return null;
        }

        private static string SafeString(Func<string> getter)
        {
            try { return getter(); }
            catch { return string.Empty; }
        }

        private static ExcelFindReplaceBackendException Failure(
            string message, string code, bool retryable)
        {
            return new ExcelFindReplaceBackendException(
                message, code, retryable);
        }

        private sealed class BoundReplacement
        {
            internal Excel.Range Range { get; set; }
            internal bool Formula { get; set; }
            internal string Text { get; set; }
        }
    }
}
