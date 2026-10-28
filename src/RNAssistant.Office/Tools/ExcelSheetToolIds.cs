using System;

namespace RNAssistant.Office.Tools
{
    internal static class ExcelSheetToolIds
    {
        internal const string AddSheet = "excel.sheet.add";
        internal const string RenameSheet = "excel.sheet.rename";

        internal static bool Owns(string toolId)
        {
            return string.Equals(toolId, AddSheet, StringComparison.Ordinal) ||
                string.Equals(toolId, RenameSheet, StringComparison.Ordinal);
        }
    }
}
