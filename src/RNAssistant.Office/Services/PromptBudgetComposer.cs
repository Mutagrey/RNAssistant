using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal sealed class PromptBudgetExceededException : InvalidOperationException
    {
        public bool CanCompact { get; private set; }

        public PromptBudgetExceededException(string message, bool canCompact)
            : base(message)
        {
            CanCompact = canCompact;
        }
    }

    internal sealed class PromptBudgetComposer
    {

        public int EstimateMessages(IEnumerable<ChatMessage> messages, AppSettings settings = null)
        {
            return ModelContextBudget.EstimateMessagesTokens(messages, settings);
        }

        internal static List<ChatMessage> ConversationHistory(
            ChatSession session,
            bool includeProtocolMessages = true,
            bool excludeLatestUser = true)
        {
            if (session == null || session.Messages == null)
            {
                return new List<ChatMessage>();
            }

            var history = ContextCompactionService.BuildActiveWindow(session);
            var activeUserIndex = -1;
            for (var index = history.Count - 1; excludeLatestUser && index >= 0; index--)
            {
                var message = history[index];
                if (!message.ProtocolMessage && IsConversationMessage(message, includeProtocolMessages) &&
                    string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    activeUserIndex = index;
                    break;
                }
            }

            return history
                .Where((message, index) => index != activeUserIndex && IsConversationMessage(message, includeProtocolMessages))
                .ToList();
        }

        private static bool IsConversationMessage(ChatMessage message, bool includeProtocolMessages)
        {
            return (ContextCompactionService.IsReplayMessage(message) && (includeProtocolMessages || !message.ProtocolMessage)) ||
                (message != null &&
                 !message.ExcludeFromModelContext &&
                 message.Activity == null &&
                 !string.IsNullOrWhiteSpace(message.Content) &&
                 string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) &&
                 (message.Content ?? string.Empty).StartsWith("COMPACTED_EARLIER_CONTEXT", StringComparison.Ordinal));
        }

    }
}
