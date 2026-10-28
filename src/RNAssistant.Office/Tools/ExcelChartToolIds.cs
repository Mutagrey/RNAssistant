using System;

namespace RNAssistant.Office.Tools
{
    internal static class ExcelChartToolIds
    {
        internal const string CreateChatChart = "excel.chat.chart.create";
        internal const string UpsertChart = "excel.chart.upsert";
        internal const string DeleteChart = "excel.chart.delete";

        internal static bool Owns(string toolId)
        {
            return string.Equals(toolId, CreateChatChart, StringComparison.Ordinal) ||
                string.Equals(toolId, UpsertChart, StringComparison.Ordinal) ||
                string.Equals(toolId, DeleteChart, StringComparison.Ordinal);
        }

        internal static bool IsMutation(string toolId)
        {
            return string.Equals(toolId, UpsertChart, StringComparison.Ordinal) ||
                string.Equals(toolId, DeleteChart, StringComparison.Ordinal);
        }
    }
}
