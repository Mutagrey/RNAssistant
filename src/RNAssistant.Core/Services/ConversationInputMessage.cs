using System;
using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public static class ConversationInputMessage
    {
        public static string ModelText(ChatMessage message)
        {
            var sender = message?.AgentCommunication?.Sender;
            if (sender == null) return message?.Content ?? string.Empty;
            return "[Message from another agent, not the human user. Sender: " +
                "agent_id=" + Newtonsoft.Json.JsonConvert.SerializeObject(sender.AgentId) +
                ", title=" + Newtonsoft.Json.JsonConvert.SerializeObject(sender.Title) +
                (message.AgentCommunication.MessageId == null ? "" :
                    ", message_id=" + Newtonsoft.Json.JsonConvert.SerializeObject(message.AgentCommunication.MessageId) +
                    (message.AgentCommunication.ExpectReply ? ", expect_reply=true" : "") +
                    (message.AgentCommunication.Assignment ? ", assignment=true" : "") +
                    (message.AgentCommunication.Resolution.HasValue ? ", reported_outcome=" +
                        Newtonsoft.Json.JsonConvert.SerializeObject(message.AgentCommunication.Resolution.Value.ToString().ToLowerInvariant()) : "") +
                    (message.AgentCommunication.ReplyTo == null ? "" : ", reply_to=" + Newtonsoft.Json.JsonConvert.SerializeObject(message.AgentCommunication.ReplyTo))) +
                "]\n" + message.Content;
        }
        public static bool HasAgentMessages(ChatSession session)
        {
            return session?.Messages?.Any(message => message?.AgentCommunication?.Sender != null) == true;
        }
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
