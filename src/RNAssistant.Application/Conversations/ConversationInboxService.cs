using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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
            internal readonly HashSet<string> ExplicitSteers = new HashSet<string>(StringComparer.Ordinal);
            internal readonly List<ConversationInput> Inputs = new List<ConversationInput>();
            internal readonly Dictionary<string, AcceptedInput> Accepted =
                new Dictionary<string, AcceptedInput>(StringComparer.Ordinal);
            internal ChatSession Session;
            internal CancellationTokenSource Interrupt = new CancellationTokenSource();
            internal bool Running, Accepting, Worker, Paused;
            internal string PauseReason;
            internal long Revision, ViewRevision;
        }
        private readonly object _sync = new object();
        private readonly Dictionary<string, Inbox> _inboxes = new Dictionary<string, Inbox>(StringComparer.Ordinal);
        private readonly IEventStore _events;
        private readonly string _epoch = Guid.NewGuid().ToString("N");
        internal ConversationInboxService(IEventStore events) { _events = events; }

        internal bool HasActiveWork(string chatId)
        {
            List<Inbox> candidates;
            lock (_sync) candidates = _inboxes.Values.Where(box => box.Session.Id == chatId).ToList();
            foreach (var box in candidates)
                lock (box.Sync) if (box.Worker || box.Running) return true;
            return false;
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
                    foreach (var record in new ConversationInputJournal(_events).Read(session))
                    {
                        Apply(box, record.Change, record.Event);
                        box.Revision = record.Event.Sequence;
                        box.ViewRevision++;
                    }
                    if (box.Inputs.Any(IsPending)) { box.Paused = true; box.PauseReason = "После перезапуска"; }
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
            return (session.Host ?? string.Empty) + "\0" + (session.WorkspaceId ?? string.Empty) +
                "\0" + (session.DocumentKey ?? string.Empty) + "\0" + session.Id;
        }

        internal void Bind(ChatSession session)
        {
            var box = Get(session);
            lock (box.Sync)
            {
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
                box.ViewRevision++;
                if (!completed && !awaiting) PauseLocked(box, "Работа остановлена или завершилась с ошибкой");
            }
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
                Paused = box.Paused, PauseReason = box.PauseReason,
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
            var latest = new ConversationInputJournal(_events).Read(session, false)
                .Where(record => record.Change.Input != null)
                .GroupBy(record => record.Change.Input.Id, StringComparer.Ordinal)
                .Select(group => group.Last()).ToArray();
            var byId = latest.ToDictionary(record => record.Change.Input.Id, StringComparer.Ordinal);
            return Undelivered(session, latest.Select(record => record.Change.Input), delivery)
                .Select(input =>
                {
                    var record = byId[input.Id];
                    var copy = Clone(input);
                    copy.Text = _events.ReadPayload(session, record.Event) ??
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
            var records = new ConversationInputJournal(_events).Read(session, false)
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
            lock (box.Sync)
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
                var fact = Write(box, input);
                if (input.Delivery == InputDelivery.Steer) box.ExplicitSteers.Add(input.Id);
                if (input.Delivery == InputDelivery.Steer && box.Accepting) box.Interrupt.Cancel();
                return fact;
            }
        }
        internal void SubmitToRun(ChatSession session, string runId, ConversationInput input)
        {
            var box = Get(session);
            lock (box.Sync)
            {
                if (input.AcceptedRunId != runId || input.Delivery != InputDelivery.Steer ||
                    !box.Running || !box.Accepting || box.Session.LastRun?.RunId != runId ||
                    box.Session.LastRun.KernelState?.Summary.Lifecycle != RNAssistant.Core.Agent.RunLifecycle.Running)
                    throw new InvalidOperationException("Запуск уже завершает работу. Обновите чат и отправьте сообщение снова.");
                Submit(session, input);
            }
        }

        internal ConversationInput Update(ChatSession session, ConversationInputUpdate request, string action)
        {
            var box = Get(session);
            lock (box.Sync)
            {
                var input = Clone(box.Inputs.FirstOrDefault(i => i.Id == request.InputId));
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
                if (action == "steer") box.ExplicitSteers.Add(input.Id);
                if (action == "steer" && box.Accepting) box.Interrupt.Cancel();
                return input;
            }
        }
        internal void Pause(ChatSession session, string reason)
        {
            var box = Get(session);
            lock (box.Sync) PauseLocked(box, reason);
        }
        private void PauseLocked(Inbox box, string reason)
        {
            try { Write(box, null, true, reason); }
            finally
            {
                // A failed journal append must still stop scheduling in this process.
                // Recovery also pauses unconditionally, so no durable replay is authorized.
                box.Paused = true; box.PauseReason = reason;
                box.ExplicitSteers.Clear(); box.ViewRevision++;
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
            foreach (var record in new ConversationInputJournal(_events).Read(box.Session))
            {
                if (record.Event.Sequence <= box.Revision) continue;
                Apply(box, record.Change, record.Event);
                box.Revision = record.Event.Sequence;
                box.ViewRevision++;
            }
            // A recovered claim is never a license to dispatch another run.
            if (box.Inputs.Any(IsPending))
            {
                box.Paused = true;
                box.PauseReason = "После перезапуска";
            }
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
        internal bool HasSteer(ChatSession session)
        {
            var box = Get(session);
            lock (box.Sync) return box.Accepting && box.Inputs.Any(i => AppliesToRun(box, i) && i.Status == ConversationInputStatus.Pending && i.Delivery == InputDelivery.Steer &&
                (!box.Paused || box.ExplicitSteers.Contains(i.Id)));
        }
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
                if (HasSteer(session)) return false;
                box.Accepting = false;
                return true;
            }
        }
        internal List<ConversationInput> TakeSteer(ChatSession session)
        {
            var box = Get(session);
            lock (box.Sync)
            {
                var pending = box.Accepting ? box.Inputs.Where(i => AppliesToRun(box, i) && i.Status == ConversationInputStatus.Pending &&
                    i.Delivery == InputDelivery.Steer && (!box.Paused || box.ExplicitSteers.Contains(i.Id))).Select(Clone).ToList() : new List<ConversationInput>();
                foreach (var input in pending)
                {
                    input.Status = ConversationInputStatus.Delivering;
                    input.Revision++;
                    Write(box, input);
                }
                box.Interrupt.Dispose();
                box.Interrupt = new CancellationTokenSource();
                return pending;
            }
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
                if (box.Worker || box.Running || !box.Inputs.Any(i => i.AcceptedRunId == null && i.Status == ConversationInputStatus.Pending &&
                    (!box.Paused || box.ExplicitSteers.Contains(i.Id)))) return false;
                box.Worker = true;
                box.ViewRevision++;
                return true;
            }
        }
        internal ConversationInput TakeNext(ChatSession session, bool awaiting)
        {
            var box = Get(session);
            lock (box.Sync)
            {
                var next = box.Inputs.FirstOrDefault(i => i.AcceptedRunId == null && i.Status == ConversationInputStatus.Pending &&
                    i.Delivery == InputDelivery.Steer && (!box.Paused || box.ExplicitSteers.Contains(i.Id))) ?? (awaiting || box.Paused ? null : box.Inputs.FirstOrDefault(i =>
                        i.AcceptedRunId == null && i.Status == ConversationInputStatus.Pending));
                if (next == null) return null;
                // A waiting approval is resumed through the same kernel continuation.
                // Its steer remains pending for that continuation to consume.
                if (awaiting) return Clone(next);
                next = Clone(next); next.Status = ConversationInputStatus.Delivering; next.Revision++;
                Write(box, next);
                return next;
            }
        }
        internal void EnsureCanDispatch(ChatSession session, string inputId)
        {
            var box = Get(session);
            lock (box.Sync)
                if (box.Paused && !box.ExplicitSteers.Contains(inputId)) throw new OperationCanceledException("Очередь приостановлена.");
        }
        internal bool HasRunnable(ChatSession session, bool awaiting)
        {
            var box = Get(session);
            lock (box.Sync) return box.Inputs.Any(i => i.AcceptedRunId == null && i.Status == ConversationInputStatus.Pending &&
                (!awaiting || i.Delivery == InputDelivery.Steer) && (!box.Paused || box.ExplicitSteers.Contains(i.Id)));
        }
        internal void EndWorker(ChatSession session)
        {
            var box = Get(session);
            lock (box.Sync) { box.Worker = false; box.ViewRevision++; }
        }
        private SessionEvent Write(Inbox box, ConversationInput input, bool? paused = null, string reason = null)
        {
            var change = new ConversationInputEvent { Input = input, Paused = paused ?? box.Paused,
                PauseReason = paused.HasValue ? reason : box.PauseReason };
            var fact = new ConversationInputJournal(_events).Append(box.Session, change);
            Apply(box, change, fact);
            box.Revision = fact.Sequence;
            box.ViewRevision++;
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
            box.Paused = change.Paused; box.PauseReason = change.PauseReason;
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
