using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    // Read-only presentation of existing canonical facts. No payload hydration,
    // finish admission, resource authority or model execution belongs here.
    internal static class ModelContextEvidenceProjection
    {
        internal static ModelContextEvidenceReportDto Describe(IEnumerable<SessionEvent> events,
            SessionEvent request, LlmTraceRecord trace, CancellationToken token)
        {
            var all = events.Where(item => item.SessionId == request.SessionId).OrderBy(item => item.Sequence).ToArray();
            var calls = new Dictionary<string, ConversationFinishEvidenceAdmission.Fact>(StringComparer.Ordinal);
            var terminals = new Dictionary<string, ConversationFinishEvidenceAdmission.Fact>(StringComparer.Ordinal);
            var eventIds = new Dictionary<long, string>();
            foreach (var item in all.TakeWhile(item => item.Sequence < request.Sequence))
            {
                token.ThrowIfCancellationRequested();
                eventIds[item.Sequence] = item.EventId;
                foreach (var fact in ConversationFinishEvidenceAdmission.Facts(item))
                {
                    if (string.IsNullOrWhiteSpace(fact.CallId)) continue;
                    if (fact.Kind == SessionOperationTypes.ToolCallRecorded) calls[fact.CallId] = fact;
                    if (fact.Kind == SessionOperationTypes.ToolExecutionFinished) terminals[fact.CallId] = fact;
                }
            }
            var entries = (trace.ContextMessages ?? new List<ContextMessagePresentation>()).Where(item => item != null).ToArray();
            var indices = entries.Where(item => item.MessageIndex >= 0 && item.MessageIndex < trace.MessageCount)
                .GroupBy(item => item.MessageIndex.Value).Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single());
            var finishes = Finishes(all, request, trace, token);
            var cited = new HashSet<string>(finishes.SelectMany(finish => finish.Claims).SelectMany(claim => claim.Evidence), StringComparer.Ordinal);
            var selected = new HashSet<string>(entries.Select(item => item.ToolCallId).Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.Ordinal);
            foreach (var call in calls.Values.Where(call => !string.IsNullOrEmpty(request.RunId) && call.RunId == request.RunId)) selected.Add(call.CallId);
            foreach (var terminal in terminals.Values.Where(fact => fact.Reference != null && cited.Contains(fact.Reference))) selected.Add(terminal.CallId);
            var rows = new List<ModelContextToolEvidenceDto>();
            foreach (var id in selected)
            {
                token.ThrowIfCancellationRequested();
                ConversationFinishEvidenceAdmission.Fact call, terminal;
                calls.TryGetValue(id, out call); terminals.TryGetValue(id, out terminal);
                var operation = call?.Message.ToolCallReference;
                // A trace-only ID or a later binding must never manufacture T/E or a terminal outcome.
                if (operation?.Kind != ModelReferenceKind.ToolCall) call = null;
                if (call == null || terminal?.Message.ToolCallReference?.Alias != operation?.Alias ||
                    terminal.RunId != call.RunId || terminal.Sequence < call.Sequence) terminal = null;
                var progress = terminal?.Message.ExecutionProgress;
                var evidence = progress?.EvidenceEligibility == EvidenceEligibility.Eligible ? terminal.Reference : null;
                var matching = entries.Where(item => item.ToolCallId == id).ToArray();
                var delivered = matching.Where(item => item.EvidenceReference?.Alias == evidence && evidence != null).ToArray();
                var delivery = ModelContextEvidenceDelivery.Unknown;
                if (delivered.Any(item => item.MessageIndex.HasValue && indices.ContainsKey(item.MessageIndex.Value) &&
                    (item.Presentation == ContextPresentationKind.Full || item.Presentation == ContextPresentationKind.Fragment ||
                     item.Presentation == ContextPresentationKind.Summary))) delivery = ModelContextEvidenceDelivery.Delivered;
                else if (delivered.Length > 0 && delivered.All(item => item.Presentation == ContextPresentationKind.Excluded))
                    delivery = ModelContextEvidenceDelivery.Excluded;
                rows.Add(new ModelContextToolEvidenceDto {
                    CallId = id, OperationAlias = call?.Message.ToolCallReference.Alias, EvidenceAlias = evidence,
                    ToolName = call?.Message.ToolName, RunId = call?.RunId, StepId = call?.Message.AcceptedCallOrigin?.StepId,
                    CallEventId = call == null ? null : eventIds[call.Sequence], ResultEventId = terminal == null ? null : eventIds[terminal.Sequence],
                    Outcome = progress?.Outcome, Eligibility = progress?.EvidenceEligibility, Delivery = delivery,
                    MessageIndices = matching.Where(item => item.MessageIndex.HasValue && indices.ContainsKey(item.MessageIndex.Value) &&
                        item.Presentation != ContextPresentationKind.Excluded).Select(item => item.MessageIndex.Value).Distinct().OrderBy(index => index).ToArray()
                });
            }
            return new ModelContextEvidenceReportDto { HasContextTrace = trace.ContextMessages != null,
                Tools = rows.OrderBy(row => calls.ContainsKey(row.CallId) ? calls[row.CallId].Sequence : long.MaxValue)
                    .ThenBy(row => row.CallId, StringComparer.Ordinal).ToArray(), Finishes = finishes };
        }

        private static IReadOnlyList<ModelContextFinishDto> Finishes(SessionEvent[] events,
            SessionEvent request, LlmTraceRecord trace, CancellationToken token)
        {
            var result = new List<ModelContextFinishDto>();
            if (string.IsNullOrWhiteSpace(trace.ModelAttemptId) || string.IsNullOrWhiteSpace(request.RunId)) return result;
            foreach (var item in events.Where(item => item.Sequence > request.Sequence && item.RunId == request.RunId))
            {
                token.ThrowIfCancellationRequested();
                // A repair/fallback request in the same attempt owns any later accepted decision.
                if (item.Type == SessionEventTypes.LlmRequest && item.Data?.ToObject<LlmTraceRecord>().ModelAttemptId == trace.ModelAttemptId) break;
                if (item.Type != SessionEventTypes.SessionCommit) continue;
                var commit = item.Data?.ToObject<CommitData>();
                foreach (var operation in commit?.Operations ?? new List<SessionOperation>())
                {
                    if (operation.Type != SessionOperationTypes.AssistantMessageAppended) continue;
                    var message = operation.Data?.ToObject<MessageData>()?.Value;
                    if (message?.ResponseModelAttemptId != trace.ModelAttemptId || message.RunId != request.RunId || message.RuntimeProjection) continue;
                    var read = ConversationResponseHistoryReader.Read(message);
                    if (!read.Success || read.Response.Action != ConversationResponse.FinishAction) continue;
                    result.Add(new ModelContextFinishDto { EventId = item.EventId, MessageId = message.Id,
                        Outcome = ConversationFinishContract.Name(read.Response.Outcome.Value),
                        Claims = read.Response.Results.Select(claim => new ModelContextFinishClaimDto {
                            Criterion = claim.Criterion, Assessment = claim.AssessmentName, Evidence = claim.Evidence }).ToArray() });
                }
            }
            return result;
        }

        private sealed class CommitData { public List<SessionOperation> Operations { get; set; } }
        private sealed class MessageData { public ChatMessage Value { get; set; } }
    }
}
