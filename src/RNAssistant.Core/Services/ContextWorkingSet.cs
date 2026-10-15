using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Services
{
    // A bounded request projection of the event archive, never another memory store.
    // Bodies retain their exact evidence and CAS references until compiler hydration.
    public sealed class ContextWorkingSet
    {
        public readonly List<ChatMessage> Messages = new List<ChatMessage>();
        internal readonly List<OperationCandidate> Operations = new List<OperationCandidate>();
        internal int ReceiptBudget;
        public int IncludedBodies;
        public int OmittedBodies;
        private readonly List<string> _omittedTargets = new List<string>();

        public static ContextWorkingSet Restore(ChatSession session, IReadOnlyList<ChatMessage> active,
            ModelAuthoritySnapshot authority, AppSettings settings, int budget,
            IReadOnlyList<ChatMessage> archive = null)
        {
            var result = new ContextWorkingSet();
            if (ContextCompactionRuntime.ActiveCheckpoint(session) == null) return result;
            var history = archive ?? session.Messages;
            var calls = history.Where(item => item.Role == "assistant" && item.CompletedOperation == null &&
                item.ToolCallId != null).GroupBy(item => item.ToolCallId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
            var activeIds = new HashSet<string>(active.Select(item => item.Id), StringComparer.Ordinal);
            var runId = session.LastRun?.RunId ?? history.LastOrDefault(item => !string.IsNullOrEmpty(item.RunId))?.RunId;
            var selected = new HashSet<string>(StringComparer.Ordinal);
            var laterSuccesses = new List<ChatMessage>();
            for (var index = history.Count - 1; index >= 0; index--)
            {
                var message = history[index];
                // Success can satisfy an older recovery even when its body is
                // active or excluded. Error candidates only see later successes.
                if (message.ExecutionProgress?.Outcome == ToolExecutionOutcome.Ok) laterSuccesses.Add(message);
                if (activeIds.Contains(message.Id) || !ContextCompactionRuntime.IsReplayMessage(message)) continue;
                ToolResultWireReadResult wire; string error;
                if (!ToolResultHistoryReader.TryRead(message, out wire, out error) ||
                    message.ResourceEffect == null && wire.Result.Status == ToolResultStatus.Ok) continue;
                var mandatory = wire.Result.Status == ToolResultStatus.Unknown ||
                    message.ResourceEffect?.Outcome == ResourceEffectOutcome.UnknownAfterDispatch;
                var recovery = message.ExecutionProgress?.Recovery;
                if (message.RunId == runId && recovery != null && recovery.RetryPolicy != ToolRetryPolicy.None &&
                    message.ExecutionProgress.Outcome == ToolExecutionOutcome.Error &&
                    !laterSuccesses.Any(later => recovery.IsSatisfiedBy(later.ResourceEvidence, later.ResourceEffect))) mandatory = true;
                // Keep the latest receipt for an exact call, including resumed runs.
                if (!selected.Add(wire.ToolCallId)) continue;
                ChatMessage call;
                calls.TryGetValue(wire.ToolCallId, out call);
                if (mandatory || budget > 0) result.Operations.Add(new OperationCandidate(call, message, mandatory));
            }
            result.Operations.Reverse();
            // Selection retains exact provenance. Only the compiler hydrates,
            // projects and budgets receipts; mandatory facts cannot be evicted.
            var bodies = result.SelectBodies(history, active, calls, authority.Resources, settings, Math.Max(0, budget));
            result.Messages.AddRange(bodies);
            result.ReceiptBudget = Math.Max(0, budget - EstimateCost(bodies, settings));
            if (result.IncludedBodies + result.OmittedBodies > 0)
                result.Messages.Add(new ChatMessage { Role = "user", ProtocolMessage = true,
                    Content = "WORKING_SET:\n" +
                        JsonConvert.SerializeObject(new { includedBodies = result.IncludedBodies, omittedBodies = result.OmittedBodies,
                            omittedTargets = result._omittedTargets,
                            instruction = "Omitted bodies are outside this request budget, not missing resources." }) });
            return result;
        }

        internal sealed class OperationCandidate
        {
            internal readonly ChatMessage Call;
            internal readonly ChatMessage Source;
            internal readonly bool Required;
            internal OperationCandidate(ChatMessage call, ChatMessage source, bool required)
            { Call = call; Source = source; Required = required; }
        }

        private List<ChatMessage> SelectBodies(IReadOnlyList<ChatMessage> history, IReadOnlyList<ChatMessage> active,
            IReadOnlyDictionary<string, ChatMessage> calls, ResourceAuthoritySnapshotSet resources, AppSettings settings, int budget)
        {
            var bodies = new List<ChatMessage>();
            var reducer = new EvidenceStateReducer();
            Func<ResourceEvidence, bool> current = evidence => evidence != null &&
                reducer.Reduce(evidence, resources).State == EvidenceState.Current;
            var activeIds = new HashSet<string>(active.Select(item => item.Id), StringComparer.Ordinal);
            var selected = new HashSet<string>(active.Where(ExposesBody)
                .SelectMany(item => item.ResourceEvidence ?? new List<ResourceEvidence>()).Where(current)
                .Select(ObservationKey), StringComparer.Ordinal);
            foreach (var message in history.Reverse())
            {
                if (activeIds.Contains(message.Id) || !ContextCompactionRuntime.IsReplayMessage(message) || !ExposesBody(message)) continue;
                var evidence = (message.ResourceEvidence ?? new List<ResourceEvidence>()).Where(current).ToArray();
                if (evidence.Length == 0 || evidence.Length != message.ResourceEvidence.Count) continue;
                // Media attachments are supplied to one model step only. A saved
                // success/header cannot recreate their pixels or claim delivery.
                if (evidence.Any(item => !new[] { "text", "source", "structure", "formulas", "metadata", "records", "table" }
                    .Contains(item.View, StringComparer.Ordinal))) continue;
                var missing = evidence.Where(item => !selected.Contains(ObservationKey(item))).ToArray();
                if (missing.Length == 0) continue;
                ToolResultWireReadResult wire; string error;
                ToolResultHistoryReader.TryRead(message, out wire, out error);
                var data = wire?.Data as JObject;
                ChatMessage call;
                var candidate = new List<ChatMessage>();
                // Skills/references and structured results keep the original closed
                // read frame: headers, fields, coverage, loaded state and body agree.
                if (message.ResourceEffect == null && calls.TryGetValue(message.ToolCallId ?? "", out call) &&
                    (message.ToolName == "common.capabilities_read" || (string)data?["type"] == "skill" || evidence.Any(item =>
                        item.View != ResourceRepresentations.Text && item.View != ResourceRepresentations.Source ||
                        item.Coverage.Kind != ResourceCoverageKinds.Whole)))
                {
                    candidate.Add(call); candidate.Add(message);
                }
                else
                {
                    foreach (var observation in missing.Where(IsWholeSource))
                        candidate.Add(new ChatMessage {
                            Role = "assistant", ProtocolMessage = true, SyntheticResourceObservation = true,
                            Content = JsonConvert.SerializeObject(new {
                                target = ModelContextCompiler.CurrentSourceLabel(message, observation) ?? message.ToolName,
                                view = observation.View, bodyIncluded = true, complete = true }),
                            ResultPayload = observation.Payload,
                            ResourceEvidence = new List<ResourceEvidence> { observation }
                        });
                }
                if (candidate.Count == 0) continue;
                foreach (var item in missing) selected.Add(ObservationKey(item));
                var cost = EstimateCost(candidate, settings);
                if (IncludedBodies >= 32 || cost > budget)
                {
                    OmittedBodies++;
                    var target = ModelContextCompiler.CurrentSourceLabel(message, missing[0]) ?? (string)data?["id"];
                    if (_omittedTargets.Count < 12 && target != null && !target.Contains("://")) _omittedTargets.Add(target);
                    continue;
                }
                budget -= cost; IncludedBodies++;
                bodies.InsertRange(0, candidate);
            }
            return bodies;
        }

        public static int EstimateCost(IEnumerable<ChatMessage> messages, AppSettings settings)
        {
            long cost = 0;
            foreach (var message in messages)
            {
                cost += ModelContextBudget.EstimateMessageTokens(message, settings: settings);
                foreach (var payload in new[] { message.ResultPayload, message.AcceptedCallPayload }.Where(item => item != null))
                    cost += ModelContextBudget.EstimateCharacterCountTokens((int)Math.Min(int.MaxValue, payload.ByteLength), settings);
            }
            return (int)Math.Min(int.MaxValue, cost);
        }

        private static bool ExposesBody(ChatMessage message)
        {
            if (message == null || message.ExcludeFromModelContext) return false;
            if (message.SyntheticResourceObservation) return true;
            ToolResultWireReadResult wire; string error;
            if (!ToolResultHistoryReader.TryRead(message, out wire, out error) || wire.Result.Status != ToolResultStatus.Ok) return false;
            if (message.ResourceEffect != null) return message.ResourceEffect.Outcome == ResourceEffectOutcome.VerifiedChanged ||
                message.ResourceEffect.Outcome == ResourceEffectOutcome.Restored;
            var data = wire.Data as JObject;
            if (message.ToolName == "common.resources_read")
                return (string)data?["type"] != "shared context" && (string)data?["type"] != "HTML data" &&
                    (string)data?["type"] != "HTML workspace";
            return message.ToolName == "common.capabilities_read" &&
                ((string)data?["kind"] == "skill" || (string)data?["kind"] == "reference" ||
                 message.ResourceEvidence.Any(item => {
                     var address = ResourceUri.Parse(item.Resource.Uri);
                     return address.Provider == "catalog" && address.Segments.Count > 0 &&
                         (address.Segments[0] == "skills" || address.Segments[0].StartsWith("builtin-skills-", StringComparison.Ordinal));
                 }));
        }

        private static bool IsWholeSource(ResourceEvidence evidence)
        { return evidence.Complete && evidence.Payload != null && evidence.Coverage.Kind == ResourceCoverageKinds.Whole &&
                (evidence.View == ResourceRepresentations.Source || evidence.View == ResourceRepresentations.Text); }

        private static string ObservationKey(ResourceEvidence evidence)
        { return evidence.Resource.Identity.Uri + "\n" + evidence.View + "\n" + JsonConvert.SerializeObject(evidence.Coverage); }
    }
}
