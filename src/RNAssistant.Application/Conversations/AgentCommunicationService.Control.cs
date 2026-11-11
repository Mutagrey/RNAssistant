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

    [JsonConverter(typeof(StringEnumConverter))]
    internal enum AgentPauseStatus { Requested, Paused, Released, NoChange, Protected, Changed, Unavailable, Unconfirmed }

    internal sealed class AgentPauseReceipt
    {
        [JsonProperty("agent_id")] public string AgentId { get; set; }
        public AgentPauseStatus Status { get; set; }
        internal bool Changed;
    }

    internal sealed partial class AgentCommunicationService
    {
        // A host user gesture addresses the existing Office/Workspace owner, independent of peer grants.
        // The returned release is bound to this exact live invocation and pause, never to a later run.
        internal Action PauseForUser(string chatId, string pauseId)
        {
            AgentConversationEndpoint endpoint;
            lock (_sync) endpoint = Find(chatId);
            if (endpoint == null || endpoint.IsClosing?.Invoke() == true || !endpoint.Inbox.HasActiveWork(chatId)) return null;
            var session = endpoint.Load();
            endpoint.Validate(session);
            var box = endpoint.Inbox.Get(session);
            ConversationActiveSnapshot live;
            bool changed;
            lock (box.Sync)
            {
                live = endpoint.Runs?.CaptureCancellation(chatId, box.Session.LastRun?.RunId);
                if (live?.Cancellation == null || live.Cancellation.IsCancellationRequested) return null;
                var status = endpoint.Inbox.ControlPause(box.Session, live.RunId, pauseId, false, out changed, userControl: true);
                if (status != AgentPauseStatus.Requested && status != AgentPauseStatus.Paused) return null;
                session = box.Session;
            }
            if (changed) try { endpoint.Changed?.Invoke(session); } catch { }
            return () => {
                if (endpoint.IsClosing?.Invoke() == true) return;
                lock (box.Sync)
                {
                    var current = endpoint.Runs.CaptureCancellation(chatId, live.RunId);
                    if (current == null || !ReferenceEquals(current.Cancellation, live.Cancellation) ||
                        live.Cancellation.IsCancellationRequested) return;
                    session = box.Session;
                }
                // Host validation must not hold the Inbox lock across a document/COM gate.
                endpoint.ValidateResume?.Invoke(session);
                lock (box.Sync)
                {
                    var current = endpoint.Runs.CaptureCancellation(chatId, live.RunId);
                    if (current == null || !ReferenceEquals(current.Cancellation, live.Cancellation) ||
                        live.Cancellation.IsCancellationRequested) return;
                    endpoint.Inbox.ControlPause(box.Session, live.RunId, pauseId, true, out changed, userControl: true);
                    session = box.Session;
                }
                if (changed) try { endpoint.Changed?.Invoke(session); } catch { }
            };
        }

        internal AgentPauseReceipt Pause(ConversationInboxService sourceInbox, ChatSession source, int limit,
            string operationId, string agentId, bool resume, Action dispatch)
        {
            AgentConversationEndpoint target;
            AgentCommunicationEvent prepared;
            ConversationActiveSnapshot live;
            ChatSession selected;
            var recorded = resume ? AgentDeliveryState.ResumeRecorded : AgentDeliveryState.PauseRecorded;
            lock (_sync)
            {
                RequireEnabled(limit);
                if (string.IsNullOrWhiteSpace(agentId) || agentId == Address(source).AgentId)
                    throw new AgentCommunicationException("agent_control_invalid", "Укажите адрес другого участника.");
                var fingerprint = ConversationInputJournal.Fingerprint(new[] { resume ? "resume" : "pause", agentId });
                var prior = sourceInbox.AgentEvents(source).Where(item => item.OperationId == operationId).ToArray();
                if (prior.Length > 0)
                {
                    if (prior.Any(item => item.Fingerprint != fingerprint || item.Target?.AgentId != agentId))
                        throw new AgentCommunicationException("agent_operation_conflict", "Идентификатор уже принадлежит другой операции.");
                    return new AgentPauseReceipt { AgentId = agentId,
                        Status = prior.LastOrDefault(item => item.State == recorded)?.PauseControl ?? AgentPauseStatus.Unconfirmed };
                }
                target = FindAgent(source, agentId);
                if (target == null) throw new AgentCommunicationException("agent_chat_not_found", "Участник недоступен в этой области.");
                selected = target.Load();
                target.Validate(selected);
                ConversationRunPolicy.RequireCurrentSession(selected);
                var box = target.Inbox.Get(selected);
                lock (box.Sync)
                {
                    live = target.Runs?.CaptureCancellation(selected.Id, box.Session.LastRun?.RunId);
                    prepared = new AgentCommunicationEvent { State = resume ? AgentDeliveryState.ResumePrepared : AgentDeliveryState.PausePrepared,
                        OperationId = operationId, Fingerprint = fingerprint, Target = target.Address,
                        TargetRunId = box.Session.LastRun?.RunId, PauseId = resume ? box.AgentPause?.PauseId : operationId };
                }
                // Pin invocation AND pause before any target change. An unknown result never retries.
                dispatch();
                sourceInbox.AppendAgentEvent(source, prepared);
            }
            bool changed = false;
            ChatSession updated = null;
            var status = AgentPauseStatus.Unavailable;
            if (target.IsClosing?.Invoke() != true)
            {
                var box = target.Inbox.Get(selected);
                lock (box.Sync)
                {
                    if (live == null || live.Cancellation == null)
                        status = box.Paused || ConversationInboxService.HasPendingWait(box.Session)
                            ? AgentPauseStatus.Protected : AgentPauseStatus.Unavailable;
                    else
                    {
                        var current = target.Runs.CaptureCancellation(selected.Id, prepared.TargetRunId);
                        if (current == null || !ReferenceEquals(current.Cancellation, live.Cancellation)) status = AgentPauseStatus.Changed;
                        else if (live.Cancellation.IsCancellationRequested) status = AgentPauseStatus.Protected;
                        else status = target.Inbox.ControlPause(box.Session, prepared.TargetRunId, prepared.PauseId, resume, out changed);
                    }
                    if (changed) updated = box.Session;
                }
            }
            try
            {
                sourceInbox.AppendAgentEvent(source, new AgentCommunicationEvent { State = recorded,
                    OperationId = operationId, Fingerprint = prepared.Fingerprint, Target = prepared.Target,
                    TargetRunId = prepared.TargetRunId, PauseId = prepared.PauseId, PauseControl = status });
            }
            finally { if (updated != null) try { target.Changed?.Invoke(updated); } catch { } }
            return new AgentPauseReceipt { AgentId = agentId, Status = status, Changed = changed };
        }

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
            long revision, ChatSession selected, ConversationActiveSnapshot live, out bool changed, out ChatSession terminal,
            string pauseReason = "Остановлено другим агентом")
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
                    target.Inbox.PauseForRun(box.Session, runId, pauseReason);
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
                    target.Inbox.PauseForRun(current, runId, pauseReason);
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
