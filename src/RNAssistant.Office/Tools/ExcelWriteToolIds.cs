using System;

namespace RNAssistant.Office.Tools
{
    public static class ExcelWriteToolIds
    {
        public const string WriteRange = "excel.range.write";

        public static bool Owns(string toolId)
        {
            return string.Equals(toolId, WriteRange, StringComparison.Ordinal);
        }
    }
}
