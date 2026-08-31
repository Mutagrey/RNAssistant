using System;

namespace RNAssistant.Office.Tools
{
    public static class ExcelFindReplaceToolIds
    {
        public const string FindCells = "excel.find_cells";
        public const string ReplaceCells = "excel.replace_cells";

        public static bool Owns(string toolId)
        {
            return string.Equals(toolId, FindCells, StringComparison.Ordinal) ||
                string.Equals(toolId, ReplaceCells, StringComparison.Ordinal);
        }

        public static bool IsMutation(string toolId)
        {
            return string.Equals(toolId, ReplaceCells, StringComparison.Ordinal);
        }
    }
}
