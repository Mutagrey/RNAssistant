using System;

namespace RNAssistant.Office.Tools
{
    public static class ExcelWriteToolIds
    {
        public const string WriteRange = "excel.write_range";
        public const string ReadBackend = "excel.write_range_read_internal";
        public const string ApplyBackend = "excel.write_range_apply_internal";

        public static bool Owns(string toolId)
        {
            return string.Equals(toolId, WriteRange, StringComparison.Ordinal);
        }

        public static bool IsInternal(string toolId)
        {
            return string.Equals(toolId, ReadBackend, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, ApplyBackend, StringComparison.OrdinalIgnoreCase);
        }
    }
}
