using System;
using System.Collections.Generic;
using RNAssistant.Office.Domains.Excel;

namespace RNAssistant.Harness
{
    internal sealed partial class FakeOfficeAdapter
    {
        internal void SetExcelFormula(string sheetName, string address, string formula)
        {
            FakeSheet sheet;
            if (!_sheets.TryGetValue(sheetName ?? string.Empty, out sheet))
                throw new InvalidOperationException("Worksheet not found: " + sheetName);
            var cell = ParseAddress(address);
            var key = CellKey(cell.Row, cell.Column);
            sheet.Cells[key] = formula ?? string.Empty;
            sheet.FormulaCells.Add(key);
        }

        public void ReadScope(
            ExcelCellScopeRequest request,
            Action<ExcelCellSnapshot> visit)
        {
            BeginExcelBackendCall(ExcelFindScopeReadOperation);
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (visit == null) throw new ArgumentNullException(nameof(visit));
            foreach (var scope in ResolveFakeScope(request))
            {
                for (var row = scope.Range.Start.Row; row <= scope.Range.End.Row; row++)
                {
                    for (var column = scope.Range.Start.Column;
                        column <= scope.Range.End.Column; column++)
                    {
                        var key = CellKey(row, column);
                        object value;
                        if (!scope.Sheet.Cells.TryGetValue(key, out value)) value = null;
                        var text = Convert.ToString(value) ?? string.Empty;
                        visit(new ExcelCellSnapshot
                        {
                            Sheet = scope.Sheet.Name,
                            Address = FormatAddress(new FakeCellAddress
                            {
                                Row = row,
                                Column = column
                            }),
                            Value = text,
                            Formula = text,
                            HasFormula = scope.Sheet.FormulaCells.Contains(key)
                        });
                    }
                }
            }
        }

        public void Apply(
            ExcelReplaceApplyRequest request,
            Action markDispatchPossible)
        {
            BeginExcelBackendCall(ExcelReplaceApplyOperation);
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (markDispatchPossible == null)
                throw new ArgumentNullException(nameof(markDispatchPossible));
            if (request.Replacements == null)
                throw FindReplaceFailure("replacement payload missing", "excel_replace_payload_invalid");

            var targets = new List<FakeReplacement>();
            foreach (var replacement in request.Replacements)
            {
                FakeSheet sheet;
                if (replacement == null ||
                    !_sheets.TryGetValue(replacement.Sheet ?? string.Empty, out sheet))
                    throw FindReplaceFailure("replacement sheet missing", "excel_replace_target_invalid");
                var cell = ParseAddress(replacement.Address);
                var key = CellKey(cell.Row, cell.Column);
                object value;
                if (!sheet.Cells.TryGetValue(key, out value)) value = null;
                var text = Convert.ToString(value) ?? string.Empty;
                var hasFormula = sheet.FormulaCells.Contains(key);
                if (hasFormula != replacement.ExpectedHasFormula ||
                    !string.Equals(text, replacement.ExpectedValue ?? string.Empty,
                        StringComparison.Ordinal) ||
                    !string.Equals(text, replacement.ExpectedFormula ?? string.Empty,
                        StringComparison.Ordinal))
                    throw FindReplaceFailure(
                        "replacement target changed", "excel_replace_target_changed");
                targets.Add(new FakeReplacement
                {
                    Sheet = sheet,
                    Key = key,
                    Formula = replacement.Formula,
                    Text = replacement.Text ?? string.Empty
                });
            }

            if (targets.Count == 0) return;
            markDispatchPossible();
            foreach (var target in targets)
            {
                target.Sheet.Cells[target.Key] = target.Text;
                if (target.Formula) target.Sheet.FormulaCells.Add(target.Key);
                else target.Sheet.FormulaCells.Remove(target.Key);
            }
            if (ExcelReplaceThrowAfterMutation)
            {
                ExcelReplaceThrowAfterMutation = false;
                throw new InvalidOperationException(
                    "scripted failure after Excel replacement mutation");
            }
        }

        private IEnumerable<FakeScopeRange> ResolveFakeScope(
            ExcelCellScopeRequest request)
        {
            var scope = (request.Scope ?? string.Empty).Trim().ToLowerInvariant();
            if (scope == "selection")
            {
                FakeSheet selected;
                if (!_sheets.TryGetValue("Data", out selected))
                {
                    foreach (var candidate in _sheets.Values)
                    {
                        selected = candidate;
                        break;
                    }
                }
                if (selected == null)
                    throw FindReplaceFailure("selection unavailable", "excel_selection_unavailable");
                yield return new FakeScopeRange
                {
                    Sheet = selected,
                    Range = ParseRange("A1:B4")
                };
                yield break;
            }

            if (scope == "range")
            {
                if (string.IsNullOrWhiteSpace(request.Address))
                    throw FindReplaceFailure("address is required", "excel_scope_invalid");
                var sheet = ResolveFakeSheet(request.Sheet);
                yield return new FakeScopeRange
                {
                    Sheet = sheet,
                    Range = ParseRange(request.Address)
                };
                yield break;
            }

            if (scope == "sheet" || !string.IsNullOrWhiteSpace(request.Sheet))
            {
                var sheet = ResolveFakeSheet(request.Sheet);
                yield return new FakeScopeRange
                {
                    Sheet = sheet,
                    Range = FakeUsedRange(sheet)
                };
                yield break;
            }

            foreach (var sheet in _sheets.Values)
            {
                yield return new FakeScopeRange
                {
                    Sheet = sheet,
                    Range = FakeUsedRange(sheet)
                };
            }
        }

        private FakeSheet ResolveFakeSheet(string name)
        {
            var sheetName = string.IsNullOrWhiteSpace(name) ? "Data" : name;
            FakeSheet sheet;
            if (_sheets.TryGetValue(sheetName, out sheet)) return sheet;
            throw FindReplaceFailure("worksheet not found", "excel_sheet_not_found");
        }

        private static FakeRange FakeUsedRange(FakeSheet sheet)
        {
            var minRow = int.MaxValue;
            var minColumn = int.MaxValue;
            var maxRow = 1;
            var maxColumn = 1;
            foreach (var key in sheet.Cells.Keys)
            {
                var parts = key.Split(':');
                int row;
                int column;
                if (parts.Length != 2 || !int.TryParse(parts[0], out row) ||
                    !int.TryParse(parts[1], out column)) continue;
                minRow = Math.Min(minRow, row);
                minColumn = Math.Min(minColumn, column);
                maxRow = Math.Max(maxRow, row);
                maxColumn = Math.Max(maxColumn, column);
            }
            if (minRow == int.MaxValue)
            {
                minRow = 1;
                minColumn = 1;
            }
            return new FakeRange
            {
                Start = new FakeCellAddress { Row = minRow, Column = minColumn },
                End = new FakeCellAddress { Row = maxRow, Column = maxColumn }
            };
        }

        private static ExcelFindReplaceBackendException FindReplaceFailure(
            string message, string code)
        {
            return new ExcelFindReplaceBackendException(message, code, false);
        }

        private sealed class FakeScopeRange
        {
            internal FakeSheet Sheet { get; set; }
            internal FakeRange Range { get; set; }
        }

        private sealed class FakeReplacement
        {
            internal FakeSheet Sheet { get; set; }
            internal string Key { get; set; }
            internal bool Formula { get; set; }
            internal string Text { get; set; }
        }
    }
}
