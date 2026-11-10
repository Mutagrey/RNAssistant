using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using RNAssistant.Core.Models;

namespace RNAssistant.Application.Conversations
{
    [JsonConverter(typeof(StringEnumConverter))]
    internal enum AgentAssignmentState { Open, DeliveryUnknown, Removed, ResolutionPrepared, ResolutionReceived }

    internal sealed class AgentAssignmentView
    {
        [JsonProperty("request_id")] public string RequestId { get; set; }
        [JsonProperty("agent_id")] public string AgentId { get; set; }
        public string Summary { get; set; }
        public AgentAssignmentState State { get; set; }
        [JsonProperty("reported_outcome", NullValueHandling = NullValueHandling.Ignore)] public AgentAssignmentOutcome? ReportedOutcome { get; set; }
        [JsonProperty("resolution_message_id", NullValueHandling = NullValueHandling.Ignore)] public string ResolutionMessageId { get; set; }
        [JsonProperty("input_status", NullValueHandling = NullValueHandling.Ignore)] public ConversationInputStatus? InputStatus { get; set; }
    }

    internal sealed class AgentAssignmentPage
    {
        public string Direction { get; set; }
        public IReadOnlyList<AgentAssignmentView> Items { get; set; }
        public bool Truncated { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string NextRequest { get; set; }
    }

    internal sealed partial class AgentCommunicationService
    {
        // Only an applied recipient can report a result. The original public message
        // ID is the assignment address; no task identity, hierarchy or second store.
        internal AgentDeliveryReceipt Resolve(ConversationInboxService inbox, ChatSession source, int limit,
            string runId, string operationId, string requestId, string outcome, string text, Action dispatch)
        {
            AgentAssignmentOutcome result;
            string recipient;
            lock (_sync)
            {
                RequireEnabled(limit);
                if (outcome == "completed") result = AgentAssignmentOutcome.Completed;
                else if (outcome == "failed") result = AgentAssignmentOutcome.Failed;
                else if (outcome == "declined") result = AgentAssignmentOutcome.Declined;
                else throw new AgentCommunicationException("agent_assignment_outcome_invalid", "Укажите completed, failed или declined.");
                var original = inbox.FindAgentMessage(source, requestId);
                if (original?.AgentCommunication?.Assignment != true || original.AgentCommunication.Sender == null ||
                    original.Status != ConversationInputStatus.Applied)
                    throw new AgentCommunicationException("agent_assignment_not_found", "Нет уже полученного поручения с этим message_id.");
                recipient = original.AgentCommunication.Sender.AgentId;
            }
            var receipt = Send(inbox, source, limit, runId, operationId, recipient,
                text, dispatch, replyTo: requestId, resolution: result);
            receipt.RequestId = requestId; receipt.ReportedOutcome = result;
            return receipt;
        }

        private static void RequireUnresolved(IEnumerable<AgentCommunicationEvent> facts, string requestId, string operationId)
        {
            var prior = facts.FirstOrDefault(item => item.State == AgentDeliveryState.Prepared &&
                item.ReplyTo == requestId && item.Resolution.HasValue && item.OperationId != operationId);
            if (prior != null) throw new AgentCommunicationException("agent_assignment_already_reported",
                "Результат поручения уже отправлен или его доставка неизвестна. Повтор и замена результата запрещены; используйте обычное уточняющее сообщение.");
        }

        internal AgentAssignmentPage Assignments(ConversationInboxService inbox, ChatSession source, int limit,
            string direction, string afterRequest)
        {
            lock (_sync)
            {
                RequireEnabled(limit);
                Scope(source);
                if ((direction != "incoming" && direction != "outgoing") ||
                    afterRequest != null && (string.IsNullOrWhiteSpace(afterRequest) || afterRequest.Length > 64))
                    throw new AgentCommunicationException("agent_assignment_query_invalid", "Укажите incoming/outgoing и при необходимости NextRequest предыдущей страницы.");
                var box = inbox.Get(source);
                lock (box.Sync)
                {
                    var facts = inbox.AgentEvents(source);
                    var accepted = new HashSet<string>(facts.Where(item => item.State == AgentDeliveryState.Accepted)
                        .Select(item => item.OperationId), StringComparer.Ordinal);
                    IEnumerable<AgentAssignmentView> views;
                    if (direction == "incoming")
                    {
                        var resolutions = facts.Where(item => item.State == AgentDeliveryState.Prepared && item.Resolution.HasValue)
                            .ToLookup(item => item.ReplyTo, StringComparer.Ordinal);
                        views = box.Inputs.Where(input => input.AgentCommunication?.Assignment == true).Select(input => {
                            var report = resolutions[input.AgentCommunication.MessageId].SingleOrDefault();
                            return new AgentAssignmentView { RequestId = input.AgentCommunication.MessageId,
                                AgentId = input.AgentCommunication.Sender.AgentId, Summary = AssignmentExcerpt(input.Text), InputStatus = input.Status,
                                State = report != null ? accepted.Contains(report.OperationId) ? AgentAssignmentState.ResolutionReceived : AgentAssignmentState.ResolutionPrepared
                                    : input.Status == ConversationInputStatus.Removed ? AgentAssignmentState.Removed : AgentAssignmentState.Open,
                                ReportedOutcome = report?.Resolution, ResolutionMessageId = report?.MessageId };
                        });
                    }
                    else
                    {
                        // Provenance only: pending report bodies do not bypass human FIFO
                        // or policy admission. Body delivery still belongs to the Inbox.
                        var resolutions = box.Inputs.Where(input => input.AgentCommunication?.Resolution != null)
                            .ToLookup(input => input.AgentCommunication.ReplyTo, StringComparer.Ordinal);
                        views = facts.Where(item => item.State == AgentDeliveryState.Prepared && item.Assignment).Select(request => {
                            var report = resolutions[request.MessageId].SingleOrDefault(input =>
                                input.AgentCommunication.Sender?.ChatId == request.Target.ChatId &&
                                input.AgentCommunication.Sender.Source == request.Target.Source &&
                                input.AgentCommunication.Sender.WorkspaceId == request.Target.WorkspaceId);
                            return new AgentAssignmentView { RequestId = request.MessageId, AgentId = request.Target.AgentId,
                                Summary = request.AssignmentSummary, State = report != null ? AgentAssignmentState.ResolutionReceived :
                                    accepted.Contains(request.OperationId) ? AgentAssignmentState.Open : AgentAssignmentState.DeliveryUnknown,
                                ReportedOutcome = report?.AgentCommunication.Resolution,
                                ResolutionMessageId = report?.AgentCommunication.MessageId, InputStatus = report?.Status };
                        });
                    }
                    var page = views.Where(item => afterRequest == null || string.CompareOrdinal(item.RequestId, afterRequest) > 0)
                        .OrderBy(item => item.RequestId, StringComparer.Ordinal).Take(51).ToArray();
                    return new AgentAssignmentPage { Direction = direction, Items = page.Take(50).ToArray(), Truncated = page.Length > 50,
                        NextRequest = page.Length > 50 ? page[49].RequestId : null };
                }
            }
        }

        private static string AssignmentExcerpt(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length <= 160) return text;
            return text.Substring(0, char.IsHighSurrogate(text[159]) ? 159 : 160) + "…";
        }
    }
}
