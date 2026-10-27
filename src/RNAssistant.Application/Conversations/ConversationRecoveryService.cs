using System;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Application.Conversations
{
    // The caller holds the exact conversation lease while reloading and applying
    // domain recovery. Kernel lifecycle, not a stale UI status, decides whether
    // an interrupted transition is needed. No run or tool is replayed here.
    internal static class ConversationRecoveryService
    {
        internal static bool NeedsInterruption(ChatRunRecord run)
        {
            if (run == null || string.IsNullOrWhiteSpace(run.RunId)) return false;
            var lifecycle = run.KernelState?.Summary?.Lifecycle;
            if (lifecycle.HasValue) return lifecycle.Value == RunLifecycle.Running;
            return string.IsNullOrWhiteSpace(run.Status) ||
                string.Equals(run.Status, "running", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(run.Status, "cancelling", StringComparison.OrdinalIgnoreCase);
        }

        internal static ChatSession ReconcileFenced(ChatSession observed, ChatSession current,
            Func<ChatSession, ChatSession> interrupt)
        {
            if (interrupt == null) throw new ArgumentNullException(nameof(interrupt));
            if (current == null || current.FormatVersion != ChatSession.CurrentFormatVersion) return current;
            if (observed != null &&
                (observed.Id != current.Id || observed.Host != current.Host ||
                 observed.WorkspaceId != current.WorkspaceId || observed.DocumentKey != current.DocumentKey ||
                 observed.LastRun?.RunId != current.LastRun?.RunId ||
                 ConversationEventFeed.Cursor(observed) != ConversationEventFeed.Cursor(current)))
                return current;
            return NeedsInterruption(current.LastRun) ? interrupt(current) ??
                throw new InvalidOperationException("Recovery did not return the canonical conversation.") : current;
        }
    }
}
