using System;

namespace RNAssistant.Office.Tools
{
    public static class ExcelReadToolIds
    {
        public const string Inspect = "excel.inspect";
        public const string SummarizeColumns = "excel.columns.summarize";

        public static bool Owns(string toolId)
        {
            return string.Equals(toolId, Inspect, StringComparison.Ordinal) ||
                string.Equals(toolId, SummarizeColumns, StringComparison.Ordinal);
        }
    }
}
