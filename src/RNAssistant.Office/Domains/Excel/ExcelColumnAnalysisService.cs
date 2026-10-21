using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;

namespace RNAssistant.Office.Domains.Excel
{
    // Numeric and occupancy observations over explicitly selected columns.
    // Each native capture is small; no worksheet-sized matrix reaches the model.
    internal sealed class ExcelColumnAnalysisService
    {
        internal const int MaximumColumns = 8;
        internal const long MaximumCells = 2000000;
        internal const int ChunkRows = 1024;
        private readonly ExcelReadService _reader;

        internal ExcelColumnAnalysisService(IExcelReadBackend backend)
        { _reader = new ExcelReadService(backend); }

        internal ExcelReadOutcome Analyze(string sheet, string address,
            IReadOnlyList<string> requestedColumns, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(sheet) || sheet.Length > 128 || sheet.Any(char.IsControl))
                return Reject("excel_analysis_sheet_required", "Specify a literal worksheet name.");
            var match = Regex.Match(address ?? string.Empty,
                @"\A(?<firstColumn>[A-Za-z]{1,3})(?<firstRow>[1-9][0-9]{0,6})(?::(?<lastColumn>[A-Za-z]{1,3})(?<lastRow>[1-9][0-9]{0,6}))?\z");
            if (!match.Success)
                return Reject("excel_analysis_address_invalid", "Specify an explicit bounded A1 rectangle.");
            var firstColumn = ColumnNumber(match.Groups["firstColumn"].Value);
            var lastColumn = match.Groups["lastColumn"].Success
                ? ColumnNumber(match.Groups["lastColumn"].Value) : firstColumn;
            var firstRow = int.Parse(match.Groups["firstRow"].Value, CultureInfo.InvariantCulture);
            var lastRow = match.Groups["lastRow"].Success
                ? int.Parse(match.Groups["lastRow"].Value, CultureInfo.InvariantCulture) : firstRow;
            if (firstColumn < 1 || lastColumn > 16384 || lastColumn < firstColumn ||
                firstRow < 1 || lastRow > 1048576 || lastRow < firstRow)
                return Reject("excel_analysis_address_invalid", "The A1 rectangle exceeds Excel bounds or is reversed.");
            if (requestedColumns == null || requestedColumns.Count < 1 || requestedColumns.Count > MaximumColumns)
                return Reject("excel_analysis_columns_invalid", "Choose 1 to 8 explicit columns inside the rectangle.");
            var numbers = new HashSet<int>();
            var columns = new List<int>();
            foreach (var requested in requestedColumns)
            {
                if (!Regex.IsMatch(requested ?? string.Empty, @"\A[A-Za-z]{1,3}\z"))
                    return Reject("excel_analysis_columns_invalid", "Use A1 column letters without quotes or ranges.");
                var number = ColumnNumber(requested);
                if (number < firstColumn || number > lastColumn || !numbers.Add(number))
                    return Reject("excel_analysis_columns_invalid", "Columns must be distinct and inside the requested rectangle.");
                columns.Add(number);
            }
            var rows = lastRow - firstRow + 1;
            var cells = (long)rows * columns.Count;
            if (cells > MaximumCells)
                return Reject("excel_analysis_too_large", "At most 2000000 selected cells can be analyzed in one call. Choose fewer columns or a smaller rectangle.");
            var normalized = Cell(firstColumn, firstRow) + (firstColumn == lastColumn && firstRow == lastRow
                ? string.Empty : ":" + Cell(lastColumn, lastRow));
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var start = _reader.CaptureRange(sheet, normalized, "metadata");
                CheckSnapshot(start, sheet, normalized, rows, lastColumn - firstColumn + 1);
                var summaries = new List<ExcelColumnAnalysis>();
                foreach (var number in columns)
                {
                    var summary = new ExcelColumnAnalysis { Column = ColumnName(number) };
                    var compensation = 0d;
                    for (var row = firstRow; row <= lastRow;)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var count = Math.Min(ChunkRows, lastRow - row + 1);
                        var chunk = Cell(number, row) + (count == 1 ? string.Empty : ":" + Cell(number, row + count - 1));
                        var captured = _reader.CaptureRange(sheet, chunk, "analysis-values");
                        CheckSnapshot(captured, sheet, chunk, count, 1);
                        for (var index = 0; index < count; index++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var value = captured.Values[index][0];
                            if (value == null) { summary.EmptyCells++; continue; }
                            if (!IsNumber(value)) { summary.OtherCells++; continue; }
                            var numberValue = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                            if (double.IsNaN(numberValue) || double.IsInfinity(numberValue))
                                throw new ExcelReadBackendException("Non-finite numeric cell value.", "excel_analysis_numeric_invalid", false);
                            var previous = summary.Sum ?? 0d;
                            var corrected = numberValue - compensation;
                            var next = previous + corrected;
                            compensation = (next - previous) - corrected;
                            if (double.IsNaN(next) || double.IsInfinity(next))
                                throw new ExcelReadBackendException("Numeric aggregate overflowed.", "excel_analysis_numeric_overflow", false);
                            summary.Sum = next;
                            summary.NumericCells++;
                            summary.Min = !summary.Min.HasValue ? numberValue : Math.Min(summary.Min.Value, numberValue);
                            summary.Max = !summary.Max.HasValue ? numberValue : Math.Max(summary.Max.Value, numberValue);
                        }
                        row += count;
                    }
                    if (summary.NumericCells > 0) summary.Average = summary.Sum / summary.NumericCells;
                    summaries.Add(summary);
                }
                cancellationToken.ThrowIfCancellationRequested();
                var end = _reader.CaptureRange(sheet, normalized, "metadata");
                CheckSnapshot(end, start.Sheet, normalized, rows, lastColumn - firstColumn + 1);
                var result = new ExcelColumnAnalysisResult { Sheet = end.Sheet, Address = normalized,
                    RowsScanned = rows, CellsScanned = cells, Columns = summaries };
                return ExcelReadOutcome.Ok("Selected columns were fully scanned in bounded chunks.",
                    JsonConvert.SerializeObject(result));
            }
            catch (ExcelReadBackendException ex)
            {
                return Reject(ex.ErrorCode, ex.Message);
            }
        }

        private static void CheckSnapshot(ExcelRangeSnapshot snapshot, string sheet, string address, int rows, int columns)
        {
            if (!string.Equals(snapshot.Sheet, sheet, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(snapshot.Address, address, StringComparison.OrdinalIgnoreCase) ||
                snapshot.Rows != rows || snapshot.Columns != columns)
                throw new ExcelReadBackendException("The live range changed during analysis.", "excel_analysis_source_changed", false);
        }

        private static bool IsNumber(object value)
        {
            switch (Type.GetTypeCode(value.GetType()))
            {
                case TypeCode.Byte: case TypeCode.SByte: case TypeCode.Int16: case TypeCode.UInt16:
                case TypeCode.Int32: case TypeCode.UInt32: case TypeCode.Int64: case TypeCode.UInt64:
                case TypeCode.Single: case TypeCode.Double: case TypeCode.Decimal: return true;
                default: return false;
            }
        }

        private static int ColumnNumber(string letters)
        {
            var result = 0;
            foreach (var character in letters.ToUpperInvariant()) result = result * 26 + character - 'A' + 1;
            return result;
        }

        private static string ColumnName(int column)
        {
            var letters = string.Empty;
            while (column > 0)
            {
                column--;
                letters = (char)('A' + column % 26) + letters;
                column /= 26;
            }
            return letters;
        }

        private static string Cell(int column, int row)
        { return ColumnName(column) + row.ToString(CultureInfo.InvariantCulture); }

        private static ExcelReadOutcome Reject(string code, string message)
        { return ExcelReadOutcome.Fail(message, JsonConvert.SerializeObject(new { code, retryable = false }), code, false); }
    }
}
