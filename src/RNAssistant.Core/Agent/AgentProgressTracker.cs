using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Agent
{
    // Request-local projection of accepted terminal facts. No tool names, domain
    // arguments or result JSON shapes are interpreted by the kernel.
    internal sealed class AgentProgressTracker
    {
        private sealed class Failure
        {
            internal int Attempts;
            internal ToolRecoveryContract Recovery;
            internal string Message;
        }
        private readonly HashSet<string> _unknown = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Failure> _failures = new Dictionary<string, Failure>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _seenResults = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _unchanged = new Dictionary<string, int>(StringComparer.Ordinal);
        private int _rejectedSteps;
        private bool _stepRejected;
        private bool _stepDispatched;

        // Count model responses, not calls in a read batch. A rejected call must
        // reach the model before it can be judged to have ignored that feedback.
        internal void BeginStep()
        {
            _rejectedSteps = _stepRejected && !_stepDispatched ? _rejectedSteps + 1 : 0;
            _stepRejected = _stepDispatched = false;
        }

        internal bool RecoveryIgnored => _stepRejected && !_stepDispatched && _rejectedSteps >= 2;

        internal bool CanDispatch(ToolCall call, out string reason, out string message, out int delayMilliseconds)
        {
            reason = message = null; delayMilliseconds = 0;
            var key = Signature(call);
            if (_unknown.Contains(key))
            {
                reason = "repeated_unknown_tool_call";
                message = "This call was not dispatched: the previous identical call may have had an effect. Inspect its actual state or choose another operation; do not repeat the uncertain effect.";
                return false;
            }
            int unchanged;
            if (_unchanged.TryGetValue(key, out unchanged) && unchanged >= 3)
            {
                reason = "repeated_tool_no_progress";
                message = "This call was not dispatched: it already returned the same result three times. Reuse that result, choose another tool or change the input to obtain new information.";
                return false;
            }
            Failure previous;
            if (!_failures.TryGetValue(key, out previous)) return true;
            if (previous.Recovery?.RetryPolicy == ToolRetryPolicy.RetryLater && previous.Attempts < 3)
            {
                delayMilliseconds = 250 * previous.Attempts;
                return true;
            }
            reason = "repeated_tool_failure";
            message = "This call was not dispatched: the same call already failed without relevant progress. Choose another tool, correct the input or satisfy its recovery before retrying. Previous error: " + previous.Message;
            return false;
        }

        internal void Observe(ToolCall call, ToolExecutionProgress progress, string message,
            IEnumerable<ResourceEvidence> evidence, ResourceEffect effect)
        {
            if (progress == null || progress.Outcome == ToolExecutionOutcome.AwaitingConfirmation) return;
            if (progress.Outcome == ToolExecutionOutcome.NotDispatched) { _stepRejected = true; return; }
            _stepDispatched = true;
            var key = Signature(call);
            if (progress.Outcome == ToolExecutionOutcome.Unknown) { _unknown.Add(key); return; }
            if (progress.Outcome == ToolExecutionOutcome.Error)
            {
                Failure previous;
                if (!_failures.TryGetValue(key, out previous)) _failures.Add(key, previous = new Failure());
                previous.Attempts++; previous.Recovery = progress.Recovery; previous.Message = message;
                return;
            }
            var observations = (evidence ?? new ResourceEvidence[0]).Where(e => e != null).ToArray();
            // A verified state transition can make a previously rejected call valid.
            if (effect != null && (effect.Outcome == ResourceEffectOutcome.VerifiedChanged ||
                effect.Outcome == ResourceEffectOutcome.Restored || effect.Outcome == ResourceEffectOutcome.ExternalDriftObserved))
            {
                foreach (var failure in _failures.ToArray())
                {
                    var recovery = failure.Value.Recovery;
                    if (recovery?.ResourceIdentity != null && !effect.Impacts.Any(impact =>
                        impact.Identity.Equals(recovery.ResourceIdentity))) continue;
                    _failures.Remove(failure.Key);
                }
                _unchanged.Clear(); _seenResults.Clear();
                return;
            }
            foreach (var pair in _failures.ToArray())
            {
                var recovery = pair.Value.Recovery;
                if (recovery?.RetryPolicy != ToolRetryPolicy.RefreshRequired || !observations.Any(recovery.IsSatisfiedBy)) continue;
                _failures.Remove(pair.Key);
            }
            var resultFingerprint = effect?.Outcome == ResourceEffectOutcome.VerifiedNoChange
                ? "verified-no-change" : progress.ResultFingerprint;
            if (string.IsNullOrEmpty(resultFingerprint)) return;
            var observationKey = string.Join("\n", observations.Select(e => e.Resource.Uri + "@" + e.Resource.Revision +
                ":" + e.View + ":" + JsonConvert.SerializeObject(e.Coverage)).OrderBy(x => x, StringComparer.Ordinal));
            var fingerprint = resultFingerprint + "\n" + observationKey;
            string previousResult;
            if (!_seenResults.TryGetValue(key, out previousResult) || previousResult != fingerprint)
            {
                _seenResults[key] = fingerprint;
                _unchanged.Clear(); // New information is progress, including read-only work.
                return;
            }
            int previousCount;
            var repeats = _unchanged.TryGetValue(key, out previousCount) ? previousCount + 1 : 2;
            _unchanged[key] = repeats;
        }

        internal void Restore(IEnumerable<AgentMessage> messages)
        {
            var calls = new Dictionary<string, ToolCall>(StringComparer.Ordinal);
            foreach (var message in messages)
            {
                if (message.Kind == AgentMessageKind.Assistant) BeginStep();
                foreach (var call in message.ToolCalls) calls[call.Id] = call;
                ToolCall completed;
                if (message.Kind == AgentMessageKind.ToolResult && calls.TryGetValue(message.ToolCallId, out completed))
                    Observe(completed, message.Progress, message.Text, message.ResourceEvidence, message.ResourceEffect);
            }
        }

        private static string Signature(ToolCall call)
        { return call.Name + "\n" + ToolPackSnapshot.JsonFingerprint(call.ArgumentsJson); }

    }
}
