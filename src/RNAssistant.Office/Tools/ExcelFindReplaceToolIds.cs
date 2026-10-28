using System;

namespace RNAssistant.Office.Tools
{
    public static class ExcelFindReplaceToolIds
    {
        public const string FindCells = "excel.cells.find";
        public const string ReplaceCells = "excel.cells.replace";

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
