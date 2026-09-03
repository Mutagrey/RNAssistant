using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Services
{
    public static class RunViewStateProjector
    {
        public static RunViewState Create(ChatSession session)
        {
            return session == null ? null : Create(session.LastRun, session.Messages);
        }

        public static RunViewState Create(ChatRunRecord run, IEnumerable<ChatMessage> messages)
        {
            var summary = run == null || run.KernelState == null ? null : run.KernelState.Summary;
            if (summary == null) return null;

            var effects = CollectEffects(messages, summary.RunId, summary.TurnId);
            var counts = summary.ToolCounts;
            var reportedVerified = effects.Count(value => value == ToolEffectEvidence.VerifiedChange);
            var reportedNoChange = effects.Count(value => value == ToolEffectEvidence.VerifiedNoChange);
            var verified = Math.Min(reportedVerified, counts.WriteOk);
            var noChange = Math.Min(reportedNoChange, Math.Max(0, counts.WriteOk - verified));
            var accountableNoChange = Math.Max(0, counts.WriteOk - verified) +
                (long)counts.WriteError;
            var inconsistentEffects = reportedVerified > counts.WriteOk ||
                reportedNoChange > accountableNoChange;
            var unverified = Math.Max(0, counts.WriteOk - verified - noChange);
            var evidenceUnknown = effects.Count(value => value == ToolEffectEvidence.Unknown);
            var unknown = SaturatingAdd(Math.Max(counts.WriteUnknown, evidenceUnknown), unverified);
            unknown = SaturatingAdd(unknown, inconsistentEffects ? 1 : 0);
            var failed = counts.ReadError > int.MaxValue - counts.WriteError
                ? int.MaxValue : counts.ReadError + counts.WriteError;
            var health = unknown > 0 ? RunViewHealth.Unknown : failed > 0 ? RunViewHealth.Errors : RunViewHealth.Clean;
            var lifecycle = Lifecycle(summary);
            var pending = lifecycle != RunViewLifecycles.AwaitingConfirmation ? null : new PendingConfirmationViewState(
                summary.PendingConfirmation.PendingId,
                summary.PendingConfirmation.Call.Id,
                summary.PendingConfirmation.Call.Name);

            return new RunViewState(
                summary.RunId,
                summary.TurnId,
                summary.AssistantMessage,
                lifecycle,
                health,
                counts.ReadOk,
                verified,
                noChange,
                unverified,
                failed,
                unknown,
                pending,
                summary.Reason,
                string.IsNullOrWhiteSpace(run.CurrentAction) ? summary.AssistantMessage : run.CurrentAction,
                run.StartedUtc);
        }

        public static RunViewState StampCurrentRun(ChatSession session)
        {
            var state = Create(session);
            if (state == null || session.Messages == null) return state;
            var target = session.Messages.LastOrDefault(message => message != null && !message.ProtocolMessage &&
                string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(message.RunId, state.RunId, StringComparison.Ordinal));
            if (target != null) target.RunViewState = state;
            return state;
        }

        private static string Lifecycle(RunSummary summary)
        {
            if (summary.Lifecycle == RunLifecycle.AwaitingConfirmation) return RunViewLifecycles.AwaitingConfirmation;
            if (summary.Lifecycle == RunLifecycle.Completed && summary.Reason == "awaiting_user") return RunViewLifecycles.AwaitingUser;
            if (summary.Lifecycle == RunLifecycle.Completed) return RunViewLifecycles.Completed;
            if (summary.Lifecycle == RunLifecycle.Cancelled) return RunViewLifecycles.Cancelled;
            if (summary.Lifecycle == RunLifecycle.Failed) return RunViewLifecycles.Failed;
            return RunViewLifecycles.Running;
        }

        private static IReadOnlyList<ToolEffectEvidence> CollectEffects(
            IEnumerable<ChatMessage> messages, string runId, string turnId)
        {
            var byCall = new Dictionary<string, ToolEffectEvidence>(StringComparer.Ordinal);
            foreach (var message in messages ?? Enumerable.Empty<ChatMessage>())
            {
                if (message == null || message.Activity == null) continue;
                var sameRun = string.Equals(message.RunId, runId, StringComparison.Ordinal) ||
                    string.Equals(message.Activity.RunId, runId, StringComparison.Ordinal);
                var sameTurn = message.RunViewState != null &&
                    string.Equals(message.RunViewState.TurnId, turnId, StringComparison.Ordinal);
                if (!sameRun && !sameTurn) continue;
                CollectEffects(message.Activity, byCall);
            }
            return byCall.Values.ToArray();
        }

        private static void CollectEffects(ChatActivity activity, IDictionary<string, ToolEffectEvidence> byCall)
        {
            if (activity == null) return;
            if (activity.ExecutionEvidence != null && !string.IsNullOrWhiteSpace(activity.ToolCallId))
                byCall[activity.ToolCallId] = activity.ExecutionEvidence.Effect;
            foreach (var child in activity.Children ?? new List<ChatActivity>()) CollectEffects(child, byCall);
        }

        private static int SaturatingAdd(int first, int second)
        {
            return first > int.MaxValue - second ? int.MaxValue : first + second;
        }
    }
}
