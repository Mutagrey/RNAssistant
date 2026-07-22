using System;
using System.Collections.Generic;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Services
{
    internal sealed class ChatHistoryEditService
    {
        private const string PendingActionCancelledReason = "Pending action cancelled because chat history changed.";

        private readonly AttachmentStore _attachmentStore;
        private readonly Action<string> _removePendingAgentTools;
        private readonly Action<ChatSession, string> _cancelPendingActivities;

        public ChatHistoryEditService(
            AttachmentStore attachmentStore,
            Action<string> removePendingAgentTools,
            Action<ChatSession, string> cancelPendingActivities)
        {
            _attachmentStore = attachmentStore ?? throw new ArgumentNullException(nameof(attachmentStore));
            _removePendingAgentTools = removePendingAgentTools ?? throw new ArgumentNullException(nameof(removePendingAgentTools));
            _cancelPendingActivities = cancelPendingActivities ?? throw new ArgumentNullException(nameof(cancelPendingActivities));
        }

        public ChatHistoryEditResult RewriteUserMessage(
            ChatSession session,
            string sessionId,
            string messageId,
            int index,
            string text)
        {
            if (session == null)
            {
                throw new InvalidOperationException("Chat session was not found.");
            }

            var trimmed = (text ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                throw new InvalidOperationException("Message text is required.");
            }

            var messages = session.Messages ?? (session.Messages = new List<ChatMessage>());
            var targetIndex = ResolveTargetIndex(messages, messageId, index);
            if (targetIndex < 0)
            {
                throw new InvalidOperationException("Message was not found.");
            }

            var target = messages[targetIndex];
            if (target == null || !string.Equals(target.Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Only user messages can be edited.");
            }

            for (var messageIndex = messages.Count - 1; messageIndex > targetIndex; messageIndex--)
            {
                _attachmentStore.DeleteMessage(messages[messageIndex]);
                messages.RemoveAt(messageIndex);
            }

            ResetMessageForReplay(target, trimmed);
            _removePendingAgentTools(sessionId);
            _cancelPendingActivities(session, PendingActionCancelledReason);
            session.PendingAgentTask = null;
            session.LastRun = null;
            session.HtmlWorkspace = new HtmlWorkspace();

            return new ChatHistoryEditResult
            {
                Message = target,
                Index = targetIndex
            };
        }

        private static int ResolveTargetIndex(IReadOnlyList<ChatMessage> messages, string messageId, int index)
        {
            if (messages == null)
            {
                return -1;
            }

            if (!string.IsNullOrWhiteSpace(messageId))
            {
                for (var messageIndex = 0; messageIndex < messages.Count; messageIndex++)
                {
                    var candidate = messages[messageIndex];
                    if (candidate != null &&
                        string.Equals(candidate.Id, messageId, StringComparison.OrdinalIgnoreCase))
                    {
                        return messageIndex;
                    }
                }
            }

            return index >= 0 && index < messages.Count ? index : -1;
        }

        private static void ResetMessageForReplay(ChatMessage message, string text)
        {
            if (message == null)
            {
                return;
            }

            message.Content = text ?? string.Empty;
            message.Activity = null;
            message.PromptTokens = null;
            message.CompletionTokens = null;
            message.TotalTokens = null;
            message.UsageJson = null;
            message.ReasoningContent = null;
            message.ReasoningTokens = null;
            message.ReasoningTruncated = false;
            message.RunId = null;
            message.Sequence = null;
        }
    }

    internal sealed class ChatHistoryEditResult
    {
        public ChatMessage Message { get; set; }
        public int Index { get; set; }
    }
}
