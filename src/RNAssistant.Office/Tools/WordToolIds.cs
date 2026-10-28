using System;

namespace RNAssistant.Office.Tools
{
    internal static class WordToolIds
    {
        internal const string FindText = "word.text.find";
        internal const string Inspect = "word.inspect";
        internal const string WriteText = "word.text.write";
        internal const string ReplaceText = "word.text.replace";
        internal const string FormatText = "word.text.format";
        internal const string AddTable = "word.table.add";
        internal const string InsertPageBreak = "word.page.break.insert";
        internal const string AddComment = "word.comment.add";

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
