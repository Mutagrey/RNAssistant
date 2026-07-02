using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal sealed class PromptBudgetComposer
    {
        public void AddConversationHistory(
            List<ChatMessage> messages,
            int insertIndex,
            ChatSession session,
            AppSettings settings)
        {
            if (messages == null)
            {
                return;
            }

            var budget = ModelContextBudget.InputBudgetTokens(settings);
            var used = EstimateMessages(messages);
            var history = ConversationHistory(session);
            for (var index = history.Count - 1; index >= 0; index--)
            {
                var source = history[index];
                var candidate = new ChatMessage
                {
                    Role = source.Role,
                    Content = source.Content ?? string.Empty,
                    Attachments = source.Attachments == null
                        ? new List<ChatAttachment>()
                        : new List<ChatAttachment>(source.Attachments)
                };
                var estimate = EstimateMessages(new[] { candidate });
                if (used + estimate > budget)
                {
                    break;
                }
                messages.Insert(insertIndex, candidate);
                used += estimate;
            }
        }

        public int EstimateMessages(IEnumerable<ChatMessage> messages)
        {
            return ModelContextBudget.EstimateMessagesTokens(messages) +
                (messages ?? new ChatMessage[0]).Sum(message =>
                    message == null ? 0 : EstimateExtractedAttachmentTokens(message.Attachments));
        }

        internal static List<ChatMessage> ConversationHistory(ChatSession session)
        {
            if (session == null || session.Messages == null)
            {
                return new List<ChatMessage>();
            }

            var activeUserIndex = -1;
            for (var index = session.Messages.Count - 1; index >= 0; index--)
            {
                var message = session.Messages[index];
                if (IsConversationMessage(message) &&
                    string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    activeUserIndex = index;
                    break;
                }
            }

            return session.Messages
                .Where((message, index) => index != activeUserIndex && IsConversationMessage(message))
                .ToList();
        }

        private static bool IsConversationMessage(ChatMessage message)
        {
            return message != null &&
                message.Activity == null &&
                !string.IsNullOrWhiteSpace(message.Content) &&
                (string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase));
        }

        private static int EstimateExtractedAttachmentTokens(IEnumerable<ChatAttachment> attachments)
        {
            var total = 0;
            foreach (var attachment in attachments ?? new ChatAttachment[0])
            {
                if (attachment == null)
                {
                    continue;
                }
                total += Math.Max(
                    Math.Max(0, attachment.ExtractedCharCount),
                    (attachment.ExtractedText ?? string.Empty).Length) / 2;
            }
            return total;
        }
    }
}
