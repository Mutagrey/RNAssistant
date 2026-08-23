using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public static class ChatSettingsResolver
    {
        public static AppSettings Resolve(AppSettings settings, ChatSession session)
        {
            var effective = (settings ?? new AppSettings()).Clone();
            if (session != null && !string.IsNullOrWhiteSpace(session.Model))
            {
                effective.Model = session.Model.Trim();
            }

            return effective;
        }
    }
}
