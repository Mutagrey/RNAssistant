using System;
using RNAssistant.Office.Domains.Excel;
using Excel = Microsoft.Office.Interop.Excel;

namespace RNAssistant.OfficeHosts
{
    internal sealed partial class ExcelPowerQueryInteropBackend
    {
        public ExcelPowerQueryCellErrorSnapshot ReadWorksheetErrors(
            string queryName, string sheet, string table, int maximumCells)
        {
            RequireWorkbook();
            if (maximumCells < 1 || maximumCells > 100000)
                throw Failure("The worksheet error check requires a cell bound from 1 through 100000.",
                    "excel_query_bound_invalid");
            var result = new ExcelPowerQueryCellErrorSnapshot();
            try
            {
                EnsureNoNativeRefresh();
                var before = ReadBindings(queryName);
                var binding = RequireBinding(before, sheet, table);
                Excel.Worksheet worksheet;
                Excel.QueryTable queryTable;
                var list = ResolveLoad(binding, out worksheet, out queryTable);
                var body = list.DataBodyRange;
                var rows = body == null ? 0 : Convert.ToInt32(body.Rows.Count);
                var columns = list.ListColumns.Count;
                if ((long)rows * columns > maximumCells)
                {
                    result.Message = "The full loaded table exceeds the requested error-check bound; no full error count is established.";
                    return result;
                }
                var errors = 0;
                var rowsPerBlock = Math.Max(1, 4096 / Math.Max(1, columns));
                for (var row = 1; row <= rows; row += rowsPerBlock)
                {
                    var takeRows = Math.Min(rowsPerBlock, rows - row + 1);
                    for (var column = 1; column <= columns; column += 4096)
                    {
                        var takeColumns = Math.Min(4096, columns - column + 1);
                        var anchor = (Excel.Range)body.Cells[row, column];
                        var block = anchor.Resize[takeRows, takeColumns];
                        // Only a native absolute local address is inserted. User names,
                        // SQL, connection strings and M text are never evaluated here.
                        var address = block.Address[true, true, Excel.XlReferenceStyle.xlA1, false];
                        var mask = worksheet.Evaluate("ISERROR(" + address + ")");
                        int count;
                        if (!ExcelPowerQueryErrorMask.TryCount(mask, takeRows, takeColumns, out count))
                        {
                            result.Message = "Excel did not return an exact boolean error mask; the error count is unavailable.";
                            return result;
                        }
                        errors += count;
                    }
                }
                EnsureNoNativeRefresh();
                var after = ReadBindings(queryName);
                var afterBinding = RequireBinding(after, binding.Sheet, binding.Table);
                if (!string.Equals(after.BindingVersion, before.BindingVersion, StringComparison.Ordinal) ||
                    !string.Equals(afterBinding.BindingIdentity, binding.BindingIdentity, StringComparison.Ordinal) ||
                    !string.Equals(afterBinding.DataRange, binding.DataRange, StringComparison.Ordinal) ||
                    afterBinding.Rows != binding.Rows || afterBinding.Columns != binding.Columns)
                {
                    result.Message = "The loaded table binding or shape changed during the error check.";
                    return result;
                }
                result.Complete = true;
                result.ErrorCount = errors;
                result.Message = "The bounded full loaded table was checked with Excel's boolean ISERROR mask. This does not establish source freshness or absence of errors outside this worksheet load.";
                return result;
            }
            catch (Exception)
            {
                // A failed Evaluate/COM read is never interpreted as zero errors.
                result.Message = "Excel could not complete the loaded-cell error check; the error count is unavailable.";
                return result;
            }
            finally { result.ObservedAtUtc = DateTime.UtcNow; }
        }

    }
}
