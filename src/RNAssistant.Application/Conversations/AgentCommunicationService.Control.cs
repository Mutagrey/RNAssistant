using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Application.Conversations
{
    [JsonConverter(typeof(StringEnumConverter))]
    internal enum AgentCancellationStatus { Requested, Cancelled, AlreadyStopped, Changed, Unavailable, Unconfirmed }

    internal sealed class AgentCancellationReceipt
    {
        [JsonProperty("agent_id")] public string AgentId { get; set; }
        public AgentCancellationStatus Status { get; set; }
        internal bool Changed;
    }

    internal sealed partial class AgentCommunicationService
    {
        internal AgentCancellationReceipt Cancel(ConversationInboxService sourceInbox, ChatSession source, int limit,
            string operationId, string agentId, Action dispatch)
        {
            AgentConversationEndpoint target;
            AgentCommunicationEvent prepared;
            ConversationActiveSnapshot live;
            ChatSession selected;
            long revision;
            lock (_sync)
            {
                RequireEnabled(limit);
                if (string.IsNullOrWhiteSpace(agentId) || agentId == Address(source).AgentId)
                    throw new AgentCommunicationException("agent_cancel_invalid", "Укажите адрес другого участника.");
                var fingerprint = ConversationInputJournal.Fingerprint(new[] { "cancel", agentId });
                var prior = sourceInbox.AgentEvents(source).Where(item => item.OperationId == operationId).ToArray();
                if (prior.Length > 0)
                {
                    if (prior.Any(item => item.Fingerprint != fingerprint || item.Target?.AgentId != agentId))
                        throw new AgentCommunicationException("agent_operation_conflict", "Идентификатор уже принадлежит другой операции.");
                    return new AgentCancellationReceipt { AgentId = agentId,
                        Status = prior.LastOrDefault(item => item.State == AgentDeliveryState.CancelRecorded)?.Cancellation ?? AgentCancellationStatus.Unconfirmed };
                }
                target = FindAgent(source, agentId);
                if (target == null) throw new AgentCommunicationException("agent_chat_not_found", "Участник недоступен в этой области.");
                var session = target.Load();
                selected = session;
                target.Validate(session);
                ConversationRunPolicy.RequireCurrentSession(session);
                prepared = new AgentCommunicationEvent { State = AgentDeliveryState.CancelPrepared,
                    OperationId = operationId, Fingerprint = fingerprint, Target = target.Address, TargetRunId = session.LastRun?.RunId };
                revision = session.Revision;
                live = target.Runs?.CaptureCancellation(session.Id, prepared.TargetRunId);
                // Persist the exact target before signalling. An uncertain request is
                // never replayed against a new Run or a later invocation of this Run.
                dispatch();
                sourceInbox.AppendAgentEvent(source, prepared);
            }
            // Preserve the original failure in the normal ToolRuntime diagnostic.
            // Prepared without a receipt remains unknown and cannot be replayed.
            bool changed;
            ChatSession terminal;
            var status = CancelTarget(target, prepared.TargetRunId, revision, selected, live, out changed, out terminal);
            try
            {
                sourceInbox.AppendAgentEvent(source, new AgentCommunicationEvent { State = AgentDeliveryState.CancelRecorded,
                    OperationId = operationId, Fingerprint = prepared.Fingerprint, Target = prepared.Target,
                    TargetRunId = prepared.TargetRunId, Cancellation = status });
            }
            finally
            {
                // Publish outside the lease/Inbox lock. A closed view cannot invalidate
                // a durable outcome; live runs use their normal progress owner.
                if (terminal != null) try { target.Changed?.Invoke(terminal); } catch { }
            }
            return new AgentCancellationReceipt { AgentId = agentId, Status = status, Changed = changed };
        }

        private static AgentCancellationStatus CancelTarget(AgentConversationEndpoint target, string runId,
            long revision, ChatSession selected, ConversationActiveSnapshot live, out bool changed, out ChatSession terminal)
        {
            changed = false;
            terminal = null;
            if (target.IsClosing?.Invoke() == true) return AgentCancellationStatus.Unavailable;
            if (live != null)
            {
                if (live.Cancellation == null) return AgentCancellationStatus.Unavailable;
                var box = target.Inbox.Get(selected);
                lock (box.Sync)
                {
                    if (box.Session.LastRun?.RunId != runId) return AgentCancellationStatus.Changed;
                    if (!target.Runs.Cancel(target.Address.ChatId, runId, live.Cancellation)) return AgentCancellationStatus.Changed;
                    target.Inbox.PauseForRun(box.Session, runId, "Остановлено другим агентом");
                }
                changed = true;
                return AgentCancellationStatus.Requested;
            }
            if (target.Reserve == null || target.Store == null) return AgentCancellationStatus.Unavailable;
            IDisposable reservation;
            try { reservation = target.Reserve(); }
            catch (Exception error) when (error is InvalidOperationException || error is System.IO.IOException)
            { return AgentCancellationStatus.Unavailable; }
            using (reservation)
            {
                var current = target.Load();
                target.Validate(current);
                var box = target.Inbox.Get(current);
                lock (box.Sync)
                {
                    if (current.LastRun?.RunId != runId || box.Running || box.Worker) return AgentCancellationStatus.Changed;
                    var state = current.LastRun?.KernelState?.Summary;
                    if (state?.Lifecycle == RunLifecycle.Cancelled) return AgentCancellationStatus.Cancelled;
                    if (state == null && current.LastRun == null || state?.Lifecycle == RunLifecycle.Completed || state?.Lifecycle == RunLifecycle.Failed)
                        return AgentCancellationStatus.AlreadyStopped;
                    if (current.Revision != revision || box.Session.Revision != revision) return AgentCancellationStatus.Changed;
                    if (!ConversationInboxService.HasPendingWait(current)) return AgentCancellationStatus.Unavailable;
                    target.Inbox.PauseForRun(current, runId, "Остановлено другим агентом");
                    try { ConversationWaitCancellation.Commit(current, target.Store, target.Payloads); }
                    catch
                    {
                        // Never keep an uncommitted terminal projection after storage
                        // failure. The paused queue still prevents automatic dispatch.
                        box.Session = target.Store.Load(current.Id);
                        throw;
                    }
                    // Only durable read-back establishes the terminal receipt.
                    var saved = target.Store.Load(current.Id);
                    if (saved.LastRun?.RunId != runId || saved.LastRun.KernelState?.Summary.Lifecycle != RunLifecycle.Cancelled ||
                        saved.LastRun.KernelState.Summary.PendingWait != null) return AgentCancellationStatus.Unconfirmed;
                    changed = true;
                    terminal = saved;
                    return AgentCancellationStatus.Cancelled;
                }
            }
        }
    }
}
