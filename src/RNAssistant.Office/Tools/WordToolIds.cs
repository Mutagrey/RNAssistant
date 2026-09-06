using System;

namespace RNAssistant.Office.Tools
{
    internal static class WordToolIds
    {
        internal const string FindText = "word.find_text";
        internal const string Inspect = "word.inspect";
        internal const string WriteText = "word.write_text";
        internal const string ReplaceText = "word.replace_text";
        internal const string FormatText = "word.format_text";
        internal const string AddTable = "word.add_table";
        internal const string InsertPageBreak = "word.insert_page_break";
        internal const string AddComment = "word.add_comment";

        internal static bool Owns(string toolId)
        {
            return string.Equals(toolId, FindText, StringComparison.Ordinal) ||
                string.Equals(toolId, Inspect, StringComparison.Ordinal) ||
                string.Equals(toolId, WriteText, StringComparison.Ordinal) ||
                string.Equals(toolId, ReplaceText, StringComparison.Ordinal) ||
                string.Equals(toolId, FormatText, StringComparison.Ordinal) ||
                string.Equals(toolId, AddTable, StringComparison.Ordinal) ||
                string.Equals(toolId, InsertPageBreak, StringComparison.Ordinal) ||
                string.Equals(toolId, AddComment, StringComparison.Ordinal);
        }

        internal static bool IsRead(string toolId)
        {
            return string.Equals(toolId, FindText, StringComparison.Ordinal) ||
                string.Equals(toolId, Inspect, StringComparison.Ordinal);
        }

        internal static bool IsMutation(string toolId)
        {
            return Owns(toolId) && !IsRead(toolId);
        }
    }
}
