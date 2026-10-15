using System;
using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public static class ConversationInputMessage
    {
        public static bool IsUserInput(ChatMessage message)
        {
            return message != null && !message.ProtocolMessage && message.Activity == null &&
                string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase);
        }

        public static ChatMessage Latest(ChatSession session)
        {
            return session?.Messages?.LastOrDefault(IsUserInput);
        }
    }
}
