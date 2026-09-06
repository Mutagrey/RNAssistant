using System;

namespace RNAssistant.Office.Tools
{
    internal static class PowerPointToolIds
    {
        internal const string ListObjects = "powerpoint.list_objects";
        internal const string SearchText = "powerpoint.search_text";
        internal const string AddSlide = "powerpoint.add_slide";
        internal const string SetText = "powerpoint.set_text";
        internal const string ReplaceText = "powerpoint.replace_text";
        internal const string AddObject = "powerpoint.add_object";
        internal const string DuplicateSlide = "powerpoint.duplicate_slide";
        internal const string MoveSlide = "powerpoint.move_slide";

        internal static bool Owns(string toolId)
        {
            return string.Equals(toolId, ListObjects, StringComparison.Ordinal) ||
                string.Equals(toolId, SearchText, StringComparison.Ordinal) ||
                string.Equals(toolId, AddSlide, StringComparison.Ordinal) ||
                string.Equals(toolId, SetText, StringComparison.Ordinal) ||
                string.Equals(toolId, ReplaceText, StringComparison.Ordinal) ||
                string.Equals(toolId, AddObject, StringComparison.Ordinal) ||
                string.Equals(toolId, DuplicateSlide, StringComparison.Ordinal) ||
                string.Equals(toolId, MoveSlide, StringComparison.Ordinal);
        }

        internal static bool IsRead(string toolId)
        {
            return string.Equals(toolId, ListObjects, StringComparison.Ordinal) ||
                string.Equals(toolId, SearchText, StringComparison.Ordinal);
        }

        internal static bool IsMutation(string toolId)
        {
            return Owns(toolId) && !IsRead(toolId);
        }
    }
}
