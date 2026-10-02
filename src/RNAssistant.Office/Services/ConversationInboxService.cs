using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    // Owns intake and its durable transitions. Never edits the run's message list.
    // ChatStore serializes appends and advances the SAME live session's storage cursor.
    internal sealed class ConversationInboxService
    {
        internal sealed class Inbox
        {
            internal readonly object Sync = new object();
            internal readonly HashSet<string> ExplicitSteers = new HashSet<string>(StringComparer.Ordinal);
            internal readonly List<ConversationInput> Inputs = new List<ConversationInput>();
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

        internal Inbox Get(ChatSession session)
        {
            Inbox box;
            lock (_sync)
            {
                if (!_inboxes.TryGetValue(session.Id, out box))
                {
                    box = new Inbox { Session = session };
                    foreach (var fact in _events.Read(session, SessionEventReadMode.RequireComplete)
                        .Where(e => e.Type == SessionEventTypes.ConversationInputChanged))
                    {
                        var change = fact.Data.ToObject<ConversationInputEvent>();
                        if (change == null) throw new InvalidOperationException("Invalid conversation input event.");
                        if (change.Input != null)
                        {
                            change.Input.Text = _events.ReadPayload(session, fact);
                            if (change.Input.Text == null) throw new InvalidOperationException("Conversation input body is missing.");
                        }
                        Apply(box, change);
                        box.Revision = fact.Sequence;
                        box.ViewRevision++;
                    }
                    if (box.Inputs.Any(IsPending)) { box.Paused = true; box.PauseReason = "После перезапуска"; }
                    _inboxes.Add(session.Id, box);
                }
            }
            lock (box.Sync)
                if (!box.Running && !box.Worker && session.Revision >= box.Session.Revision) box.Session = session;
            return box;
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
        internal ChatInboxResponse Snapshot(ChatSession session)
        {
            var box = Get(session);
            lock (box.Sync) return new ChatInboxResponse
            {
                ChatId = session.Id, Epoch = _epoch, Revision = box.ViewRevision, Phase = box.Session.LastRun == null ? null : box.Session.LastRun.Phase, Running = box.Running || box.Worker,
                Paused = box.Paused, PauseReason = box.PauseReason,
                Items = box.Inputs.Where(IsPending).Select(Clone).ToList()
            };
        }
        internal ConversationInput Find(ChatSession session, string id)
        {
            var box = Get(session);
            lock (box.Sync) return Clone(box.Inputs.FirstOrDefault(i => i.Id == id));
        }
        internal ConversationInput FindOperation(ChatSession session, string operationId)
        {
            var box = Get(session);
            lock (box.Sync) return Clone(box.Inputs.FirstOrDefault(i => i.OperationId == operationId));
        }
        internal void Submit(ChatSession session, ConversationInput input)
        {
            var box = Get(session);
            lock (box.Sync)
            {
                if (box.Inputs.Any(i => i.Id == input.Id || (!string.IsNullOrEmpty(input.OperationId) && i.OperationId == input.OperationId))) return;
                input.Revision = 1;
                Write(box, input);
                if (input.Delivery == InputDelivery.Steer) box.ExplicitSteers.Add(input.Id);
                if (input.Delivery == InputDelivery.Steer && box.Accepting) box.Interrupt.Cancel();
            }
        }
        internal void Update(ChatSession session, UpdateChatInputPayload request, string action)
        {
            var box = Get(session);
            lock (box.Sync)
            {
                var input = Clone(box.Inputs.FirstOrDefault(i => i.Id == request.InputId));
                if (input != null && input.Revision == request.ExpectedRevision + 1 &&
                    ((action == "remove" && input.Status == ConversationInputStatus.Removed) ||
                     (action == "steer" && input.Delivery == InputDelivery.Steer) ||
                     (action == "edit" && input.Status == ConversationInputStatus.Pending && input.Text == (request.Text ?? string.Empty)))) return;
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
            var box = Get(session);
            lock (box.Sync)
            {
                foreach (var item in box.Inputs.Where(i => !box.Running && !box.Worker && i.Status == ConversationInputStatus.Delivering).ToArray())
                {
                    var copy = Clone(item);
                    copy.Status = box.Session.Messages.Any(m => m.Id == item.Id)
                        ? ConversationInputStatus.Applied : ConversationInputStatus.Pending;
                    copy.Revision++;
                    Write(box, copy);
                }
                Write(box, null, false, null);
            }
        }
        internal bool HasSteer(ChatSession session)
        {
            var box = Get(session);
            lock (box.Sync) return box.Accepting && box.Inputs.Any(i => i.Status == ConversationInputStatus.Pending && i.Delivery == InputDelivery.Steer &&
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
                var pending = box.Accepting ? box.Inputs.Where(i => i.Status == ConversationInputStatus.Pending &&
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
                if (item == null || item.Status == ConversationInputStatus.Applied) return;
                item.Status = ConversationInputStatus.Applied; item.Revision++;
                Write(box, item);
            }
        }
        internal bool TryStartWorker(ChatSession session)
        {
            var box = Get(session);
            lock (box.Sync)
            {
                if (box.Worker || box.Running || !box.Inputs.Any(i => i.Status == ConversationInputStatus.Pending &&
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
                var next = box.Inputs.FirstOrDefault(i => i.Status == ConversationInputStatus.Pending &&
                    i.Delivery == InputDelivery.Steer && (!box.Paused || box.ExplicitSteers.Contains(i.Id))) ?? (awaiting || box.Paused ? null : box.Inputs.FirstOrDefault(i =>
                        i.Status == ConversationInputStatus.Pending));
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
            lock (box.Sync) return box.Inputs.Any(i => i.Status == ConversationInputStatus.Pending &&
                (!awaiting || i.Delivery == InputDelivery.Steer) && (!box.Paused || box.ExplicitSteers.Contains(i.Id)));
        }
        internal void EndWorker(ChatSession session)
        {
            var box = Get(session);
            lock (box.Sync) { box.Worker = false; box.ViewRevision++; }
        }
        private void Write(Inbox box, ConversationInput input, bool? paused = null, string reason = null)
        {
            var change = new ConversationInputEvent { Input = input, Paused = paused ?? box.Paused,
                PauseReason = paused.HasValue ? reason : box.PauseReason };
            var metadata = new ConversationInputEvent { Input = Clone(input), Paused = change.Paused, PauseReason = change.PauseReason };
            if (metadata.Input != null) metadata.Input.Text = null;
            var body = input == null ? null : SessionEventPayload.FromText(input.Text ?? string.Empty, "text/plain; charset=utf-8");
            var fact = _events.Append(box.Session, new SessionEventWrite(
                SessionEventDescriptors.For(SessionEventKind.ConversationInputChanged), metadata, body, null));
            Apply(box, change);
            box.Revision = fact.Sequence;
            box.ViewRevision++;
        }
        private static void Apply(Inbox box, ConversationInputEvent change)
        {
            if (change.Input != null)
            {
                var index = box.Inputs.FindIndex(i => i.Id == change.Input.Id);
                if (index < 0) box.Inputs.Add(Clone(change.Input)); else box.Inputs[index] = Clone(change.Input);
            }
            box.Paused = change.Paused; box.PauseReason = change.PauseReason;
        }
        private static bool IsPending(ConversationInput input)
        { return input.Status == ConversationInputStatus.Pending || input.Status == ConversationInputStatus.Delivering; }
        private static ConversationInput Clone(ConversationInput input)
        { return input == null ? null : JsonConvert.DeserializeObject<ConversationInput>(JsonConvert.SerializeObject(input)); }
    }
}
