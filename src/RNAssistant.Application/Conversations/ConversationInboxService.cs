using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;

namespace RNAssistant.Application.Conversations
{
    // Owns intake and its durable transitions. Never edits the run's message list.
    // ChatStore serializes appends and advances the SAME live session's storage cursor.
    internal sealed class ConversationInboxService
    {
        internal sealed class AcceptedInput
        {
            internal SessionEvent Event;
            internal ConversationInput Initial;
            internal ConversationInput Current;
        }
        private sealed class SharedInbox
        {
            internal ConversationInboxService Service;
            internal int Users;
        }
        private static readonly object SharedSync = new object();
        private static readonly Dictionary<string, SharedInbox> Shared =
            new Dictionary<string, SharedInbox>(Path.DirectorySeparatorChar == '\\'
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        // Controllers in one process must use the same live session cursor and interrupt
        // token. The event log remains the only durable source; the last release drops
        // this projection so a new controller replays pending input as paused.
        internal static ConversationInboxService AcquireShared(string root, IEventStore events)
        {
            var key = Path.GetFullPath(root);
            lock (SharedSync)
            {
                SharedInbox shared;
                if (!Shared.TryGetValue(key, out shared))
                {
                    shared = new SharedInbox { Service = new ConversationInboxService(events) };
                    Shared.Add(key, shared);
                }
                shared.Users++;
                return shared.Service;
            }
        }

        internal static void ReleaseShared(string root, ConversationInboxService service)
        {
            var key = Path.GetFullPath(root);
            lock (SharedSync)
            {
                SharedInbox shared;
                if (!Shared.TryGetValue(key, out shared) || !ReferenceEquals(shared.Service, service) || shared.Users <= 0)
                    throw new InvalidOperationException("Conversation inbox lease is not owned by this controller.");
                if (--shared.Users == 0) Shared.Remove(key);
            }
        }

        internal sealed class Inbox
        {
            internal readonly object Sync = new object();
            internal readonly HashSet<string> ExplicitInputs = new HashSet<string>(StringComparer.Ordinal);
            internal readonly List<ConversationInput> Inputs = new List<ConversationInput>();
            internal readonly Dictionary<string, AcceptedInput> Accepted =
                new Dictionary<string, AcceptedInput>(StringComparer.Ordinal);
            internal ChatSession Session;
            internal CancellationTokenSource Interrupt = new CancellationTokenSource();
            internal TaskCompletionSource<bool> InputChanged = NewInputSignal();
            internal readonly HashSet<string> ReadyAgentInputs = new HashSet<string>(StringComparer.Ordinal);
            internal bool AgentWaiting;
            internal ConversationAgentPause AgentPause;
            internal bool Running, Accepting, Worker, Paused;
            internal string PauseReason;
            internal long Revision, ViewRevision;
        }
        private readonly object _sync = new object();
        private readonly Dictionary<string, Inbox> _inboxes = new Dictionary<string, Inbox>(StringComparer.Ordinal);
        private readonly IEventStore _events;
        private readonly string _epoch = Guid.NewGuid().ToString("N");
        internal ConversationInboxService(IEventStore events) { _events = events; }

        internal IReadOnlyList<AgentCommunicationEvent> AgentEvents(ChatSession session)
        {
            var box = Get(session);
            lock (box.Sync) return _events.Read(ConversationMetadata.From(box.Session), SessionEventReadMode.RequireCompleteAgentCommunicationJournal)
                .Where(item => item.Type == SessionEventTypes.AgentCommunicationChanged)
                .Select(item => item.Data.ToObject<AgentCommunicationEvent>()).ToArray();
        }

        internal IReadOnlyList<AgentRequestReference> AgentRequests(ChatSession session, IReadOnlyList<string> messageIds)
        {
            var box = Get(session);
            lock (box.Sync) return _events.ReadAgentRequests(ConversationMetadata.From(box.Session), messageIds);
        }

        internal void AppendAgentEvent(ChatSession session, AgentCommunicationEvent change)
        {
            var box = Get(session);
            lock (box.Sync)
            {
                _events.Append(box.Session, new SessionEventWrite(
                    SessionEventDescriptors.For(SessionEventKind.AgentCommunicationChanged), change, null, null));
                box.ViewRevision++;
            }
        }

        internal AgentPeer AgentPeer(AgentChatAddress address)
        {
            Inbox box;
            lock (_sync) box = _inboxes.Values.FirstOrDefault(item => item.Session.Id == address.ChatId);
            if (box == null) return new AgentPeer { AgentId = address.AgentId, Title = address.Title,
                ScopeTitle = address.ScopeTitle, Source = address.Source, Status = "not_loaded" };
            lock (box.Sync) return new AgentPeer { AgentId = address.AgentId, Title = box.Session.Title,
                ScopeTitle = box.Session.DocumentTitle, Source = address.Source,
                Status = box.Paused && box.Session.LastRun?.KernelState?.Summary.PauseId != null ? "paused" :
                    (box.Running || box.Worker) && box.Session.LastRun?.KernelState?.ExecutionQueued == true ? "queued" :
                    box.AgentWaiting ? "waiting" : box.Running || box.Worker ? "working" :
                    box.Paused ? "paused" : PendingConfirmationId(box.Session) != null ? "awaiting_approval" : "idle" };
        }

        internal bool HasActiveWork(string chatId)
        {
            List<Inbox> candidates;
            lock (_sync) candidates = _inboxes.Values.Where(box => box.Session.Id == chatId).ToList();
            foreach (var box in candidates)
                lock (box.Sync) if (box.Worker || box.Running) return true;
            return false;
        }

        internal ChatSession RunnableSession(string chatId, string workspaceId)
        {
            List<Inbox> candidates;
            lock (_sync) candidates = _inboxes.Values.Where(box => box.Session.Id == chatId &&
                box.Session.WorkspaceId == workspaceId).ToList();
            foreach (var box in candidates)
                lock (box.Sync) if (!box.Worker && !box.Running && HasRunnableLocked(box, box.Session)) return box.Session;
            return null;
        }

        internal Inbox Get(ChatSession session)
        {
            bool created;
            return Get(session, out created);
        }

        private Inbox Get(ChatSession session, out bool created)
        {
            var key = ScopeKey(session);
            Inbox box;
            lock (_sync)
            {
                created = !_inboxes.TryGetValue(key, out box);
                if (created)
                {
                    box = new Inbox { Session = session };
                    foreach (var record in ReadInboxReplay(session, 0))
                    {
                        Apply(box, record.Change, record.Event);
                        box.Revision = record.Event.Sequence;
                        box.ViewRevision++;
                    }
                    box.AgentPause = null;
                    if (box.Inputs.Any(IsPending) || HasPendingWait(box.Session)) { box.Paused = true; box.PauseReason = "После перезапуска"; }
                    _inboxes.Add(key, box);
                }
            }
            lock (box.Sync)
                if (!box.Running && !box.Worker && session.Revision >= box.Session.Revision) box.Session = session;
            return box;
        }

        private static string ScopeKey(ChatSession session)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.Id))
                throw new ArgumentException("A scoped conversation is required.", nameof(session));
            return ScopeKey(session.Host, session.WorkspaceId, session.DocumentKey, session.Id);
        }

        private static string ScopeKey(string host, string workspaceId, string documentKey, string chatId)
        { return (host ?? string.Empty) + "\0" + (workspaceId ?? string.Empty) + "\0" + (documentKey ?? string.Empty) + "\0" + chatId; }

        internal void Bind(ChatSession session, bool queuedWait = false)
        {
            var box = Get(session);
            lock (box.Sync)
            {
                if (queuedWait && (!box.Worker || box.Paused || box.Running ||
                    box.Session.LastRun?.RunId != session.LastRun?.RunId || !HasPendingWait(session)))
                    throw new OperationCanceledException("Продолжение ожидания уже остановлено или изменилось.");
                box.Session = session;
                box.Running = true;
                box.Accepting = true;
                box.ViewRevision++;
            }
        }
        internal void End(ChatSession session, bool completed, bool awaiting)
        {
            var box = Get(session);
            lock (box.Sync)
            {
                box.Session = session;
                box.Running = false;
                box.Accepting = false;
                box.ReadyAgentInputs.Clear();
                box.AgentWaiting = false;
                SignalInputChanged(box);
                box.ViewRevision++;
                if (!completed && !awaiting) PauseLocked(box, "Работа остановлена или завершилась с ошибкой");
                else if (box.AgentPause != null) Write(box, null, box.Paused, box.PauseReason);
            }
        }
        internal void EndRun(ChatSession session)
        {
            var summary = session.LastRun?.KernelState?.Summary;
            // A finished turn may contain rejected/failed attempts that the agent
            // already handled. Their counters must not freeze later user/peer input.
            // Historical unknown counters do not gate delivery: the kernel owns
            // inspection/replay safety. Failed/cancelled runs and explicit Stop still pause.
            End(session, summary?.Lifecycle == RNAssistant.Core.Agent.RunLifecycle.Completed,
                summary?.Lifecycle == RNAssistant.Core.Agent.RunLifecycle.AwaitingConfirmation);
        }
        internal ConversationInboxSnapshot Snapshot(ChatSession session, IReadOnlyList<string> receiptOperationIds = null)
        {
            if (receiptOperationIds != null && (receiptOperationIds.Count > 128 ||
                receiptOperationIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128)))
                throw new ArgumentException("Receipt lookup requires at most 128 operation IDs of 1–128 characters.");
            var requested = new HashSet<string>(receiptOperationIds ?? new string[0], StringComparer.Ordinal);
            var box = Get(session);
            lock (box.Sync) return new ConversationInboxSnapshot
            {
                ChatId = session.Id, Epoch = _epoch, Revision = box.ViewRevision, Phase = box.Session.LastRun == null ? null : box.Session.LastRun.Phase, Running = box.Running || box.Worker,
                ActiveRun = box.Running, HasPendingWait = HasPendingWait(box.Session), Paused = box.Paused, PauseReason = box.PauseReason,
                RunPaused = box.Session.LastRun?.KernelState?.Summary.PauseId != null, WaitTarget = SavedWaitTarget(box.Session),
                Items = Undelivered(box.Session, box.Inputs, null).Select(Clone).ToList(),
                Receipts = requested.Count == 0 ? new List<ConversationInboxReceipt>() : box.Inputs.Where(input => requested.Contains(input.OperationId))
                    .Select(input => new ConversationInboxReceipt { Id = input.Id, OperationId = input.OperationId,
                        Revision = input.Revision, Status = input.Status }).ToList()
            };
        }
        // Same read-only input projection for live Office snapshots and Workspace
        // snapshots of a different process. Workspace reads canonical events here;
        // it never treats a persisted message as still waiting for delivery.
        internal IReadOnlyList<ConversationInput> ReadUndelivered(ChatSession session, InputDelivery? delivery = null)
        {
            var latest = new ConversationInputJournal(_events).Read(session, false, session.Revision)
                .Where(record => record.Change.Input != null)
                .GroupBy(record => record.Change.Input.Id, StringComparer.Ordinal)
                .Select(group => group.Last()).ToArray();
            var byId = latest.ToDictionary(record => record.Change.Input.Id, StringComparer.Ordinal);
            return Undelivered(session, latest.Select(record => record.Change.Input), delivery)
                .Select(input =>
                {
                    var record = byId[input.Id];
                    var copy = Clone(input);
                    copy.Text = _events.ReadPayload(ConversationMetadata.From(session), record.Event) ??
                        throw new InvalidOperationException("Conversation input body is missing.");
                    return copy;
                }).ToArray();
        }

        private static IEnumerable<ConversationInput> Undelivered(ChatSession session,
            IEnumerable<ConversationInput> inputs, InputDelivery? delivery)
        {
            var candidates = inputs.Where(input => IsPending(input) &&
                (!delivery.HasValue || input.Delivery == delivery.Value)).ToArray();
            if (!candidates.Any(input => input.Status == ConversationInputStatus.Delivering)) return candidates;
            // Only a claimed input can have a persisted user message. Ordinary
            // pending snapshots avoid scanning a long transcript on every poll.
            var messageIds = new HashSet<string>((session.Messages ?? new List<ChatMessage>())
                .Where(message => message != null && message.Id != null).Select(message => message.Id), StringComparer.Ordinal);
            return candidates.Where(input => input.Status == ConversationInputStatus.Pending ||
                !messageIds.Contains(input.Id) || !ConversationInputJournal.HasPersistedUserMessage(session, input));
        }
        internal ConversationInput Find(ChatSession session, string id)
        {
            var box = Get(session);
            lock (box.Sync) return Clone(box.Inputs.FirstOrDefault(i => i.Id == id));
        }
        internal ConversationInput FindAgentMessage(ChatSession session, string messageId)
        {
            if (string.IsNullOrWhiteSpace(messageId) || messageId.Length > 64) return null;
            var box = Get(session);
            lock (box.Sync) return Clone(box.Inputs.SingleOrDefault(input => input.AgentCommunication?.MessageId == messageId));
        }
        internal ConversationInput FindOperation(ChatSession session, string operationId, string fingerprint = null)
        {
            return FindAccepted(session, operationId, fingerprint)?.Current;
        }
        // The live owner uses its projection; Office retries must not scan the
        // complete event stream on every submit.
        internal AcceptedInput FindAccepted(ChatSession session, string operationId, string fingerprint = null)
        {
            var box = Get(session);
            lock (box.Sync)
            {
                AcceptedInput first;
                if (!box.Accepted.TryGetValue(operationId, out first)) return null;
                var current = box.Inputs.FirstOrDefault(item => item.OperationId == operationId);
                return ProjectAccepted(first.Event, first.Initial, current, fingerprint, false);
            }
        }
        // Cross-process observers read the canonical stream. Receipt lookup is
        // read-only and must never reconcile an input or start a worker.
        internal AcceptedInput ReadAccepted(ChatSession session, string operationId,
            string fingerprint = null, bool requireRunId = false)
        {
            // Read-only receipts must use the same event prefix as the supplied
            // message projection. Another worker may append Applied after it loaded.
            var records = new ConversationInputJournal(_events).Read(session, false, session.Revision)
                .Where(record => record.Change.Input?.OperationId == operationId).ToArray();
            return records.Length == 0 ? null : ProjectAccepted(records[0].Event,
                records[0].Change.Input, records[records.Length - 1].Change.Input, fingerprint, requireRunId);
        }
        private static AcceptedInput ProjectAccepted(SessionEvent fact, ConversationInput initial,
            ConversationInput current, string fingerprint, bool requireRunId)
        {
            if (fact == null || initial == null || current == null || initial.Revision != 1 ||
                string.IsNullOrEmpty(initial.RequestFingerprint) ||
                requireRunId && string.IsNullOrEmpty(initial.AcceptedRunId))
                throw new InvalidOperationException("The accepted input receipt is unavailable; inspect the session without replaying input.");
            if (fingerprint != null) ConversationInputJournal.RequireSameRequest(initial, fingerprint);
            return new AcceptedInput { Event = fact, Initial = AcceptanceIdentity(initial), Current = Clone(current) };
        }
        internal SessionEvent Submit(ChatSession session, ConversationInput input)
        {
            var box = Get(session);
            lock (box.Sync) return SubmitLocked(box, input);
        }
        private SessionEvent SubmitLocked(Inbox box, ConversationInput input)
        {
            if (input == null || string.IsNullOrWhiteSpace(input.Id) ||
                string.IsNullOrEmpty(input.OperationId) || string.IsNullOrEmpty(input.RequestFingerprint))
                throw new ArgumentException("Input requires an ID, operation ID and request fingerprint.");
            var accepted = box.Inputs.FirstOrDefault(i => i.OperationId == input.OperationId);
            if (accepted != null)
            { ConversationInputJournal.RequireSameRequest(accepted, input.RequestFingerprint); return null; }
            if (box.Inputs.Any(i => i.Id == input.Id))
                throw new ArgumentException("An input ID already belongs to another operation.");
            if (input.QuestionSetId != null && (box.Running || box.Inputs.Any(i => IsPending(i) ||
                i.Status != ConversationInputStatus.Removed && i.AnswerRunId == input.AnswerRunId && i.QuestionSetId == input.QuestionSetId)))
                throw new InvalidOperationException("The questions already have accepted input or the conversation has moved on.");
            input.Revision = 1;
            // Pressing Send in an idle chat starts this new input. An empty queue's
            // old stop/error flag must not require a second confirmation. Retained
            // older inputs stay paused; fresh input does not authorize their replay.
            var idleInput = input.AgentCommunication?.Sender == null && !box.Running && PendingConfirmationId(box.Session) == null;
            var clearEmptyPause = idleInput && !box.Inputs.Any(IsPending);
            var fact = Write(box, input, clearEmptyPause ? (bool?)false : null);
            if (input.Delivery == InputDelivery.Steer || idleInput) box.ExplicitInputs.Add(input.Id);
            if (input.Delivery == InputDelivery.Steer && box.Accepting) box.Interrupt.Cancel();
            return fact;
        }
        internal void SubmitToRun(ChatSession session, string runId, ConversationInput input)
        {
            var box = Get(session);
            lock (box.Sync)
            {
                if (input == null || string.IsNullOrWhiteSpace(runId) || input.AcceptedRunId != runId ||
                    input.Delivery != InputDelivery.Steer ||
                    input.PendingId != null)
                    throw new ArgumentException("Steer requires its exact run target.", nameof(input));
                if (AlreadyAcceptedForTarget(box, input, runId, null)) return;
                if (!box.Running || !box.Accepting || box.Session.LastRun?.RunId != runId ||
                    (box.Session.LastRun.KernelState?.Summary.Lifecycle != RNAssistant.Core.Agent.RunLifecycle.Running && box.Session.LastRun.KernelState?.Summary.Lifecycle != RNAssistant.Core.Agent.RunLifecycle.Waiting))
                    throw new InvalidOperationException("Запуск уже завершает работу. Обновите чат и отправьте сообщение снова.");
                SubmitLocked(box, input);
            }
        }

        internal void SubmitQueuedToRun(ChatSession session, string runId, ConversationInput input)
        {
            var box = Get(session);
            lock (box.Sync)
            {
                if (input == null || input.Delivery != InputDelivery.Queue || input.AcceptedRunId != null || input.PendingId != null)
                    throw new ArgumentException("Queued input belongs to the conversation, not the current run.");
                if (!box.Running || !box.Accepting || box.Session.LastRun?.RunId != runId)
                    throw new InvalidOperationException("Запуск уже завершает работу. Обновите чат и отправьте сообщение снова.");
                SubmitLocked(box, input);
            }
        }

        internal void SubmitToPending(ChatSession session, string runId, string pendingId,
            ConversationInput input)
        {
            var box = Get(session);
            lock (box.Sync)
            {
                if (input == null || string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(pendingId) ||
                    input.AcceptedRunId != runId || input.PendingId != pendingId ||
                    input.Delivery != InputDelivery.Steer)
                    throw new ArgumentException("Steer requires its exact pending target.", nameof(input));
                if (AlreadyAcceptedForTarget(box, input, runId, pendingId)) return;
                if (box.Running || box.Worker ||
                    box.Session.LastRun?.RunId != runId ||
                    PendingConfirmationId(box.Session) != pendingId)
                    throw new InvalidOperationException("Ожидавшее действие уже изменилось. Обновите чат перед уточнением.");
                SubmitLocked(box, input);
            }
        }

        private static bool AlreadyAcceptedForTarget(Inbox box, ConversationInput input,
            string runId, string pendingId)
        {
            var accepted = box.Inputs.FirstOrDefault(i => i.OperationId == input.OperationId);
            if (accepted == null) return false;
            ConversationInputJournal.RequireSameRequest(accepted, input.RequestFingerprint);
            if (accepted.AcceptedRunId != runId || accepted.PendingId != pendingId ||
                accepted.Delivery != InputDelivery.Steer)
                throw new InvalidOperationException("The accepted input belongs to another target.");
            return true;
        }

        internal ConversationInput Update(ChatSession session, ConversationInputUpdate request, string action)
        {
            var box = Get(session);
            lock (box.Sync)
            {
                var input = Clone(box.Inputs.FirstOrDefault(i => i.Id == request.InputId));
                if (input?.AgentCommunication?.Sender != null && action != "remove")
                    throw new InvalidOperationException("Сообщение агента можно только удалить из очереди.");
                if (action == "edit" && input?.QuestionSetId != null)
                    throw new InvalidOperationException("Ответы на вопросы нельзя изменять как текст сообщения. Удалите ответ и отправьте новый.");
                if (input != null && input.Revision == request.ExpectedRevision + 1 &&
                    ((action == "remove" && input.Status == ConversationInputStatus.Removed) ||
                     (action == "steer" && input.Delivery == InputDelivery.Steer) ||
                     (action == "edit" && input.Status == ConversationInputStatus.Pending && input.Text == (request.Text ?? string.Empty)))) return input;
                if (input == null || (input.Status != ConversationInputStatus.Pending &&
                    !(action == "remove" && input.Status == ConversationInputStatus.Delivering && box.Paused && !box.Running && !box.Worker)) ||
                    input.Revision != request.ExpectedRevision)
                    throw new InvalidOperationException("Сообщение уже изменено или передано агенту. Обновите очередь.");
                if (action == "remove") input.Status = ConversationInputStatus.Removed;
                else if (action == "steer") input.Delivery = InputDelivery.Steer;
                else
                {
                    if (string.IsNullOrWhiteSpace(request.Text) && input.Attachments.Count == 0)
                        throw new InvalidOperationException("Сообщение не может быть пустым.");
                    input.Text = request.Text ?? string.Empty;
                }
                input.Revision++;
                Write(box, input);
                if (action == "steer") box.ExplicitInputs.Add(input.Id);
                if (action == "steer" && box.Accepting) box.Interrupt.Cancel();
                return input;
            }
        }
        internal void Pause(ChatSession session, string reason)
        {
            var box = Get(session);
            lock (box.Sync) PauseLocked(box, reason);
        }
        internal bool PauseForRun(ChatSession session, string runId, string reason = "Остановлено пользователем")
        {
            return PauseForRun(Get(session), runId, reason);
        }

        // A live Stop addresses the existing Inbox; it must not hydrate/replay history
        // or create a queue from a detached run-owner snapshot.
        internal bool PauseActiveRun(ConversationActiveSnapshot run, string reason = "Остановлено пользователем")
        {
            if (run == null) return false;
            Inbox box;
            lock (_sync)
                if (!_inboxes.TryGetValue(ScopeKey(run.Host, run.WorkspaceId, run.DocumentKey, run.ChatId), out box))
                    return false;
            return PauseForRun(box, run.RunId, reason);
        }

        private bool PauseForRun(Inbox box, string runId, string reason)
        {
            lock (box.Sync)
            {
                if (box.Session.LastRun?.RunId != runId) return false;
                PauseLocked(box, reason);
                return true;
            }
        }
        private void PauseLocked(Inbox box, string reason)
        {
            try { Write(box, null, true, reason); }
            finally
            {
                // A failed journal append must still stop scheduling in this process.
                // Recovery also pauses unconditionally, so no durable replay is authorized.
                box.Paused = true; box.PauseReason = reason; box.AgentPause = null;
                box.ExplicitInputs.Clear(); box.ViewRevision++;
                SignalInputChanged(box);
            }
        }
        internal string RunPauseId(ChatSession session)
        {
            var box = Get(session);
            lock (box.Sync) return box.Running && box.Accepting && box.AgentPause != null && box.AgentPause.RunId == session.LastRun?.RunId
                ? box.AgentPause.PauseId : null;
        }

        // The caller pins the exact live invocation and serializes control with this Inbox.
        internal AgentPauseStatus ControlPause(ChatSession session, string runId, string pauseId, bool resume, out bool changed,
            bool userControl = false)
        {
            var box = Get(session);
            lock (box.Sync)
            {
                changed = false;
                if (!box.Running || !box.Accepting || box.Session.LastRun?.RunId != runId) return AgentPauseStatus.Changed;
                if (box.AgentPause == null && box.Paused) return AgentPauseStatus.Protected;
                if (box.AgentPause?.RequiresUserResume == true && !userControl) return AgentPauseStatus.Protected;
                if (resume)
                {
                    if (box.AgentPause == null) return AgentPauseStatus.NoChange;
                    if (box.AgentPause.RunId != runId || box.AgentPause.PauseId != pauseId) return AgentPauseStatus.Changed;
                    Write(box, null, false, null, agentControl: true);
                    changed = true;
                    return AgentPauseStatus.Released;
                }
                if (box.AgentPause != null && (!userControl || box.AgentPause.PauseId == pauseId))
                    return box.Paused ? AgentPauseStatus.Paused : AgentPauseStatus.Requested;
                Write(box, null, agentPause: new ConversationAgentPause { RunId = runId, PauseId = pauseId,
                    RequiresUserResume = userControl }, agentControl: true);
                changed = true;
                return AgentPauseStatus.Requested;
            }
        }

        internal void AcknowledgePause(ChatSession session, string pauseId)
        {
            var box = Get(session);
            lock (box.Sync) AcknowledgePauseLocked(box, session, pauseId);
        }

        private void AcknowledgePauseLocked(Inbox box, ChatSession session, string pauseId)
        {
            if (box.Running && box.Accepting && box.AgentPause != null && box.AgentPause.RunId == session.LastRun?.RunId &&
                box.AgentPause.PauseId == pauseId && !box.Paused)
                Write(box, null, true, box.AgentPause.RequiresUserResume ? "Вы управляете браузером" : "Приостановлено агентом", box.AgentPause, true);
        }

        internal async Task WaitForPauseRequestAsync(ChatSession session, CancellationToken token)
        {
            var box = Get(session);
            var cancelled = NewInputSignal();
            using (token.Register(() => cancelled.TrySetResult(true)))
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    Task signal;
                    lock (box.Sync)
                    {
                        if (box.Running && box.Accepting && box.AgentPause != null &&
                            box.AgentPause.RunId == session.LastRun?.RunId) return;
                        signal = box.InputChanged.Task;
                    }
                    await Task.WhenAny(signal, cancelled.Task).ConfigureAwait(false);
                }
        }

        internal async Task WaitForResumeAsync(ChatSession session, string pauseId, CancellationToken token)
        {
            var box = Get(session);
            var cancelled = NewInputSignal();
            using (token.Register(() => cancelled.TrySetResult(true)))
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    Task signal;
                    lock (box.Sync)
                    {
                        if (!box.Running || !box.Accepting || box.Session.LastRun?.RunId != session.LastRun?.RunId)
                            throw new InvalidOperationException("The pause no longer belongs to this live invocation.");
                        if (box.AgentPause == null)
                        {
                            if (!box.Paused) return;
                            // User Stop/recovery supersedes peer control, including a failed append.
                        }
                        else if (box.AgentPause.PauseId != pauseId) return; // New pause is checked at the next boundary.
                        else AcknowledgePauseLocked(box, session, pauseId);
                        signal = box.InputChanged.Task;
                    }
                    await Task.WhenAny(signal, cancelled.Task).ConfigureAwait(false);
                }
        }

        internal void Resume(ChatSession session)
        {
            bool created;
            var box = Get(session, out created);
            lock (box.Sync)
            {
                if (!box.Running && !box.Worker)
                {
                    if (!created) RefreshForRecovery(box);
                    ReconcilePersistedDelivery(box, false);
                }
                foreach (var item in box.Inputs.Where(i => !box.Running && !box.Worker && i.Status == ConversationInputStatus.Delivering).ToArray())
                {
                    var copy = Clone(item);
                    copy.Status = ConversationInputStatus.Pending;
                    copy.Revision++;
                    Write(box, copy);
                }
                Write(box, null, false, null);
            }
        }
        // Recovery owns both the durable transition and the live receipt projection.
        // The caller has already fenced the conversation with its exact run lease.
        internal void ReconcileAcceptedDelivery(ChatSession session)
        {
            bool created;
            var box = Get(session, out created);
            lock (box.Sync)
            {
                if (box.Running || box.Worker)
                    throw new InvalidOperationException("Accepted input cannot be reconciled while its run is active.");
                if (!created) RefreshForRecovery(box);
                ReconcilePersistedDelivery(box, true);
            }
        }
        private void RefreshForRecovery(Inbox box)
        {
            foreach (var record in ReadInboxReplay(box.Session, box.Revision))
            {
                Apply(box, record.Change, record.Event);
                box.Revision = record.Event.Sequence;
                box.ViewRevision++;
            }
            box.AgentPause = null;
            // A recovered claim is never a license to dispatch another run.
            if (box.Inputs.Any(IsPending) || HasPendingWait(box.Session))
            {
                box.Paused = true;
                box.PauseReason = "После перезапуска";
            }
        }
        private IReadOnlyList<ConversationInputRecord> ReadInboxReplay(ChatSession session, long afterSequence)
        {
            // Validate every transition, but hydrate only the latest version of each
            // input. Claim/applied/edit events otherwise reread the same CAS body.
            var records = new ConversationInputJournal(_events).Read(session, false)
                .Where(record => record.Event.Sequence > afterSequence).ToArray();
            foreach (var latest in records.Where(record => record.Change.Input != null)
                .GroupBy(record => record.Change.Input.Id, StringComparer.Ordinal).Select(group => group.Last()))
                latest.Change.Input.Text = _events.ReadPayload(ConversationMetadata.From(session), latest.Event) ??
                    throw new InvalidOperationException("Conversation input body is missing.");
            // Complete all required reads before changing an existing recovery projection.
            return records;
        }
        private void ReconcilePersistedDelivery(Inbox box, bool requireAcceptedRun)
        {
            var delivering = box.Inputs.Where(item => item.Status == ConversationInputStatus.Delivering).ToArray();
            if (requireAcceptedRun && delivering.Any(item =>
                (item.Delivery == InputDelivery.Steer ? item.Revision < 2 : item.Revision != 1) ||
                string.IsNullOrWhiteSpace(item.AcceptedRunId) || string.IsNullOrWhiteSpace(item.RequestFingerprint)))
                throw new InvalidOperationException("The accepted input receipt is unavailable; inspect the session without replaying input.");
            var persisted = delivering.Where(item =>
                ConversationInputJournal.HasPersistedUserMessage(box.Session, item)).ToArray();
            var clearRecoveryPause = requireAcceptedRun && persisted.Length > 0 &&
                persisted.Length == delivering.Length && !box.Inputs.Any(item => item.Status == ConversationInputStatus.Pending) &&
                box.PauseReason == "После перезапуска";
            for (var index = 0; index < persisted.Length; index++)
            {
                var copy = Clone(persisted[index]);
                copy.Status = ConversationInputStatus.Applied;
                copy.Revision++;
                Write(box, copy, clearRecoveryPause && index == persisted.Length - 1 ? (bool?)false : null);
            }
        }
        internal bool HasRunInput(ChatSession session)
        {
            var box = Get(session);
            lock (box.Sync)
            {
                // Queried by the kernel between requests/tool calls, never by
                // the running tool. Peer intake does not cancel InterruptToken.
                ReadyQueuedAgentsLocked(box);
                return HasRunInputLocked(box);
            }
        }
        private static bool HasRunInputLocked(Inbox box)
        {
            return box.Accepting && box.Inputs.Any(i => IsRunInput(box, i));
        }
        private static bool IsRunInput(Inbox box, ConversationInput input)
        { return AppliesToRun(box, input) && input.Status == ConversationInputStatus.Pending &&
            (input.Delivery == InputDelivery.Steer || box.ReadyAgentInputs.Contains(input.Id)) &&
            (!box.Paused || box.ExplicitInputs.Contains(input.Id)); }
        internal CancellationToken InterruptToken(ChatSession session)
        {
            var box = Get(session);
            lock (box.Sync) return box.Interrupt.Token;
        }
        internal bool TryClose(ChatSession session)
        {
            var box = Get(session);
            lock (box.Sync)
            {
                ReadyQueuedAgentsLocked(box);
                if (HasRunInputLocked(box) || box.AgentPause != null) return false;
                box.Accepting = false;
                return true;
            }
        }
        internal List<ConversationInput> TakeRunInput(ChatSession session)
        {
            var box = Get(session);
            lock (box.Sync)
            {
                // Peer inputs are sampled only at a kernel boundary. They never
                // cancel model inference or an in-flight tool call.
                ReadyQueuedAgentsLocked(box);
                var pending = box.Accepting ? box.Inputs.Where(i => IsRunInput(box, i)).Select(Clone).ToList() : new List<ConversationInput>();
                foreach (var input in pending)
                {
                    input.Status = ConversationInputStatus.Delivering;
                    input.Revision++;
                    Write(box, input);
                    box.ReadyAgentInputs.Remove(input.Id);
                }
                box.Interrupt.Dispose();
                box.Interrupt = new CancellationTokenSource();
                return pending;
            }
        }

        private static void ReadyQueuedAgentsLocked(Inbox box)
        {
            if (!box.Running || !box.Accepting || box.Paused || PendingConfirmationId(box.Session) != null) return;
            foreach (var input in PendingQueuedInputs(box).TakeWhile(i => CanIncludePeerInput(box, i)))
                box.ReadyAgentInputs.Add(input.Id);
        }
        private static bool CanIncludePeerInput(Inbox box, ConversationInput input)
        { return box.Session.LastRun?.ConversationPolicy?.CanIncludePeerInput(input.AgentCommunication) == true; }
        private static IEnumerable<ConversationInput> PendingQueuedInputs(Inbox box)
        { return box.Inputs.Where(i => i.Status == ConversationInputStatus.Pending &&
            i.AcceptedRunId == null && i.Delivery == InputDelivery.Queue); }

        internal async Task<AgentWaitResult> WaitForAgentsAsync(ChatSession session, string runId,
            DateTime deadlineUtc, CancellationToken token, IReadOnlyDictionary<string, AgentChatAddress> requests = null,
            bool all = false, bool completion = false)
        {
            var box = Get(session);
            lock (box.Sync)
            {
                RequireAgentWaitRun(box, runId);
                box.AgentWaiting = true;
                box.ViewRevision++;
            }
            using (var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var remaining = deadlineUtc - DateTime.UtcNow;
                var timeout = Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, delayCancellation.Token);
                try
                {
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        Task changed;
                        lock (box.Sync)
                        {
                            RequireAgentWaitRun(box, runId);
                            if (box.Inputs.Any(i => IsRunInput(box, i) && i.AgentCommunication?.Sender == null))
                                return new AgentWaitResult { Status = AgentWaitStatus.InputPending };
                            var pending = PendingQueuedInputs(box).ToArray();
                            // Keep FIFO: a queued human request is not skipped to reach later peers.
                            var ready = pending.TakeWhile(i => CanIncludePeerInput(box, i)).ToArray();
                            if (requests == null)
                            {
                                if (ready.Any(input => input.CreatedUtc <= deadlineUtc)) return CompleteAgentWait(box, AgentWaitStatus.MessagesReady, ready);
                                if (pending.Length > ready.Length) return new AgentWaitResult { Status = AgentWaitStatus.InputPending };
                                if (timeout.IsCompleted || DateTime.UtcNow >= deadlineUtc) return CompleteAgentWait(box, AgentWaitStatus.TimedOut, ready);
                            }
                            else
                            {
                                // Applied input is retained in the canonical Inbox journal. A
                                // reply that arrived before this wait cannot be missed. The
                                // state check and signal capture below share the intake lock.
                                var readyIds = new HashSet<string>(ready.Select(input => input.Id), StringComparer.Ordinal);
                                var matches = box.Inputs.Where(input => input.Status == ConversationInputStatus.Applied || readyIds.Contains(input.Id))
                                    .Where(input => input.CreatedUtc <= deadlineUtc && MatchesReply(input, requests, completion))
                                    .GroupBy(input => input.AgentCommunication.ReplyTo, StringComparer.Ordinal)
                                    .Select(group => group.First()).Select(input => new AgentReplyMatch {
                                        RequestId = input.AgentCommunication.ReplyTo, MessageId = input.AgentCommunication.MessageId,
                                        AgentId = input.AgentCommunication.Sender.AgentId,
                                        ReportedOutcome = input.AgentCommunication.Resolution }).ToArray();
                                if (pending.Length > 0 && ready.Length == 0)
                                    return CompleteAgentWait(box, AgentWaitStatus.InputPending, ready, matches);
                                if (matches.Length > 0 && (!all || matches.Length == requests.Count))
                                    return CompleteAgentWait(box, completion ? AgentWaitStatus.ResolutionsReady : AgentWaitStatus.RepliesReady, ready, matches);
                                // Questions from peers must remain actionable while waiting;
                                // unrelated input is not evidence that the requested reply arrived.
                                if (ready.Any(input => !MatchesReply(input, requests, completion)) || pending.Length > ready.Length)
                                    return CompleteAgentWait(box, AgentWaitStatus.InputPending, ready, matches);
                                if (timeout.IsCompleted || DateTime.UtcNow >= deadlineUtc)
                                    return CompleteAgentWait(box, AgentWaitStatus.TimedOut, ready, matches);
                            }
                            changed = box.InputChanged.Task;
                        }
                        await Task.WhenAny(changed, timeout).ConfigureAwait(false);
                    }
                }
                finally
                {
                    delayCancellation.Cancel();
                    lock (box.Sync)
                    {
                        box.AgentWaiting = false;
                        box.ViewRevision++;
                    }
                }
            }
        }

        private static bool MatchesReply(ConversationInput input, IReadOnlyDictionary<string, AgentChatAddress> requests, bool completion)
        {
            var message = input.AgentCommunication;
            AgentChatAddress target;
            return message?.ReplyTo != null && message.MessageId != null && message.Sender != null &&
                (!completion || message.Resolution.HasValue && Enum.IsDefined(typeof(AgentAssignmentOutcome), message.Resolution.Value)) &&
                requests.TryGetValue(message.ReplyTo, out target) && target.ChatId == message.Sender.ChatId &&
                target.Source == message.Sender.Source && target.WorkspaceId == message.Sender.WorkspaceId;
        }

        private static AgentWaitResult CompleteAgentWait(Inbox box, AgentWaitStatus status, IEnumerable<ConversationInput> ready,
            AgentReplyMatch[] matches = null)
        {
            foreach (var input in ready) box.ReadyAgentInputs.Add(input.Id);
            return new AgentWaitResult { Status = status, Matches = matches ?? new AgentReplyMatch[0] };
        }

        private static void RequireAgentWaitRun(Inbox box, string runId)
        {
            if (string.IsNullOrWhiteSpace(runId) || !box.Running || !box.Accepting || box.Paused ||
                box.Session.LastRun?.RunId != runId)
                throw new AgentCommunicationException("agent_wait_unavailable", "Ожидание доступно только в текущем активном запуске без паузы.");
        }
        private static TaskCompletionSource<bool> NewInputSignal()
        { return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); }
        private static void SignalInputChanged(Inbox box)
        {
            var previous = box.InputChanged;
            box.InputChanged = NewInputSignal();
            previous.TrySetResult(true);
        }
        internal void Applied(ChatSession session, string id)
        {
            var box = Get(session);
            lock (box.Sync)
            {
                var item = Clone(box.Inputs.FirstOrDefault(i => i.Id == id));
                if (item == null) return;
                if (!ConversationInputJournal.HasPersistedUserMessage(box.Session, item))
                    throw new InvalidOperationException("Input cannot be marked applied before its user message is persisted.");
                if (item.Status == ConversationInputStatus.Applied) return;
                item.Status = ConversationInputStatus.Applied; item.Revision++;
                Write(box, item);
            }
        }
        internal bool TryStartWorker(ChatSession session)
        {
            var box = Get(session);
            lock (box.Sync)
            {
                if (box.Worker || box.Running || !HasRunnableLocked(box, box.Session)) return false;
                box.Worker = true;
                box.ViewRevision++;
                return true;
            }
        }
        internal ConversationInput TakeNext(ChatSession session)
        {
            var box = Get(session);
            lock (box.Sync)
            {
                var awaiting = PendingConfirmationId(session) != null;
                var next = box.Inputs.FirstOrDefault(i => i.Status == ConversationInputStatus.Pending &&
                    i.Delivery == InputDelivery.Steer && CanSchedule(session, i, awaiting) &&
                    (!box.Paused || box.ExplicitInputs.Contains(i.Id))) ?? (awaiting ? null : box.Inputs.FirstOrDefault(i =>
                        i.AcceptedRunId == null && i.Status == ConversationInputStatus.Pending &&
                        (!box.Paused || box.ExplicitInputs.Contains(i.Id))));
                if (next == null) return null;
                // A waiting approval is resumed through the same kernel continuation.
                // Its steer remains pending for that continuation to consume.
                if (awaiting) return Clone(next);
                next = Clone(next); next.Status = ConversationInputStatus.Delivering; next.Revision++;
                Write(box, next);
                return next;
            }
        }
        internal void BindQueuedTurn(ChatSession session, string inputId)
        {
            var box = Get(session);
            lock (box.Sync)
            {
                if (!box.Worker || box.Running || !box.Inputs.Any(item => item.Id == inputId &&
                    item.Status == ConversationInputStatus.Delivering))
                    throw new InvalidOperationException("The queue no longer owns this input.");
                box.Session = session;
            }
        }
        internal void EnsureCanDispatch(ChatSession session, string inputId)
        {
            var box = Get(session);
            lock (box.Sync)
                if (box.Paused && !box.ExplicitInputs.Contains(inputId)) throw new OperationCanceledException("Очередь приостановлена.");
        }
        internal bool HasRunnable(ChatSession session)
        {
            var box = Get(session);
            lock (box.Sync) return HasRunnableLocked(box, box.Session);
        }
        private static bool HasRunnableLocked(Inbox box, ChatSession session)
        {
            if (HasPendingWait(session)) return !box.Paused;
            var awaiting = PendingConfirmationId(session) != null;
            return box.Inputs.Any(i => i.Status == ConversationInputStatus.Pending &&
                CanSchedule(session, i, awaiting) && (!awaiting || i.Delivery == InputDelivery.Steer) &&
                (!box.Paused || box.ExplicitInputs.Contains(i.Id)));
        }
        private static bool CanSchedule(ChatSession session, ConversationInput input, bool awaiting)
        {
            return input.AcceptedRunId == null || awaiting && input.Delivery == InputDelivery.Steer &&
                input.AcceptedRunId == session.LastRun?.RunId &&
                (input.PendingId == null || input.PendingId == PendingConfirmationId(session));
        }
        internal static RNAssistant.Application.Contracts.AgentWaitResumeTarget SavedWaitTarget(ChatSession session)
        {
            return HasPendingWait(session) ? new RNAssistant.Application.Contracts.AgentWaitResumeTarget {
                SessionId = session.Id, RunId = session.LastRun.RunId, Revision = session.Revision } : null;
        }

        internal static bool HasPendingWait(ChatSession session)
        { return session?.LastRun?.KernelState?.Summary?.Lifecycle == RNAssistant.Core.Agent.RunLifecycle.Waiting; }

        internal static string PendingConfirmationId(ChatSession session)
        {
            var summary = session?.LastRun?.KernelState?.Summary;
            return summary?.Lifecycle == RNAssistant.Core.Agent.RunLifecycle.AwaitingConfirmation
                ? summary.PendingConfirmation?.PendingId : null;
        }
        internal void EndWorker(ChatSession session)
        {
            var box = Get(session);
            lock (box.Sync) { box.Worker = false; box.ViewRevision++; }
        }
        private SessionEvent Write(Inbox box, ConversationInput input, bool? paused = null, string reason = null,
            ConversationAgentPause agentPause = null, bool agentControl = false)
        {
            var change = new ConversationInputEvent { Input = input, Paused = paused ?? box.Paused,
                PauseReason = paused.HasValue ? reason : box.PauseReason,
                AgentPause = agentControl ? agentPause : paused.HasValue ? null : box.AgentPause };
            var fact = new ConversationInputJournal(_events).Append(box.Session, change);
            Apply(box, change, fact);
            box.Revision = fact.Sequence;
            box.ViewRevision++;
            SignalInputChanged(box);
            return fact;
        }
        private static void Apply(Inbox box, ConversationInputEvent change, SessionEvent fact)
        {
            if (change.Input != null)
            {
                if (!box.Accepted.ContainsKey(change.Input.OperationId))
                    box.Accepted.Add(change.Input.OperationId, new AcceptedInput
                    { Event = fact, Initial = AcceptanceIdentity(change.Input) });
                var index = box.Inputs.FindIndex(i => i.Id == change.Input.Id);
                if (index < 0) box.Inputs.Add(Clone(change.Input)); else box.Inputs[index] = Clone(change.Input);
            }
            box.Paused = change.Paused; box.PauseReason = change.PauseReason; box.AgentPause = change.AgentPause;
        }
        private static bool AppliesToRun(Inbox box, ConversationInput input)
        { return input.AcceptedRunId == null || input.AcceptedRunId == box.Session.LastRun?.RunId; }

        private static bool IsPending(ConversationInput input)
        { return input.Status == ConversationInputStatus.Pending || input.Status == ConversationInputStatus.Delivering; }
        private static ConversationInput Clone(ConversationInput input)
        { return input == null ? null : JsonConvert.DeserializeObject<ConversationInput>(JsonConvert.SerializeObject(input)); }
        private static ConversationInput AcceptanceIdentity(ConversationInput input)
        {
            var copy = Clone(input);
            if (copy != null) { copy.Text = null; copy.Attachments = new List<ChatAttachment>(); }
            return copy;
        }
    }
}
