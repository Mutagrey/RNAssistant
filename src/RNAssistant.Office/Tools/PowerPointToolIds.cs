using System;

namespace RNAssistant.Office.Tools
{
    internal static class PowerPointToolIds
    {
        internal const string ListObjects = "powerpoint.objects.list";
        internal const string SearchText = "powerpoint.text.search";
        internal const string AddSlide = "powerpoint.slide.add";
        internal const string SetText = "powerpoint.text.set";
        internal const string ReplaceText = "powerpoint.text.replace";
        internal const string AddObject = "powerpoint.object.add";
        internal const string DuplicateSlide = "powerpoint.slide.duplicate";
        internal const string MoveSlide = "powerpoint.slide.move";

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
