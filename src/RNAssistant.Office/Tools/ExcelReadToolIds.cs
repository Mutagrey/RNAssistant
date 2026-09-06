using System;

namespace RNAssistant.Office.Tools
{
    public static class ExcelReadToolIds
    {
        public const string Inspect = "excel.inspect";

        public static bool Owns(string toolId)
        {
            return string.Equals(toolId, Inspect, StringComparison.Ordinal);
        }
    }
}
