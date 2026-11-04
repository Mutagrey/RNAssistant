using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Application.Conversations
{
    internal sealed partial class AgentCommunicationService
    {
        internal const int RecoveryPageSize = 20;
        internal const int MaximumResumeParticipants = 32;

        private AgentConversationEndpoint[] RecoveryCatalog(AgentRecoveryScope request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.SessionId) || request.SessionId.Length > 128 ||
                !(request.Source == "office" && request.WorkspaceId == null ||
                    request.Source == "workspace" && !string.IsNullOrWhiteSpace(request.WorkspaceId)))
                throw new ArgumentException("Требуется точная область исходного чата.");
            lock (_sync)
            {
                AgentConversationScope scope;
                if (!_scopes.TryGetValue(request.Source == "office" ? "office" : "workspace:" + request.WorkspaceId, out scope))
                    throw new InvalidOperationException("Область чата недоступна.");
                var catalog = scope.ReadCatalog().GroupBy(item => item.Address.ChatId, StringComparer.Ordinal)
                    .Select(group => group.Last()).OrderBy(item => item.Address.ChatId, StringComparer.Ordinal).ToArray();
                var anchor = catalog.SingleOrDefault(item => item.Address.ChatId == request.SessionId);
                var session = anchor?.Load();
                if (session == null || !scope.Contains(session)) throw new InvalidOperationException("Исходный чат недоступен в этой области.");
                anchor.Validate(session);
                return catalog;
            }
        }

        // Bounded, on-demand inspection. No model calls, view registry or durable group store.
        internal AgentRecoveryPage ListRecoverableWaits(AgentRecoveryQuery request)
        {
            if (request?.AfterSessionId != null && (string.IsNullOrWhiteSpace(request.AfterSessionId) || request.AfterSessionId.Length > 128))
                throw new ArgumentException("Некорректная граница списка чатов.");
            var page = RecoveryCatalog(request).Where(item => request.AfterSessionId == null ||
                StringComparer.Ordinal.Compare(item.Address.ChatId, request.AfterSessionId) > 0).Take(RecoveryPageSize + 1).ToArray();
            var participants = new List<AgentRecoveryParticipant>();
            foreach (var endpoint in page.Take(RecoveryPageSize))
            {
                var participant = new AgentRecoveryParticipant { SessionId = endpoint.Address.ChatId,
                    AgentId = endpoint.Address.AgentId, Title = endpoint.Address.Title };
                try
                {
                    if (endpoint.Inbox.HasActiveWork(endpoint.Address.ChatId)) continue;
                    var session = endpoint.Load();
                    if (session == null) throw new InvalidOperationException("Чат больше недоступен.");
                    if (!ConversationInboxService.HasPendingWait(session)) continue;
                    endpoint.Validate(session);
                    var box = endpoint.Inbox.Get(session);
                    lock (box.Sync)
                    {
                        if (box.Running || box.Worker || !box.Paused) continue;
                        participant.Target = new AgentWaitResumeTarget { SessionId = session.Id,
                            RunId = session.LastRun.RunId, Revision = session.Revision };
                    }
                }
                catch (Exception error) { participant.Error = error.Message; }
                participants.Add(participant);
            }
            return new AgentRecoveryPage { Participants = participants,
                NextAfterSessionId = page.Length > RecoveryPageSize ? page[RecoveryPageSize - 1].Address.ChatId : null };
        }

        internal AgentRecoveryResponse ResumeSelectedWaits(AgentRecoveryCommand request)
        {
            if (request?.Targets == null || request.Targets.Count == 0 || request.Targets.Count > MaximumResumeParticipants ||
                request.Targets.Any(item => item == null || string.IsNullOrWhiteSpace(item.SessionId) || item.SessionId.Length > 128 ||
                    string.IsNullOrWhiteSpace(item.RunId) || item.RunId.Length > 128 || item.Revision < 1) ||
                request.Targets.Select(item => item.SessionId).Distinct(StringComparer.Ordinal).Count() != request.Targets.Count)
                throw new ArgumentException("Выберите от 1 до 32 разных чатов с точной версией сохранённого запуска.");
            var catalog = RecoveryCatalog(request).ToDictionary(item => item.Address.ChatId, StringComparer.Ordinal);
            var receipts = new List<AgentWaitResumeReceipt>();
            var starts = new List<Action>();
            foreach (var target in request.Targets)
            {
                var receipt = new AgentWaitResumeReceipt { SessionId = target.SessionId, RunId = target.RunId,
                    Status = AgentWaitResumeStatus.Unavailable };
                receipts.Add(receipt);
                AgentConversationEndpoint endpoint;
                if (!catalog.TryGetValue(target.SessionId, out endpoint))
                { receipt.Detail = "Чат недоступен в выбранной области."; continue; }
                var appendAttempted = false;
                try
                {
                    if (endpoint.ReserveForResume == null || endpoint.IsClosing?.Invoke() == true)
                        throw new InvalidOperationException("Runtime не принимает продолжение.");
                    ChatSession session;
                    using (endpoint.ReserveForResume())
                    {
                        session = endpoint.Load();
                        if (session == null) throw new InvalidOperationException("Чат больше недоступен.");
                        ConversationRunPolicy.RequireCurrentSession(session);
                        endpoint.Validate(session);
                        var box = endpoint.Inbox.Get(session);
                        lock (box.Sync)
                        {
                            if (box.Running || box.Worker || !box.Paused || session.Revision != target.Revision ||
                                box.Session.Revision != target.Revision || session.LastRun?.RunId != target.RunId ||
                                !ConversationInboxService.HasPendingWait(session))
                            { receipt.Status = AgentWaitResumeStatus.Changed; receipt.Detail = "Состояние изменилось. Обновите список."; continue; }
                            ConversationRunPolicy.ForAcceptedRun(session);
                            endpoint.ValidateResume?.Invoke(session);
                            appendAttempted = true;
                            endpoint.Inbox.Resume(session);
                        }
                    }
                    receipt.Status = AgentWaitResumeStatus.Requested;
                    // Admit every selected participant before starting any of them:
                    // an immediate peer reply must not stale another selection in this batch.
                    starts.Add(() => {
                        try { endpoint.Start(session); }
                        catch (Exception error)
                        { receipt.Status = AgentWaitResumeStatus.Unconfirmed; receipt.Detail = error.Message; }
                    });
                }
                catch (Exception error)
                { receipt.Status = appendAttempted ? AgentWaitResumeStatus.Unconfirmed : AgentWaitResumeStatus.Unavailable; receipt.Detail = error.Message; }
            }
            foreach (var start in starts) start();
            return new AgentRecoveryResponse { Receipts = receipts };
        }
    }
}
