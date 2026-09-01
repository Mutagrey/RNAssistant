using RNAssistant.Core.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Domains.Excel;

namespace RNAssistant.Harness
{
    internal sealed partial class FakeOfficeAdapter
    {
        public ExcelSheetCollectionSnapshot Read()
        {
            BeginExcelBackendCall(ExcelSheetReadOperation);
            var snapshot = new ExcelSheetCollectionSnapshot
            {
                ActiveSheet = string.IsNullOrWhiteSpace(_activeExcelSheetName)
                    ? _excelSheetOrder.FirstOrDefault() ?? string.Empty
                    : _activeExcelSheetName,
                SheetNames = _excelSheetOrder.ToArray()
            };
            var transform = ExcelSheetReadTransform;
            return transform == null ? snapshot : transform(snapshot);
        }

        public void Add(
            ExcelAddSheetApplyRequest request,
            Action markDispatchPossible)
        {
            BeginExcelBackendCall(ExcelSheetAddOperation);
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (markDispatchPossible == null)
                throw new ArgumentNullException(nameof(markDispatchPossible));
            ExcelSheetRequests.Add(new ToolInvocation
            {
                ToolId = "excel.add_sheet",
                Arguments = new Dictionary<string, object> { { "name", request.Name } }
            });
            ThrowQueuedExcelSheetFailure();
            ValidateExpectedSheetNames(request.ExpectedSheetNames);
            if (!ExcelWorksheetNameRules.IsValid(request.Name))
                throw SheetFailure("invalid worksheet name", "excel_sheet_name_invalid");
            if (_sheets.ContainsKey(request.Name))
                throw SheetFailure("worksheet already exists", "excel_sheet_already_exists");

            markDispatchPossible();
            EnsureSheet(request.Name);
            _activeExcelSheetName = request.Name;
            if (ExcelSheetThrowAfterMutation)
            {
                ExcelSheetThrowAfterMutation = false;
                throw new InvalidOperationException(
                    "scripted failure after Excel sheet mutation");
            }
        }

        public void Rename(
            ExcelRenameSheetApplyRequest request,
            Action markDispatchPossible)
        {
            BeginExcelBackendCall(ExcelSheetRenameOperation);
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (markDispatchPossible == null)
                throw new ArgumentNullException(nameof(markDispatchPossible));
            ExcelSheetRequests.Add(new ToolInvocation
            {
                ToolId = "excel.rename_sheet",
                Arguments = new Dictionary<string, object>
                {
                    { "sheet", request.Sheet },
                    { "newName", request.NewName }
                }
            });
            ThrowQueuedExcelSheetFailure();
            ValidateExpectedSheetNames(request.ExpectedSheetNames);
            FakeSheet sheet;
            if (!_sheets.TryGetValue(request.Sheet ?? string.Empty, out sheet))
                throw SheetFailure("worksheet not found", "excel_sheet_not_found");
            if (!ExcelWorksheetNameRules.IsValid(request.NewName))
                throw SheetFailure("invalid worksheet name", "excel_sheet_name_invalid");
            FakeSheet collision;
            if (_sheets.TryGetValue(request.NewName, out collision) &&
                !ReferenceEquals(collision, sheet))
                throw SheetFailure("worksheet already exists", "excel_sheet_already_exists");

            markDispatchPossible();
            var index = _excelSheetOrder.FindIndex(name =>
                string.Equals(name, sheet.Name, StringComparison.OrdinalIgnoreCase));
            _sheets.Remove(sheet.Name);
            var oldName = sheet.Name;
            sheet.Name = request.NewName;
            _sheets[request.NewName] = sheet;
            if (index >= 0) _excelSheetOrder[index] = request.NewName;
            if (string.Equals(
                _activeExcelSheetName, oldName, StringComparison.OrdinalIgnoreCase))
                _activeExcelSheetName = request.NewName;
            if (ExcelSheetThrowAfterMutation)
            {
                ExcelSheetThrowAfterMutation = false;
                throw new InvalidOperationException(
                    "scripted failure after Excel sheet mutation");
            }
        }

        internal void AddExcelSheetForTest(string name)
        {
            EnsureSheet(name);
        }

        internal void SetActiveExcelSheet(string name)
        {
            if (!_sheets.ContainsKey(name ?? string.Empty))
                throw new InvalidOperationException("Worksheet not found: " + name);
            _activeExcelSheetName = name;
        }

        private void ValidateExpectedSheetNames(IReadOnlyList<string> expected)
        {
            if (expected == null || expected.Count != _excelSheetOrder.Count)
                throw SheetFailure(
                    "sheet collection changed", "excel_sheet_target_changed");
            for (var index = 0; index < expected.Count; index++)
                if (!string.Equals(
                    expected[index], _excelSheetOrder[index], StringComparison.Ordinal))
                    throw SheetFailure(
                        "sheet collection changed", "excel_sheet_target_changed");
        }

        private void ThrowQueuedExcelSheetFailure()
        {
            if (_nextExcelSheetApplyFailure == null) return;
            var failure = _nextExcelSheetApplyFailure;
            _nextExcelSheetApplyFailure = null;
            throw failure;
        }

        private static ExcelSheetBackendException SheetFailure(
            string message, string code)
        {
            return new ExcelSheetBackendException(message, code, false);
        }
    }
}
