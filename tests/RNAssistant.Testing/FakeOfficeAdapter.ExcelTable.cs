using RNAssistant.Core.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Domains.Excel;

namespace RNAssistant.Harness
{
    internal sealed partial class FakeOfficeAdapter
    {
        public ExcelTableCollectionSnapshot Read(ExcelTableReadRequest request)
        {
            BeginExcelBackendCall(ExcelTableReadOperation);
            var snapshot = CreateExcelTableSnapshot(request);
            var transform = ExcelTableReadTransform;
            return transform == null ? snapshot : transform(snapshot);
        }

        public void Add(
            ExcelTableApplyRequest request,
            Action markDispatchPossible)
        {
            BeginExcelBackendCall(ExcelTableAddOperation);
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (markDispatchPossible == null)
                throw new ArgumentNullException(nameof(markDispatchPossible));
            ExcelTableRequests.Add(new ToolInvocation
            {
                ToolId = "excel.add_table",
                Arguments = new Dictionary<string, object>
                {
                    { "sheet", request.Sheet },
                    { "sourceRange", request.SourceRange },
                    { "name", request.Name },
                    { "hasHeaders", request.HasHeaders },
                    { "style", request.Style }
                }
            });
            ThrowQueuedExcelTableFailure();
            var current = CreateExcelTableSnapshot(new ExcelTableReadRequest
            {
                Sheet = request.Sheet,
                SourceRange = request.SourceRange,
                ExpectedRows = request.Rows,
                ExpectedColumns = request.Columns,
                MaxCells = request.MaxCells,
                MaxTables = request.MaxTables
            });
            if (!string.Equals(
                current.StateToken, request.ExpectedStateToken,
                StringComparison.Ordinal))
                throw TableFailure(
                    "table source or collection changed",
                    "excel_table_target_changed");
            if (current.Tables.Count >= request.MaxTables)
                throw TableFailure(
                    "table collection limit reached",
                    "excel_table_limit_reached");
            if (!string.IsNullOrWhiteSpace(request.Name) &&
                current.Tables.Any(table =>
                    string.Equals(table.Name, request.Name,
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(table.DisplayName, request.Name,
                        StringComparison.OrdinalIgnoreCase)))
                throw TableFailure(
                    "table already exists", "excel_table_already_exists");

            markDispatchPossible();
            var sheet = ResolveFakeTableSheet(request.Sheet);
            var name = string.IsNullOrWhiteSpace(request.Name)
                ? NextFakeTableName() : request.Name;
            sheet.Tables.Add(new FakeTable
            {
                Name = name,
                DisplayName = name,
                Range = current.SourceRange,
                Rows = current.Rows,
                Columns = current.Columns,
                HasHeaders = request.HasHeaders,
                Style = request.Style ?? string.Empty
            });
            if (ExcelTableThrowAfterMutation)
            {
                ExcelTableThrowAfterMutation = false;
                throw new InvalidOperationException(
                    "scripted failure after Excel table mutation");
            }
        }

        internal void AddExcelTableForTest(
            string sheetName, string sourceRange, string name,
            bool hasHeaders, string style)
        {
            var sheet = ResolveFakeTableSheet(sheetName);
            var range = ParseRange(sourceRange);
            sheet.Tables.Add(new FakeTable
            {
                Name = name,
                DisplayName = name,
                Range = FormatRange(range),
                Rows = range.End.Row - range.Start.Row + 1,
                Columns = range.End.Column - range.Start.Column + 1,
                HasHeaders = hasHeaders,
                Style = style ?? string.Empty
            });
        }

        internal int ExcelTableCount(string sheetName)
        {
            FakeSheet sheet;
            return _sheets.TryGetValue(sheetName ?? string.Empty, out sheet)
                ? sheet.Tables.Count : 0;
        }

        internal ExcelTableState ExcelTableForTest(
            string sheetName, string name)
        {
            FakeSheet sheet;
            if (!_sheets.TryGetValue(sheetName ?? string.Empty, out sheet))
                return null;
            var table = sheet.Tables.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name,
                    StringComparison.OrdinalIgnoreCase));
            return table == null ? null : TableState(sheet, table);
        }

