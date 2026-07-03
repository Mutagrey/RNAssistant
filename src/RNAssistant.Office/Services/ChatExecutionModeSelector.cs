using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal sealed class ChatExecutionModeSelector
    {
        public string Select(string text, ChatSession session, string host)
        {
            if (session != null && session.HtmlModeEnabled)
            {
                return ChatModes.Agent;
            }

            var configured = ChatModes.Normalize(session == null ? null : session.Mode);
            if (configured != ChatModes.Auto)
            {
                return configured;
            }

            var route = new OfficeIntentRouter().Route(
                text,
                new OfficeSnapshot { Host = string.IsNullOrWhiteSpace(host) ? "Office" : host },
                session);
            return route.RequiresTool ? ChatModes.Agent : ChatModes.Chat;
        }
    }
}
