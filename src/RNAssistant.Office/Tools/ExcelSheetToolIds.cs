using System;

namespace RNAssistant.Office.Tools
{
    internal static class ExcelSheetToolIds
    {
        internal const string AddSheet = "excel.add_sheet";
        internal const string RenameSheet = "excel.rename_sheet";

        internal static bool Owns(string toolId)
        {
            return string.Equals(toolId, AddSheet, StringComparison.Ordinal) ||
                string.Equals(toolId, RenameSheet, StringComparison.Ordinal);
        }
    }
}
