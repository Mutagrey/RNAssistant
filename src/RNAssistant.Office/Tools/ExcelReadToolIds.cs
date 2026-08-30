using System;

namespace RNAssistant.Office.Tools
{
    // Public ids belong to runtime composition. Internal ids are a temporary
    // one-way host binding and are never added to the callable catalog.
    public static class ExcelReadToolIds
    {
        public const string Inspect = "excel.inspect";
        public const string ReadRange = "excel.read_range";
        public const string InspectBackend = "excel.inspect_internal";
        public const string ReadRangeBackend = "excel.read_range_internal";

        public static bool Owns(string toolId)
        {
            return string.Equals(toolId, Inspect, StringComparison.Ordinal) ||
                string.Equals(toolId, ReadRange, StringComparison.Ordinal);
        }

        public static bool IsInternal(string toolId)
        {
            return string.Equals(toolId, InspectBackend, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, ReadRangeBackend, StringComparison.OrdinalIgnoreCase);
        }
    }
}
