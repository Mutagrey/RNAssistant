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
                if (!IsExchange(message)) break;
                // A native batch may persist all calls before any result. Only
                // reusing one of our IDs starts a different exchange.
                if (IsCall(message) && Ids(message).Overlaps(remaining)) break;
                if (IsResult(message)) remaining.ExceptWith(Ids(message));
                if (remaining.Count == 0) return true;
            }
            return false;
        }

        public static IReadOnlyList<ChatMessage> ExcludeUnmatchedToolCalls(IReadOnlyList<ChatMessage> messages)
        {
            var changed = new List<ChatMessage>();
            if (messages == null) return changed;
            var pending = new Dictionary<string, ChatMessage>(StringComparer.Ordinal);
            var remaining = new Dictionary<ChatMessage, HashSet<string>>();
            var results = new Dictionary<ChatMessage, List<ChatMessage>>();
            foreach (var message in messages)
            {
                if (!IsExchange(message)) { pending.Clear(); continue; }
                if (IsCall(message))
                {
                    var ids = Ids(message);
                    remaining[message] = ids;
                    results[message] = new List<ChatMessage>();
                    // Confirmation may finish an accepted call in a new runtime
                    // invocation. Pair by exact call identity, not by run ID.
                    foreach (var id in ids) pending[id] = message;
                }
                else if (IsResult(message))
                {
                    foreach (var id in Ids(message))
                    {
                        ChatMessage call;
                        if (!pending.TryGetValue(id, out call)) continue;
                        if (!message.ExcludeFromModelContext) remaining[call].Remove(id);
                        results[call].Add(message);
                        pending.Remove(id);
                    }
                }
            }
            foreach (var pair in remaining)
            {
                if (pair.Value.Count == 0 && !pair.Key.ExcludeFromModelContext) continue;
                // A compound call is one transport unit: never leave orphan
                // results. Producers persist batch calls individually so a
                // later unfinished sibling cannot hide a completed exchange.
                foreach (var message in new[] { pair.Key }.Concat(results[pair.Key]))
                    if (!message.ExcludeFromModelContext)
                    { message.ExcludeFromModelContext = true; changed.Add(message); }
            }
            return changed;
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
