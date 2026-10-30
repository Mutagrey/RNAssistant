using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Persistence
{
    public sealed class FinishEvidenceSnapshot
    {
        public SessionEventPosition Head { get; private set; }
        public IReadOnlyList<string> RetainedMessageIds { get; private set; }
        internal IReadOnlyList<ConversationFinishEvidenceAdmission.Fact> Facts { get; private set; }
        internal FinishEvidenceSnapshot(SessionEventPosition head, IReadOnlyList<ConversationFinishEvidenceAdmission.Fact> facts,
            IReadOnlyList<string> retainedMessageIds)
        { Head = head; Facts = facts; RetainedMessageIds = retainedMessageIds; }
    }

    // The store validates the stream and selects canonical events through its
    // existing disposable index. Only cited calls hydrate their accepted bodies.
    // A delivered alias or an index row alone never establishes a terminal fact.
    public static class ConversationFinishEvidenceAdmission
    {
        public static FinishAdmission Evaluate(IEventStore store, ChatSession session,
            string runId, string turnId, IReadOnlyList<string> acceptedCallIds,
            IReadOnlyList<ConversationResultClaim> results, ChatBlobStore payloads, IReadOnlyList<string> deliveredReferences)
        {
            if (store == null || session == null || !session.UsesCurrentProtocol || string.IsNullOrWhiteSpace(runId) ||
                string.IsNullOrWhiteSpace(turnId) || acceptedCallIds == null || results == null || deliveredReferences == null)
                throw new ArgumentException("Current run/session and canonical evidence reader are required.");
            if (results.Any(item => item == null) || acceptedCallIds.Any(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException("Invalid finish proposal or accepted call identity.");
            var expected = new HashSet<string>(acceptedCallIds, StringComparer.Ordinal);
            if (expected.Count != acceptedCallIds.Count) throw new InvalidOperationException("Current accepted calls are ambiguous.");
            var references = results.SelectMany(item => item.Evidence).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var delivered = new HashSet<string>(deliveredReferences, StringComparer.OrdinalIgnoreCase);
            // Bounded metadata for correction hints; unselected historical bodies
            // and unrelated current accepted arguments are never hydrated here.
            var selection = references.Concat(deliveredReferences.Take(16)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var snapshot = store.ReadFinishEvidence(session, runId, selection);
            if (snapshot?.Head == null || snapshot.Head.SessionId != session.Id || snapshot.Head.Sequence != session.Revision ||
                snapshot.Head.Hash != session.StorageHeadHash)
                throw new InvalidOperationException("Canonical finish evidence is not current.");
            var retained = new HashSet<string>(snapshot.RetainedMessageIds, StringComparer.Ordinal);
            var facts = snapshot.Facts;
            var calls = facts.Where(fact => fact.Kind == SessionOperationTypes.ToolCallRecorded)
                .GroupBy(fact => fact.CallId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
            var terminals = facts.Where(fact => fact.Kind == SessionOperationTypes.ToolExecutionFinished)
                .GroupBy(fact => fact.CallId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
            var byAlias = terminals.Values.Where(fact => fact.Reference != null)
                .ToDictionary(fact => fact.Reference, fact => fact, StringComparer.OrdinalIgnoreCase);
            var current = calls.Values.Where(fact => fact.RunId == runId).ToArray();
            if (current.Length != expected.Count || current.Any(fact => fact.TurnId != turnId || !expected.Contains(fact.CallId)))
                throw new InvalidOperationException("Durable current-run accepted calls differ from kernel state.");
            var available = string.Join(", ", byAlias.Values.Where(fact => delivered.Contains(fact.Reference) &&
                retained.Contains(fact.Message.Id)).Take(16).Select(fact => fact.Reference + " (" +
                    fact.Message.Activity.ToolId + ": " + fact.Message.ExecutionProgress.Outcome + ")"));
            var hint = available.Length == 0 ? " No terminal references are available in this request." :
                " Available: " + available + ". Correct the citation without repeating tools.";
            foreach (var reference in references)
            {
                Fact terminal;
                if (!byAlias.TryGetValue(reference, out terminal))
                    return FinishAdmission.Reject("unknown_evidence", reference + " is not an eligible terminal result of this chat. T/R are not evidence." + hint);
                if (!delivered.Contains(reference))
                    return FinishAdmission.Reject("undelivered_evidence", reference + " was not delivered in this request." + hint);
                Fact call;
                if (!calls.TryGetValue(terminal.CallId, out call) || !retained.Contains(call.Message.Id) || !retained.Contains(terminal.Message.Id))
                    return FinishAdmission.Reject("unknown_evidence", reference + " no longer belongs to the retained chat history." + hint);
                ValidateSelected(call, terminal, facts, payloads);
            }
            // Missing current terminals cannot turn a damaged/incomplete index
            // into evidence-free success. Service-only completed work stays valid.
            if (expected.Any(id => !terminals.ContainsKey(id)))
                return FinishAdmission.Reject("nonterminal_evidence", "An accepted call has no canonical terminal result.");
            var currentEligible = terminals.Values.Any(fact => fact.RunId == runId && fact.Reference != null);
            if (currentEligible && results.Count == 0)
                return FinishAdmission.Reject("missing_finish_results", "Report the outcomes of the accepted calls." + hint);
            if (currentEligible && references.Length == 0)
                return FinishAdmission.Reject("missing_finish_evidence", "Cite at least one delivered eligible result (E1, E2)." + hint);
            foreach (var claim in results)
                if (claim.Assessment == ConversationAssessment.Satisfied &&
                    claim.Evidence.Any(id => byAlias[id].Message.ExecutionProgress.Outcome != ToolExecutionOutcome.Ok))
                    return FinishAdmission.Reject("contradictory_assessment", "A satisfied claim cites a failed, unknown or undispatched result.");
            return FinishAdmission.Allow();
        }

        private static void ValidateSelected(Fact call, Fact terminal, IReadOnlyList<Fact> facts, ChatBlobStore payloads)
        {
            var message = call.Message;
            var origin = message.AcceptedCallOrigin;
            var activity = terminal.Message.Activity;
            var progress = terminal.Message.ExecutionProgress;
            if (origin == null || !message.ProtocolMessage || message.ResponseProtocolVersion != ConversationResponse.ProtocolVersion ||
                string.IsNullOrWhiteSpace(origin.StepId) || string.IsNullOrWhiteSpace(origin.ModelAttemptId) || origin.CallIndex < 0 ||
                message.ToolCallReference?.Kind != ModelReferenceKind.ToolCall || message.EvidenceReference != null ||
                string.IsNullOrWhiteSpace(message.ToolName) || message.RunId != call.RunId ||
                terminal.RunId != call.RunId || terminal.TurnId != call.TurnId ||
                terminal.Message.RunId != terminal.RunId || activity?.RunId != call.RunId ||
                activity.ToolCallId != call.CallId || activity.ToolId != message.ToolName || activity.StepId != origin.StepId ||
                terminal.Message.ToolCallReference?.Kind != ModelReferenceKind.ToolCall ||
                terminal.Message.ToolCallReference?.Alias != message.ToolCallReference.Alias ||
                terminal.Message.EvidenceReference?.Kind != ModelReferenceKind.Evidence || progress == null ||
                progress.EvidenceEligibility != EvidenceEligibility.Eligible ||
                !Enum.IsDefined(typeof(ToolExecutionOutcome), progress.Outcome) || progress.Outcome == ToolExecutionOutcome.AwaitingConfirmation)
                throw new InvalidOperationException("Selected evidence disagrees with its canonical call/terminal identity.");
            var full = AcceptedCallPayloadService.Hydrate(message, payloads);
            var parsed = ConversationResponseHistoryReader.Read(full);
            if (!parsed.Success || parsed.Response.ToolCalls.Count != 1 || parsed.Response.ToolCalls[0].Id != call.CallId ||
                parsed.Response.ToolCalls[0].Name != message.ToolName)
                throw new InvalidOperationException("Selected accepted call body disagrees with its metadata.");
            if (call.Seed && terminal.Seed && call.Sequence == terminal.Sequence && call.Position < terminal.Position) return;
            if (call.Seed || terminal.Seed || call.Sequence > terminal.Sequence ||
                call.Sequence == terminal.Sequence && call.Position >= terminal.Position)
                throw new InvalidOperationException("Selected terminal precedes its accepted call.");
            if (progress.Outcome != ToolExecutionOutcome.NotDispatched && !facts.Any(start =>
                start.Kind == SessionOperationTypes.ToolExecutionStarted && start.CallId == call.CallId &&
                start.RunId == call.RunId && start.TurnId == call.TurnId && start.Message.Activity?.RunId == call.RunId &&
                start.Message.Activity?.ToolId == message.ToolName && start.Message.Activity?.StepId == origin.StepId &&
                (start.Sequence > call.Sequence || start.Sequence == call.Sequence && start.Position > call.Position) &&
                (start.Sequence < terminal.Sequence || start.Sequence == terminal.Sequence && start.Position < terminal.Position)))
                throw new InvalidOperationException("Selected terminal has no canonical dispatch boundary.");
        }

        internal sealed class Fact
        {
            internal string Kind, RunId, TurnId;
            internal ChatMessage Message;
            internal bool Seed;
            internal int Position;
            internal long Sequence;
            internal string CallId { get { return Message.ToolCallId ?? Message.Activity?.ToolCallId; } }
            internal string Reference { get { return Kind == SessionOperationTypes.ToolExecutionFinished ? Message.EvidenceReference?.Alias : null; } }
        }

        internal static IEnumerable<Fact> Facts(SessionEvent item, ISet<int> positions = null)
        {
            if (item.Type == SessionEventTypes.SessionCreated || item.Type == SessionEventTypes.SessionForked)
            {
                if ((int?)item.Data?["ReferenceContractVersion"] != ConversationResponse.ReferenceContractVersion) yield break;
                var messages = item.Data?["Messages"] as JArray ?? new JArray();
                foreach (var index in positions == null ? Enumerable.Range(0, messages.Count) : positions.OrderBy(value => value))
                {
                    if (index < 0 || index >= messages.Count) continue;
                    var message = messages[index].ToObject<ChatMessage>();
                    var kind = message.AcceptedCallOrigin != null ? SessionOperationTypes.ToolCallRecorded :
                        message.Activity != null && message.ExecutionProgress != null && message.ExecutionProgress.Outcome != ToolExecutionOutcome.AwaitingConfirmation
                            ? SessionOperationTypes.ToolExecutionFinished : null;
                    if (kind == null) continue;
                    // The canonical fork seed owns copied bindings. It neither
                    // invents a new dispatch nor reallocates T/E references.
                    yield return new Fact { Kind = kind, Message = message, Seed = true, Position = index,
                        Sequence = item.Sequence, RunId = message.RunId,
                        TurnId = (string)item.Data?["LastRun"]?["RunId"] == message.RunId ? (string)item.Data?["LastRun"]?["TurnId"] : null };
                }
                yield break;
            }
            if (item.Type != SessionEventTypes.SessionCommit) yield break;
            var operations = item.Data?["Operations"] as JArray;
            if (operations == null) throw new InvalidOperationException("Run commit has no typed operations.");
            foreach (var index in positions == null ? Enumerable.Range(0, operations.Count) : positions.OrderBy(value => value))
            {
                if (index < 0 || index >= operations.Count) continue;
                var operation = operations[index];
                var kind = (string)operation["Type"];
                if (kind != SessionOperationTypes.ToolCallRecorded && kind != SessionOperationTypes.ToolExecutionStarted &&
                    kind != SessionOperationTypes.ToolExecutionFinished) continue;
                var message = operation["Data"]?["Value"]?.ToObject<ChatMessage>();
                if (message == null) throw new InvalidOperationException("Tool fact lacks its canonical message.");
                if (kind == SessionOperationTypes.ToolExecutionFinished && message.ExecutionProgress?.Outcome == ToolExecutionOutcome.AwaitingConfirmation) continue;
                yield return new Fact { Kind = kind, Message = message, Position = index,
                    Sequence = item.Sequence, RunId = item.RunId, TurnId = item.TurnId };
            }
        }
    }
}
