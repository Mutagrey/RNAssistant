using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Services
{
    // Detached archive candidates only. The compiler owns currency, deduplication,
    // bounded materialization and request budgeting for active and archived bodies.
    public sealed class ContextWorkingSet
    {
        internal readonly List<Candidate> Operations = new List<Candidate>();
        internal readonly List<Candidate> Bodies = new List<Candidate>();
        internal int Budget;

        public static ContextWorkingSet Restore(ChatSession session, IReadOnlyList<ChatMessage> active,
            int budget, IReadOnlyList<ChatMessage> archive = null)
        {
            var result = new ContextWorkingSet { Budget = Math.Max(0, budget) };
            if (ContextCompactionRuntime.ActiveCheckpoint(session) == null) return result;
            var history = archive ?? session.Messages;
            var calls = history.Where(item => item.Role == "assistant" && item.CompletedOperation == null &&
                item.ToolCallId != null).GroupBy(item => item.ToolCallId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
            var activeIds = new HashSet<string>(active.Select(item => item.Id), StringComparer.Ordinal);
            var runId = session.LastRun?.RunId ?? history.LastOrDefault(item => !string.IsNullOrEmpty(item.RunId))?.RunId;
            var latestTerminal = history.LastOrDefault(message => message.RunId == runId && message.ToolCallReference != null &&
                message.Activity == null && message.AcceptedCallOrigin == null && message.ToolResultProtocolVersion != 0);
            var selected = new HashSet<string>(StringComparer.Ordinal);
            var laterSuccesses = new List<ChatMessage>();
            for (var index = history.Count - 1; index >= 0; index--)
            {
                var message = history[index];
                // Success can satisfy an older recovery even when its body is
                // active or excluded. Error candidates only see later successes.
                if (message.ExecutionProgress?.Outcome == ToolExecutionOutcome.Ok) laterSuccesses.Add(message);
                if (activeIds.Contains(message.Id) || !ContextCompactionRuntime.IsReplayMessage(message)) continue;
                ChatMessage call;
                calls.TryGetValue(message.ToolCallId ?? "", out call);
                if (!message.ExcludeFromModelContext && (message.ResourceEvidence?.Count ?? 0) > 0)
                    result.Bodies.Add(new Candidate(call, message, false));
                ToolResultWireReadResult wire; string error;
                if (!ToolResultHistoryReader.TryRead(message, out wire, out error) ||
                    message.ResourceEffect == null && wire.Result.Status == ToolResultStatus.Ok && message != latestTerminal) continue;
                var mandatory = message == latestTerminal || wire.Result.Status == ToolResultStatus.Unknown ||
                    message.ResourceEffect?.Outcome == ResourceEffectOutcome.UnknownAfterDispatch;
                var recovery = message.ExecutionProgress?.Recovery;
                if (message.RunId == runId && recovery != null && recovery.RetryPolicy != ToolRetryPolicy.None &&
                    message.ExecutionProgress.Outcome == ToolExecutionOutcome.Error &&
                    !laterSuccesses.Any(later => recovery.IsSatisfiedBy(later.ResourceEvidence, later.ResourceEffect))) mandatory = true;
                // Keep the latest receipt for an exact call, including resumed runs.
                if (!selected.Add(wire.ToolCallId)) continue;
                calls.TryGetValue(wire.ToolCallId, out call);
                if (mandatory || budget > 0) result.Operations.Add(new Candidate(call, message, mandatory));
            }
            result.Operations.Reverse();
            result.Bodies.Reverse();
            return result;
        }

        internal sealed class Candidate
        {
            internal readonly ChatMessage Call;
            internal readonly ChatMessage Source;
            internal readonly bool Required;
            internal Candidate(ChatMessage call, ChatMessage source, bool required)
            { Call = call; Source = source; Required = required; }
        }
    }
}
