using System;

namespace RNAssistant.Office.Tools
{
    internal static class OutlookToolIds
    {
        internal const string SearchMail = "outlook.mail.search";
        internal const string IndexArchive = "outlook.archive.index";
        internal const string CreateDraft = "outlook.draft.create";
        internal const string UpdateMail = "outlook.mail.update";

        internal static bool Owns(string toolId)
        {
            return string.Equals(toolId, SearchMail, StringComparison.Ordinal) ||
                string.Equals(toolId, IndexArchive, StringComparison.Ordinal) ||
                string.Equals(toolId, CreateDraft, StringComparison.Ordinal) ||
                string.Equals(toolId, UpdateMail, StringComparison.Ordinal);
        }

        internal static bool IsRead(string toolId)
        {
            return string.Equals(toolId, SearchMail, StringComparison.Ordinal) ||
                string.Equals(toolId, IndexArchive, StringComparison.Ordinal);
        }

        internal static bool IsMutation(string toolId)
        {
            return Owns(toolId) && !IsRead(toolId);
        }
    }
}
