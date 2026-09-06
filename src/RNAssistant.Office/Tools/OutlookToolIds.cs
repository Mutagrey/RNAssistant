using System;

namespace RNAssistant.Office.Tools
{
    internal static class OutlookToolIds
    {
        internal const string SearchMail = "outlook.search_mail";
        internal const string CreateDraft = "outlook.create_draft";
        internal const string UpdateMail = "outlook.update_mail";

        internal static bool Owns(string toolId)
        {
            return string.Equals(toolId, SearchMail, StringComparison.Ordinal) ||
                string.Equals(toolId, CreateDraft, StringComparison.Ordinal) ||
                string.Equals(toolId, UpdateMail, StringComparison.Ordinal);
        }

        internal static bool IsRead(string toolId)
        {
            return string.Equals(toolId, SearchMail, StringComparison.Ordinal);
        }

        internal static bool IsMutation(string toolId)
        {
            return Owns(toolId) && !IsRead(toolId);
        }
    }
}
