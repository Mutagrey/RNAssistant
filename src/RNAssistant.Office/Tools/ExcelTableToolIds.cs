using System;

namespace RNAssistant.Office.Tools
{
    internal static class ExcelTableToolIds
    {
        internal const string AddTable = "excel.table.add";

        internal static bool Owns(string toolId)
        {
            return string.Equals(toolId, AddTable, StringComparison.Ordinal);
        }
    }
}
