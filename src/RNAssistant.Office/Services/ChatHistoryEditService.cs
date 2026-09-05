using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Office.Services
{
    internal sealed class ChatHistoryEditService
    {
        private const string PendingActionCancelledReason = "Pending action cancelled because chat history changed.";

        private readonly Action<string> _removePendingAgentTools;
        private readonly Action<ChatSession, string> _cancelPendingActivities;
        private readonly Func<ChatSession, string, bool> _loadArtifactBody;

        public ChatHistoryEditService(
            Action<string> removePendingAgentTools,
            Action<ChatSession, string> cancelPendingActivities,
            Func<ChatSession, string, bool> loadArtifactBody = null)
        {
            _removePendingAgentTools = removePendingAgentTools ?? throw new ArgumentNullException(nameof(removePendingAgentTools));
            _cancelPendingActivities = cancelPendingActivities ?? throw new ArgumentNullException(nameof(cancelPendingActivities));
            _loadArtifactBody = loadArtifactBody;
        }

        public ChatHistoryEditResult RewriteUserMessage(
            ChatSession session,
            string sessionId,
            string messageId,
            int index,
            string text)
        {
            var targetIndex = ValidateUserMessageEdit(session, messageId, index, text);
            var trimmed = text.Trim();
            var messages = session.Messages;
            var target = messages[targetIndex];

            string targetCheckpointId;
            var workspaceCheckpoint = ChatResourceUri.TryGetArtifactId(session, target.HtmlWorkspaceCheckpoint, out targetCheckpointId)
                ? targetCheckpointId
                : HtmlWorkspaceArtifactService.CheckpointAtOrBefore(session, messages, targetIndex);
            if (!string.IsNullOrWhiteSpace(workspaceCheckpoint) && _loadArtifactBody != null)
            {
                _loadArtifactBody(session, workspaceCheckpoint);
            }
            if (string.IsNullOrWhiteSpace(workspaceCheckpoint))
            {
                session.HtmlWorkspace = new HtmlWorkspace();
                session.ActiveHtmlArtifactId = null;
                HtmlWorkspaceArtifactService.RebuildNavigation(session);
            }
            else if (!HtmlWorkspaceArtifactService.Restore(session, workspaceCheckpoint))
            {
                session.HtmlWorkspace = new HtmlWorkspace();
                session.ActiveHtmlArtifactId = workspaceCheckpoint;
                HtmlWorkspaceArtifactService.RebuildNavigation(session);
            }

            var removedMessages = new List<ChatMessage>();
            for (var messageIndex = messages.Count - 1; messageIndex > targetIndex; messageIndex--)
            {
                removedMessages.Add(messages[messageIndex]);
                messages.RemoveAt(messageIndex);
            }

            ResetMessageForReplay(target, trimmed);
            _removePendingAgentTools(sessionId);
            _cancelPendingActivities(session, PendingActionCancelledReason);
            session.LastRun = null;
            session.LastContextReceipt = null;
            InvalidateContextCheckpoints(session);
            if (_loadArtifactBody != null)
                foreach (var artifact in ChatResourceReferenceService.ReachableForMessages(session.Artifacts, messages)
                    .Where(item => item.Kind == ChatArtifactKinds.TaskList)) _loadArtifactBody(session, artifact.Id);
            ChatResourceReferenceService.RestoreActiveTaskListFromMessages(session);
            ChatResourceReferenceService.RestoreActivePlanDocumentFromMessages(session);
            ChatResourceReferenceService.PruneUnreachable(session);

            return new ChatHistoryEditResult
            {
                Message = target,
                Index = targetIndex,
                RemovedMessages = removedMessages
            };
        }

        internal static int ValidateUserMessageEdit(ChatSession session, string messageId, int index, string text)
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

            var messages = session.Messages ?? new List<ChatMessage>();
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

            return targetIndex;
        }

        internal void Clear(ChatSession session, DocumentContext emptyContext)
        {
            if (session == null || emptyContext == null) throw new ArgumentNullException();
            _removePendingAgentTools(session.Id);
            session.Messages = new List<ChatMessage>();
            session.Context = emptyContext;
            session.HtmlWorkspace = new HtmlWorkspace();
            session.HtmlWorkspaceRecovery = null;
            session.Artifacts = new List<ChatArtifact>();
            InvalidateContextCheckpoints(session);
            session.ActiveHtmlArtifactId = null;
            session.ActiveTaskListArtifactId = null;
            session.ActivePlanDocumentArtifactId = null;
            session.LastRun = null;
            session.LastContextReceipt = null;
        }

        internal static List<ChatMessage> SelectMessagesForDeletion(
            IReadOnlyList<ChatMessage> messages,
            int targetIndex)
        {
            if (messages == null || targetIndex < 0 || targetIndex >= messages.Count)
            {
                return new List<ChatMessage>();
            }

            var targetMessage = messages[targetIndex];
            var toolCallIds = ToolProtocolMessages.Ids(targetMessage);
            if (toolCallIds.Count == 0) return new List<ChatMessage> { targetMessage };

            var first = FindExchangeStart(messages, targetIndex, toolCallIds);
            var callIds = ToolProtocolMessages.Ids(messages[first]);
            if (ToolProtocolMessages.IsCall(messages[first]) && callIds.Count > 0)
            {
                toolCallIds = callIds;
            }
            var last = first;
            while (last + 1 < messages.Count &&
                ToolProtocolMessages.IsExchange(messages[last + 1]) &&
                !ToolProtocolMessages.IsCall(messages[last + 1]))
            {
                last += 1;
            }

            var result = new List<ChatMessage>();
            for (var index = first; index <= last; index++)
            {
                var message = messages[index];
                if (ReferenceEquals(message, targetMessage) || ToolProtocolMessages.Uses(message, toolCallIds))
                {
                    result.Add(message);
                }
            }
            return result;
        }

        internal static void ExcludeUnmatchedToolCalls(IReadOnlyList<ChatMessage> messages)
        {
            if (messages == null) return;
            for (var index = 0; index < messages.Count; index++)
            {
                var call = messages[index];
                var ids = ToolProtocolMessages.Ids(call);
                if (!ToolProtocolMessages.IsCall(call) ||
                    ToolProtocolMessages.HasAllResults(messages, index, ids, messages.Count))
                {
                    continue;
                }

                call.ExcludeFromModelContext = true;
                for (var resultIndex = index + 1; resultIndex < messages.Count; resultIndex++)
                {
                    var message = messages[resultIndex];
                    if (!ToolProtocolMessages.IsExchange(message) || ToolProtocolMessages.IsCall(message)) break;
                    if (ToolProtocolMessages.Uses(message, ids)) message.ExcludeFromModelContext = true;
                }
            }
        }

        internal static bool HasResultForLatestToolCall(
            IReadOnlyList<ChatMessage> messages,
            string toolCallId)
        {
            if (messages == null || string.IsNullOrWhiteSpace(toolCallId)) return false;
            for (var index = messages.Count - 1; index >= 0; index--)
            {
                var call = messages[index];
                var ids = ToolProtocolMessages.Ids(call);
                if (ToolProtocolMessages.IsCall(call) && ids.Contains(toolCallId))
                {
                    return ToolProtocolMessages.HasAllResults(
                        messages,
                        index,
                        new HashSet<string>(new[] { toolCallId }, StringComparer.Ordinal),
                        messages.Count);
                }
            }
            return false;
        }

        private static int FindExchangeStart(
            IReadOnlyList<ChatMessage> messages,
            int targetIndex,
            ISet<string> toolCallIds)
        {
            if (ToolProtocolMessages.IsCall(messages[targetIndex])) return targetIndex;
            for (var index = targetIndex - 1; index >= 0 && ToolProtocolMessages.IsExchange(messages[index]); index--)
            {
                if (!ToolProtocolMessages.IsCall(messages[index])) continue;
                return ToolProtocolMessages.Uses(messages[index], toolCallIds) ? index : targetIndex;
            }
            return targetIndex;
        }

        private static void InvalidateContextCheckpoints(ChatSession session)
        {
            if (session == null) return;
            session.ContextCheckpoints = new List<ContextCheckpoint>();
            session.ActiveContextCheckpointId = null;
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

                return -1;
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
            message.AttachmentAnalysis = null;
            message.ExcludeFromModelContext = false;
            message.ResponseProtocolVersion = 0;
            message.ResponseStatus = null;
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

    internal static class ToolProtocolMessages
    {
        public static bool IsExchange(ChatMessage message)
        {
            return message != null && (message.ProtocolMessage || message.Activity != null);
        }

        public static bool IsCall(ChatMessage message)
        {
            return message != null && message.ProtocolMessage &&
                string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) &&
                !IsResult(message) && Ids(message).Count > 0;
        }

        public static bool IsResult(ChatMessage message)
        {
            return message != null && message.ProtocolMessage &&
                (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase) ||
                 (message.Content ?? string.Empty).StartsWith("TOOL_RESULT:", StringComparison.Ordinal));
        }

        public static HashSet<string> Ids(ChatMessage message)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            AddIds(message, result);
            return result;
        }

        public static bool Uses(ChatMessage message, ISet<string> ids)
        {
            return IsExchange(message) && ids != null && Ids(message).Any(ids.Contains);
        }

        public static bool HasAllResults(
            IReadOnlyList<ChatMessage> messages,
            int callIndex,
            ISet<string> callIds,
            int endExclusive)
        {
            if (messages == null || callIds == null || callIds.Count == 0) return false;
            var remaining = new HashSet<string>(callIds, StringComparer.Ordinal);
            var end = Math.Min(messages.Count, Math.Max(0, endExclusive));
            for (var index = callIndex + 1; index < end; index++)
            {
                var message = messages[index];
                if (!IsExchange(message) || IsCall(message)) break;
                if (IsResult(message)) remaining.ExceptWith(Ids(message));
                if (remaining.Count == 0) return true;
            }
            return false;
        }

        public static int PreserveCompletePrefix(IReadOnlyList<ChatMessage> messages, int prefixCount)
        {
            var safeCount = Math.Max(0, Math.Min(prefixCount, messages == null ? 0 : messages.Count));
            for (var index = 0; index < safeCount; index++)
            {
                var call = messages[index];
                if (IsCall(call) && !HasAllResults(messages, index, Ids(call), safeCount)) return index;
            }
            return safeCount;
        }

        private static void AddIds(ChatMessage message, ISet<string> target)
        {
            if (message == null || target == null) return;
            if (!string.IsNullOrWhiteSpace(message.ToolCallId)) target.Add(message.ToolCallId);
            foreach (var call in message.ToolCalls ?? new List<RNAssistant.Core.Llm.LlmToolCall>())
            {
                if (call != null && !string.IsNullOrWhiteSpace(call.Id)) target.Add(call.Id);
            }
            AddIds(message.Activity, target);
        }

        private static void AddIds(ChatActivity activity, ISet<string> target)
        {
            if (activity == null || target == null) return;
            if (!string.IsNullOrWhiteSpace(activity.ToolCallId)) target.Add(activity.ToolCallId);
            foreach (var child in activity.Children ?? new List<ChatActivity>()) AddIds(child, target);
        }
    }

    internal sealed class ChatHistoryEditResult
    {
        public ChatMessage Message { get; set; }
        public int Index { get; set; }
        public IReadOnlyList<ChatMessage> RemovedMessages { get; set; }
    }
}
