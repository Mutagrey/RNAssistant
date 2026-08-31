using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.OfficeHosts.Identity;
using Excel = Microsoft.Office.Interop.Excel;

namespace RNAssistant.OfficeHosts
{
    internal sealed class ExcelSheetInteropBackend : IExcelSheetBackend
    {
        private readonly ExcelDocumentSession _session;
        private readonly Excel.Workbook _workbook;

        internal ExcelSheetInteropBackend(ExcelDocumentSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _workbook = session.BoundDocumentObject as Excel.Workbook;
            if (_workbook == null)
                throw new InvalidOperationException(
                    "The bound Excel workbook is unavailable.");
        }

        public ExcelSheetCollectionSnapshot Read()
        {
            var workbook = RequireWorkbook();
            var names = ReadNames(workbook);
            var activeName = string.Empty;
            try
            {
                var active = workbook.ActiveSheet as Excel.Worksheet;
                if (BelongsToSession(active)) activeName = active.Name;
            }
            catch
            {
            }
            if (string.IsNullOrWhiteSpace(activeName) && names.Count > 0)
                activeName = names[0];
            return new ExcelSheetCollectionSnapshot
            {
                ActiveSheet = activeName,
                SheetNames = names
            };
        }

        public void Add(
            ExcelAddSheetApplyRequest request,
            Action markDispatchPossible)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (markDispatchPossible == null)
                throw new ArgumentNullException(nameof(markDispatchPossible));
            var workbook = RequireWorkbook();
            ValidateExpected(workbook, request.ExpectedSheetNames);
            ValidateName(workbook, request.Name, null);

            Excel.Worksheet added = null;
            try
            {
                markDispatchPossible();
                added = (Excel.Worksheet)workbook.Worksheets.Add();
                if (!BelongsToSession(added))
                    throw Failure(
                        "Excel added a worksheet outside the bound workbook.",
                        "excel_sheet_target_invalid", false);
                added.Name = request.Name;
            }
            catch
            {
                TryDeleteAdded(added);
                throw;
            }
        }

        public void Rename(
            ExcelRenameSheetApplyRequest request,
            Action markDispatchPossible)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (markDispatchPossible == null)
                throw new ArgumentNullException(nameof(markDispatchPossible));
            var workbook = RequireWorkbook();
            ValidateExpected(workbook, request.ExpectedSheetNames);
            var sheet = FindWorksheet(workbook, request.Sheet);
            if (sheet == null)
                throw Failure(
                    "Worksheet not found: " + (request.Sheet ?? string.Empty),
                    "excel_sheet_not_found", false);
            ValidateName(workbook, request.NewName, sheet.Name);
            markDispatchPossible();
            sheet.Name = request.NewName;
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

        private List<string> ReadNames(Excel.Workbook workbook)
        {
            var names = new List<string>();
            foreach (Excel.Worksheet sheet in workbook.Worksheets)
            {
                if (!BelongsToSession(sheet))
                    throw Failure(
                        "Excel worksheet belongs to another workbook.",
                        "excel_sheet_target_invalid", false);
                names.Add(sheet.Name);
            }
            return names;
        }

        private void ValidateExpected(
            Excel.Workbook workbook,
            IReadOnlyList<string> expected)
        {
            if (expected == null)
                throw Failure(
                    "Expected Excel sheet state is missing.",
                    "excel_sheet_target_changed", false);
            var current = ReadNames(workbook);
            if (current.Count != expected.Count || current.Where((name, index) =>
                !string.Equals(name, expected[index], StringComparison.Ordinal)).Any())
                throw Failure(
                    "Excel sheet collection changed before dispatch.",
                    "excel_sheet_target_changed", false);
        }

        private static void ValidateName(
            Excel.Workbook workbook, string name, string currentName)
        {
            if (!ExcelWorksheetNameRules.IsValid(name))
                throw Failure(
                    "Invalid Excel worksheet name: " + (name ?? string.Empty),
                    "excel_sheet_name_invalid", false);
            var existing = FindWorksheet(workbook, name);
            if (existing != null && !string.Equals(
                name, currentName, StringComparison.OrdinalIgnoreCase))
                throw Failure(
                    "Worksheet already exists: " + name,
                    "excel_sheet_already_exists", false);
        }

        private static Excel.Worksheet FindWorksheet(
            Excel.Workbook workbook, string name)
        {
            if (workbook == null || string.IsNullOrWhiteSpace(name)) return null;
            foreach (Excel.Worksheet sheet in workbook.Worksheets)
                if (string.Equals(sheet.Name, name, StringComparison.OrdinalIgnoreCase))
                    return sheet;
            return null;
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

        private void TryDeleteAdded(Excel.Worksheet sheet)
        {
            if (sheet == null || !BelongsToSession(sheet)) return;
            Excel.Application application = null;
            var displayAlerts = true;
            var restoreAlerts = false;
            try
            {
                application = _workbook.Application;
                if (application != null)
                {
                    displayAlerts = application.DisplayAlerts;
                    application.DisplayAlerts = false;
                    restoreAlerts = true;
                }
                sheet.Delete();
            }
            catch
            {
            }
            finally
            {
                if (restoreAlerts)
                {
                    try { application.DisplayAlerts = displayAlerts; }
                    catch { }
                }
            }
        }

        private static ExcelSheetBackendException Failure(
            string message, string code, bool retryable)
        {
            return new ExcelSheetBackendException(message, code, retryable);
        }
    }
}
