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
        public void AddConversationHistory(
            List<ChatMessage> messages,
            int insertIndex,
            ChatSession session,
            AppSettings settings,
            int inputBudgetTokens = 0,
            bool includeProtocolMessages = true,
            bool excludeLatestUser = true)
        {
            if (messages == null)
            {
                return;
            }

            var budget = inputBudgetTokens > 0 ? inputBudgetTokens : ModelContextBudget.InputBudgetTokens(settings);
            var used = EstimateMessages(messages, settings);
            if (used > budget)
            {
                throw new PromptBudgetExceededException(
                    "The current request and required runtime context exceed the model input budget. No conversation history was removed.",
                    false);
            }
            var history = ConversationHistory(session, includeProtocolMessages, excludeLatestUser);
            var available = Math.Max(0, budget - used);
            if (history.Count == 0)
            {
                return;
            }
            if (available <= 0)
            {
                throw new PromptBudgetExceededException(
                    "Active conversation context exceeds the request budget. Run context compaction before this model turn.",
                    true);
            }

            var candidates = history
                .Select(message => HistoricalContextProjector.Project(
                    message,
                    artifactId => ChatArtifactResourceProvider.ResolveRevisionUri(session, artifactId)))
                .ToList();
            var required = EstimateMessages(candidates, settings);
            if (required > available)
            {
                throw new PromptBudgetExceededException(
                    "Active conversation context exceeds the request budget. Run context compaction before this model turn.",
                    true);
            }
            for (var index = 0; index < candidates.Count; index++)
            {
                messages.Insert(insertIndex, candidates[index]);
                insertIndex += 1;
            }
        }

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
