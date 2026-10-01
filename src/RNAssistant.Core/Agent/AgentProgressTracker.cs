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
        private readonly Dictionary<string, int> _refreshFailures = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _seenResults = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _unchanged = new Dictionary<string, int>(StringComparer.Ordinal);

        internal bool CanDispatch(ToolCall call, out string reason, out string message, out int delayMilliseconds)
        {
            reason = message = null; delayMilliseconds = 0;
            var key = Signature(call);
            if (_unknown.Contains(key))
            {
                reason = "repeated_unknown_tool_call";
                message = "The previous identical call may have had an effect; reconcile it before repeating the operation.";
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
            message = "The same call already failed without relevant progress. Correct the input or satisfy its recovery before retrying. Previous error: " + previous.Message;
            return false;
        }

        internal string Observe(ToolCall call, ToolExecutionProgress progress, string message,
            IEnumerable<ResourceEvidence> evidence, ResourceEffect effect)
        {
            if (progress == null || progress.Outcome == ToolExecutionOutcome.AwaitingConfirmation ||
                progress.Outcome == ToolExecutionOutcome.NotDispatched) return null;
            var key = Signature(call);
            if (progress.Outcome == ToolExecutionOutcome.Unknown) { _unknown.Add(key); return null; }
            if (progress.Outcome == ToolExecutionOutcome.Error)
            {
                Failure previous;
                if (!_failures.TryGetValue(key, out previous)) _failures.Add(key, previous = new Failure());
                previous.Attempts++; previous.Recovery = progress.Recovery; previous.Message = message;
                var refresh = RefreshKey(progress.Recovery);
                if (refresh != null)
                {
                    int count; _refreshFailures.TryGetValue(refresh, out count);
                    _refreshFailures[refresh] = count + 1;
                    if (count >= 1) return "Repeated source conflicts require a complete current observation before another mutation.";
                }
                return null;
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
                    var refresh = RefreshKey(recovery);
                    if (refresh != null) _refreshFailures.Remove(refresh);
                }
                _unchanged.Clear(); _seenResults.Clear();
                return null;
            }
            foreach (var pair in _failures.ToArray())
            {
                var recovery = pair.Value.Recovery;
                if (RefreshKey(recovery) == null || !observations.Any(recovery.IsSatisfiedBy)) continue;
                _failures.Remove(pair.Key); _refreshFailures.Remove(RefreshKey(recovery));
            }
            var resultFingerprint = effect?.Outcome == ResourceEffectOutcome.VerifiedNoChange
                ? "verified-no-change" : progress.ResultFingerprint;
            if (string.IsNullOrEmpty(resultFingerprint)) return null;
            var observationKey = string.Join("\n", observations.Select(e => e.Resource.Uri + "@" + e.Resource.Revision +
                ":" + e.View + ":" + JsonConvert.SerializeObject(e.Coverage)).OrderBy(x => x, StringComparer.Ordinal));
            var fingerprint = resultFingerprint + "\n" + observationKey;
            string previousResult;
            if (!_seenResults.TryGetValue(key, out previousResult) || previousResult != fingerprint)
            {
                _seenResults[key] = fingerprint;
                _unchanged.Clear(); // New information is progress, including read-only work.
                return null;
            }
            int previousCount;
            var repeats = _unchanged.TryGetValue(key, out previousCount) ? previousCount + 1 : 2;
            _unchanged[key] = repeats;
            return repeats >= 3 ? "The same operation returned the same result three times. Reuse its result or choose a different next step." : null;
        }

        internal void Restore(IEnumerable<AgentMessage> messages)
        {
            var calls = new Dictionary<string, ToolCall>(StringComparer.Ordinal);
            foreach (var message in messages)
            {
                foreach (var call in message.ToolCalls) calls[call.Id] = call;
                ToolCall completed;
                if (message.Kind == AgentMessageKind.ToolResult && calls.TryGetValue(message.ToolCallId, out completed))
                    Observe(completed, message.Progress, message.Text, message.ResourceEvidence, message.ResourceEffect);
            }
        }

        private static string Signature(ToolCall call)
        { return call.Name + "\n" + ToolPackSnapshot.JsonFingerprint(call.ArgumentsJson); }

        private static string RefreshKey(ToolRecoveryContract recovery)
        {
            return recovery?.RetryPolicy == ToolRetryPolicy.RefreshRequired && recovery.ResourceIdentity != null
                ? recovery.ResourceIdentity.Uri + "\n" + recovery.View : null;
        }
    }
}