        private ExcelTableCollectionSnapshot CreateExcelTableSnapshot(
            ExcelTableReadRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.MaxCells < 1 ||
                request.MaxCells > ExcelTableService.MaxTableCells ||
                request.MaxTables < 1 ||
                request.MaxTables > ExcelTableService.MaxWorkbookTables)
                throw TableFailure(
                    "invalid table bound", "excel_table_bound_invalid");
            var sheet = ResolveFakeTableSheet(request.Sheet);
            var range = ParseRange(string.IsNullOrWhiteSpace(request.SourceRange)
                ? "A1:B2" : request.SourceRange);
            var rows = range.End.Row - range.Start.Row + 1;
            var columns = range.End.Column - range.Start.Column + 1;
            var cells = (long)rows * columns;
            if (rows < 1 || columns < 1 || cells > request.MaxCells)
                throw TableFailure(
                    "table source is too large", "excel_table_too_large");
            if ((request.ExpectedRows > 0 || request.ExpectedColumns > 0) &&
                (rows != request.ExpectedRows ||
                 columns != request.ExpectedColumns))
                throw TableFailure(
                    "table source dimensions changed",
                    "excel_table_target_changed");
            var tables = _excelSheetOrder
                .SelectMany(sheetName => _sheets[sheetName].Tables.Select(table =>
                    TableState(_sheets[sheetName], table)))
                .ToList();
            if (tables.Count > request.MaxTables)
                throw TableFailure(
                    "table collection is too large",
                    "excel_table_collection_too_large");
            var address = FormatRange(range);
            return new ExcelTableCollectionSnapshot
            {
                Sheet = sheet.Name,
                SourceRange = address,
                Rows = rows,
                Columns = columns,
                CellCount = cells,
                Tables = tables,
                StateToken = FakeExcelTableState(
                    sheet, range, address, tables)
            };
        }

        private string FakeExcelTableState(
            FakeSheet sheet, FakeRange range, string address,
            IReadOnlyList<ExcelTableState> tables)
        {
            var cells = new JArray();
            for (var row = range.Start.Row; row <= range.End.Row; row++)
            {
                var line = new JArray();
                for (var column = range.Start.Column;
                    column <= range.End.Column; column++)
                {
                    var key = CellKey(row, column);
                    object value;
                    line.Add(new JObject
                    {
                        ["value"] = sheet.Cells.TryGetValue(key, out value)
                            ? JToken.FromObject(value ?? string.Empty)
                            : JValue.CreateNull(),
                        ["formula"] = sheet.FormulaCells.Contains(key)
                    });
                }
                cells.Add(line);
            }
            return new JObject
            {
                ["sheet"] = sheet.Name,
                ["range"] = address,
                ["cells"] = cells,
                ["tables"] = JArray.FromObject(tables)
            }.ToString(Formatting.None);
        }

        private FakeSheet ResolveFakeTableSheet(string sheetName)
        {
            if (string.IsNullOrWhiteSpace(sheetName))
            {
                FakeSheet active;
                if (!string.IsNullOrWhiteSpace(_activeExcelSheetName) &&
                    _sheets.TryGetValue(_activeExcelSheetName, out active))
                    return active;
                return _sheets.Values.FirstOrDefault() ?? EnsureSheet("Sheet1");
            }
            FakeSheet sheet;
            if (!_sheets.TryGetValue(sheetName, out sheet))
                throw TableFailure(
                    "worksheet not found", "excel_sheet_not_found");
            return sheet;
        }

        private string NextFakeTableName()
        {
            var used = new HashSet<string>(_sheets.Values
                .SelectMany(sheet => sheet.Tables)
                .Select(table => table.Name), StringComparer.OrdinalIgnoreCase);
            for (var index = 1; ; index++)
            {
                var candidate = "Table" + index;
                if (!used.Contains(candidate)) return candidate;
            }
        }

        private static ExcelTableState TableState(
            FakeSheet sheet, FakeTable table)
        {
            return new ExcelTableState
            {
                Sheet = sheet.Name,
                Name = table.Name,
                DisplayName = table.DisplayName,
                Range = table.Range,
                Rows = table.Rows,
                Columns = table.Columns,
                HasHeaders = table.HasHeaders,
                Style = table.Style
            };
        }

        private void ThrowQueuedExcelTableFailure()
        {
            if (_nextExcelTableApplyFailure == null) return;
            var failure = _nextExcelTableApplyFailure;
            _nextExcelTableApplyFailure = null;
            throw failure;
        }

        private static ExcelTableBackendException TableFailure(
            string message, string code)
        {
            return new ExcelTableBackendException(message, code, false);
        }
    }
}
