using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.ModelProtocol;
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

            var partialPatches = new HashSet<string>((messages ?? Enumerable.Empty<ChatMessage>()).Where(m =>
                m?.RunId == summary.RunId && m.ToolName == ResourceMutationToolCatalog.Patch &&
                !string.IsNullOrWhiteSpace(m.ToolCallId) && m.ResourceEffect?.Targets != null &&
                m.ResourceEffect.Targets.Any(t => t.Status == ResourceTargetStatus.Changed) &&
                m.ResourceEffect.Targets.Any(t => t.Status == ResourceTargetStatus.Rejected ||
                    t.Status == ResourceTargetStatus.Unknown || t.Status == ResourceTargetStatus.NotStarted))
                .Select(m => m.ToolCallId), StringComparer.Ordinal);
            var effects = CollectEffects(messages, summary.RunId, summary.TurnId, partialPatches);
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
                summary.PendingConfirmation.Call.Name, summary.PendingConfirmation.PatchPreview);
            var finish = FinishResponse(messages, summary);

            return new RunViewState(
                summary.RunId,
                summary.TurnId,
                summary.AssistantMessage,
                lifecycle,
                health,
                counts.ReadOk,
                SaturatingAdd(verified, partialPatches.Count),
                noChange,
                unverified,
                failed,
                unknown,
                pending,
                summary.Reason,
                string.IsNullOrWhiteSpace(run.CurrentAction) ? summary.AssistantMessage : run.CurrentAction,
                run.StartedUtc,
                modelResults: finish?.Results.Select(claim => new ModelResultViewState(
                    claim.Criterion, claim.AssessmentName, claim.Evidence)),
                modelOutcome: finish?.Outcome.HasValue == true ? ConversationFinishContract.Name(finish.Outcome.Value) : null);
        }

        public static RunViewState StampCurrentRun(ChatSession session, bool includeProtocolResponses = false)
        {
            var state = Create(session);
            if (state == null || session.Messages == null) return state;
            var target = session.Messages.LastOrDefault(message => message != null &&
                (!message.ProtocolMessage || includeProtocolResponses &&
                    message.ResponseProtocolVersion == ConversationResponse.ProtocolVersion) &&
                string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(message.RunId, state.RunId, StringComparison.Ordinal));
            if (target != null) target.RunViewState = state;
            return state;
        }

        public static ChatActivity TerminalDiagnostic(RunSummary summary)
        {
            if (summary == null || summary.Lifecycle == RunLifecycle.Running ||
                summary.Lifecycle == RunLifecycle.AwaitingConfirmation ||
                summary.Lifecycle == RunLifecycle.Completed ||
                summary.Reason == "provider_refused") return null;
            return new ChatActivity
            {
                RunId = summary.RunId, Kind = "diagnostic", Title = "Выполнение остановлено",
                Status = summary.Lifecycle.ToString().ToLowerInvariant(),
                ExecutionStatus = summary.Reason == "ProtocolExhausted" ? "invalid_model_response"
                    : summary.Reason == "PromptBudgetExceeded" ? "prompt_budget_exceeded"
                    : summary.Reason == "iteration_limit" || summary.Reason == "tool_step_limit"
                        ? "step_limit_reached" : summary.Reason,
                ResultMessage = summary.AssistantMessage
            };
        }

        private static string Lifecycle(RunSummary summary)
        {
            if (summary.Lifecycle == RunLifecycle.AwaitingConfirmation) return RunViewLifecycles.AwaitingConfirmation;
            if (summary.Lifecycle == RunLifecycle.Completed &&
                summary.Reason == "awaiting_user") return RunViewLifecycles.AwaitingUser;
            if (summary.Lifecycle == RunLifecycle.Completed) return RunViewLifecycles.Completed;
            if (summary.Lifecycle == RunLifecycle.Cancelled) return RunViewLifecycles.Cancelled;
            if (summary.Lifecycle == RunLifecycle.Failed) return RunViewLifecycles.Failed;
            return RunViewLifecycles.Running;
        }

        private static AgentResponse FinishResponse(
            IEnumerable<ChatMessage> messages, RunSummary summary)
        {
            if (summary.Lifecycle != RunLifecycle.Completed || summary.Reason != "model_finish")
                return null;
            var message = (messages ?? Enumerable.Empty<ChatMessage>()).LastOrDefault(item =>
                item != null && item.Role == "assistant" && item.ProtocolMessage &&
                string.Equals(item.RunId, summary.RunId, StringComparison.Ordinal));
            if (message == null || message.ResponseProtocolVersion != ConversationResponse.ProtocolVersion)
                return null;
            var accepted = ConversationResponseHistoryReader.Read(message);
            return !accepted.Success || accepted.Response.Action != ConversationResponse.FinishAction
                ? null : accepted.Response;
        }

        private static IReadOnlyList<ToolEffectEvidence> CollectEffects(
            IEnumerable<ChatMessage> messages, string runId, string turnId, ISet<string> partialPatches)
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
            // Known partial changes accompany a failed/unknown call, not WriteOk.
            // Preserve them separately without inventing an inconsistent-effect warning.
            return byCall.Where(item => !partialPatches.Contains(item.Key) || item.Value != ToolEffectEvidence.VerifiedChange)
                .Select(item => item.Value).ToArray();
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
