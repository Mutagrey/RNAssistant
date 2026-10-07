using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public static class ToolProtocolMessages
    {
        public static bool IsExchange(ChatMessage message)
        {
            return message != null && (message.ProtocolMessage || message.Activity != null);
        }

        public static bool IsCall(ChatMessage message)
        {
            return message != null && message.ProtocolMessage && message.CompletedOperation == null &&
                string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) &&
                !IsResult(message) && Ids(message).Count > 0;
        }

        public static bool IsResult(ChatMessage message)
        {
            return message != null && message.ProtocolMessage &&
                (message.ToolResultProtocolVersion == RNAssistant.Core.ModelProtocol.ToolResultWire.CurrentVersion &&
                 !string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(message.ToolCallId) ||
                 string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase) ||
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

}
