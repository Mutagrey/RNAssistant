using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal sealed class ChatExecutionModeSelector
    {
        public string Select(string text, ChatSession session)
        {
            if (session != null && session.HtmlModeEnabled)
            {
                return ChatModes.Agent;
            }

            if (AgentTaskContinuationResolver.ShouldContinue(text, session))
            {
                return ChatModes.Agent;
            }

            return ChatModes.Normalize(session == null ? null : session.Mode);
        }
    }
}
