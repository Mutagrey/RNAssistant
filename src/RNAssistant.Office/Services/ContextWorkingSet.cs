using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    // A bounded request projection of the event archive, never another memory store.
    // Bodies retain their exact evidence and CAS references until compiler hydration.
    internal sealed class ContextWorkingSet
    {
        internal readonly List<ChatMessage> Messages = new List<ChatMessage>();
        internal int IncludedBodies;
        internal int OmittedBodies;
        private readonly List<string> _omittedTargets = new List<string>();

        internal static ContextWorkingSet Restore(ChatSession session, IReadOnlyList<ChatMessage> active,
            ModelAuthoritySnapshot authority, AppSettings settings, int budget)
        {
            var result = new ContextWorkingSet();
            if (ContextCompactionService.ActiveCheckpoint(session) == null) return result;
            // Reserve space for exact execution receipts even when large bodies are evicted.
            var receiptBudget = Math.Min(2048, budget / 4);
            result.Messages.AddRange(result.SelectBodies(session, active, authority.Resources, settings, budget - receiptBudget));
            var activeIds = new HashSet<string>(active.Select(item => item.Id), StringComparer.Ordinal);
            var receipts = new List<ChatMessage>();
            foreach (var message in session.Messages.AsEnumerable().Reverse())
            {
                if (activeIds.Contains(message.Id) || !ContextCompactionService.IsReplayMessage(message)) continue;
                ToolResultWireReadResult wire; string error;
                if (!ToolResultHistoryReader.TryRead(message, out wire, out error) ||
                    message.ResourceEffect == null && wire.Result.Status == ToolResultStatus.Ok) continue;
                var projected = ModelToolResultProjection.Project(message);
                if (!ToolResultHistoryReader.TryRead(projected, out wire, out error)) continue;
                var receipt = ModelContextCompiler.CompleteOperation(message, wire,
                    new ResourceObservationNotice { BodyIncluded = false, State = EvidenceState.Unknown,
                        Reason = "Historical operation receipt; source bodies are selected independently.",
                        NextAction = "Keep the recorded outcome. Reuse an included current observation for further work." });
                receipt.Id = "receipt_" + message.Id;
                var recovery = message.ExecutionProgress?.Recovery;
                if (recovery != null) receipt.Content += "\nRECOVERY: " + recovery.ModelProjection().ToString(Formatting.None);
                var cost = EstimateCost(new[] { receipt }, settings);
                if (cost > receiptBudget || receipts.Count >= 24) break;
                receiptBudget -= cost;
                receipts.Add(receipt);
            }
            receipts.Reverse();
            result.Messages.InsertRange(0, receipts);
            if (result.IncludedBodies + result.OmittedBodies > 0)
                result.Messages.Add(new ChatMessage { Role = "user", ProtocolMessage = true,
                    Content = "WORKING_SET (reconstructed from saved results, without executing tools):\n" +
                        JsonConvert.SerializeObject(new { includedBodies = result.IncludedBodies, omittedBodies = result.OmittedBodies,
                            omittedTargets = result._omittedTargets,
                            instruction = "Reuse included complete bodies and exact coverage. Omitted bodies are outside this request budget, not missing resources. Read a known target only when its absent content is needed; do not repeat discovery or a completed mutation." }) });
            return result;
        }

        private List<ChatMessage> SelectBodies(ChatSession session, IReadOnlyList<ChatMessage> active,
            ResourceAuthoritySnapshotSet resources, AppSettings settings, int budget)
        {
            var bodies = new List<ChatMessage>();
            if (ContextCompactionService.ActiveCheckpoint(session) == null) return bodies;
            var reducer = new EvidenceStateReducer();
            Func<ResourceEvidence, bool> current = evidence => evidence != null &&
                reducer.Reduce(evidence, resources).State == EvidenceState.Current;
            var activeIds = new HashSet<string>(active.Select(item => item.Id), StringComparer.Ordinal);
            var selected = new HashSet<string>(active.Where(ExposesBody)
                .SelectMany(item => item.ResourceEvidence ?? new List<ResourceEvidence>()).Where(current)
                .Select(ObservationKey), StringComparer.Ordinal);
            var calls = session.Messages.Where(item => item.Role == "assistant" && item.CompletedOperation == null &&
                item.ToolCallId != null).GroupBy(item => item.ToolCallId).ToDictionary(group => group.Key, group => group.Last());
            foreach (var message in session.Messages.AsEnumerable().Reverse())
            {
                if (activeIds.Contains(message.Id) || !ContextCompactionService.IsReplayMessage(message) || !ExposesBody(message)) continue;
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
                var data = wire == null ? null : ToolResultWire.ParseData(wire.Result.DataJson) as JObject;
                ChatMessage call;
                var candidate = new List<ChatMessage>();
                // Skills/references and structured results keep the original closed
                // read frame: headers, fields, coverage, loaded state and body agree.
                if (message.ResourceEffect == null && calls.TryGetValue(message.ToolCallId ?? "", out call) &&
                    (message.ToolName == CapabilityToolCatalog.ReadToolId || evidence.Any(item =>
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

        internal static int EstimateCost(IEnumerable<ChatMessage> messages, AppSettings settings)
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
            var data = ToolResultWire.ParseData(wire.Result.DataJson) as JObject;
            if (message.ToolName == ResourceToolCatalog.ReadToolId)
                return (string)data?["type"] != "shared context" && (string)data?["type"] != "HTML data" &&
                    (string)data?["type"] != "HTML workspace";
            return message.ToolName == CapabilityToolCatalog.ReadToolId &&
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
