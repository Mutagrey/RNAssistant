using System;

namespace RNAssistant.Office.Tools
{
    internal static class ExcelRangeMutationToolIds
    {
        internal const string FormatRange = "excel.format_range";
        internal const string ClearRange = "excel.clear_range";
        internal const string SortRange = "excel.sort_range";
        internal const string FilterRange = "excel.filter_range";

        internal static bool Owns(string toolId)
        {
            return string.Equals(toolId, FormatRange, StringComparison.Ordinal) ||
                string.Equals(toolId, ClearRange, StringComparison.Ordinal) ||
                string.Equals(toolId, SortRange, StringComparison.Ordinal) ||
                string.Equals(toolId, FilterRange, StringComparison.Ordinal);
        }
    }
}
