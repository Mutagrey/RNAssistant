using System;

namespace RNAssistant.Office.Tools
{
    internal static class ExcelRangeMutationToolIds
    {
        internal const string FormatRange = "excel.range.format";
        internal const string ClearRange = "excel.range.clear";
        internal const string DeleteRowsColumns = "excel.rows.columns.delete";
        internal const string SortRange = "excel.range.sort";
        internal const string FilterRange = "excel.range.filter";

        internal static bool Owns(string toolId)
        {
            return string.Equals(toolId, FormatRange, StringComparison.Ordinal) ||
                string.Equals(toolId, ClearRange, StringComparison.Ordinal) ||
                string.Equals(toolId, DeleteRowsColumns, StringComparison.Ordinal) ||
                string.Equals(toolId, SortRange, StringComparison.Ordinal) ||
                string.Equals(toolId, FilterRange, StringComparison.Ordinal);
        }
    }
}
