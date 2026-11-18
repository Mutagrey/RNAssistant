using System;

namespace RNAssistant.Office.Domains.Excel
{
    // Only the native ISERROR boolean result is evidence of cell errors. Numeric
    // values, HRESULT-like integers and an unavailable/mis-sized result are not.
    public static class ExcelPowerQueryErrorMask
    {
        public static bool TryCount(object mask, int rows, int columns, out int count)
        {
            count = 0;
            if (rows < 1 || columns < 1 || (long)rows * columns > 4096) return false;
            if (rows == 1 && columns == 1 && mask is bool)
            {
                count = (bool)mask ? 1 : 0;
                return true;
            }
            var array = mask as Array;
            if (array == null || array.Rank != 2 || array.GetLength(0) != rows ||
                array.GetLength(1) != columns) return false;
            var total = 0;
            for (var row = array.GetLowerBound(0); row <= array.GetUpperBound(0); row++)
                for (var column = array.GetLowerBound(1); column <= array.GetUpperBound(1); column++)
                {
                    var value = array.GetValue(row, column);
                    if (!(value is bool)) return false;
                    if ((bool)value) total++;
                }
            count = total;
            return true;
        }
    }
}
