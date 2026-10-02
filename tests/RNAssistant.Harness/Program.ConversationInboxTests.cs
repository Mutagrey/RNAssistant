using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private sealed class KernelInputFake : IRunInputChannel
        {
            private CancellationTokenSource _signal = new CancellationTokenSource();
            private readonly List<AgentMessage> _messages = new List<AgentMessage>();
            internal Action OnClose;
            public CancellationToken InterruptToken { get { return _signal.Token; } }
            public bool HasPendingInput { get { return _messages.Count != 0; } }
            internal void Add(string text) { _messages.Add(AgentMessage.User(text)); _signal.Cancel(); }
            public Task<IReadOnlyList<AgentMessage>> TakeInputAsync(CancellationToken token)
            {
                var messages = _messages.ToArray(); _messages.Clear();
                _signal.Dispose(); _signal = new CancellationTokenSource();
                return Task.FromResult<IReadOnlyList<AgentMessage>>(messages);
            }
            public bool TryCloseInput()
            {
                var close = OnClose; OnClose = null;
                if (close != null) close();
                return !HasPendingInput;
            }
        }
        private static async Task InboxSteerCancelsOnlyModel()
        {
            var f = new KernelFixture(); var input = new KernelInputFake();
            f.Model.OnSend = (request, token) =>
            {
                if (f.Model.Requests.Count == 1)
                {
                    input.Add("Use the new requirements");
                    AssertTrue(token.IsCancellationRequested, "model request is interrupted");
                    throw new OperationCanceledException(token);
                }
                return Task.FromResult(AgentModelResult.Accepted(KernelResponse()));
            };
            var result = await new AgentKernel(f.Model, f.Tools, f.Store, input: input).RunAsync(
                new AgentRunRequest("run", "turn", "original", new AgentRunLimits(5, 5)), CancellationToken.None);
            AssertEqual(RunLifecycle.Completed, result.Summary.Lifecycle, "steer does not cancel the run");
            AssertEqual(2, result.Summary.IterationsUsed, "interrupted request is still charged");
            AssertEqual(2, f.Model.Requests.Last().AcceptedMessages.Count(m => m.Kind == AgentMessageKind.User), "both user messages reach the model");
        }
        private static async Task InboxSteerDiscardsLateModelResponse()
        {
            var f = new KernelFixture(); var input = new KernelInputFake();
            f.Model.OnSend = (request, token) =>
            {
                if (f.Model.Requests.Count == 1)
                {
                    input.Add("Do not write");
                    return Task.FromResult(AgentModelResult.Accepted(KernelResponse(KernelCall())));
                }
                return Task.FromResult(AgentModelResult.Accepted(KernelResponse()));
            };
            await new AgentKernel(f.Model, f.Tools, f.Store, input: input).RunAsync(
                new AgentRunRequest("run", "turn", "original", new AgentRunLimits(5, 5)), CancellationToken.None);
            AssertEqual(0, f.Tools.Calls.Count, "late response cannot dispatch stale tools");
        }
        private static async Task InboxSteerClosesAcceptedBatch()
        {
            var f = new KernelFixture(KernelResponse(KernelCall("read"), KernelCall("read", "{\"x\":2}")), KernelResponse());
            var input = new KernelInputFake();
            f.Tools.OnExecute = (context, token) =>
            {
                input.Add("Change direction");
                AssertTrue(!token.IsCancellationRequested, "tool read-back is not cancelled by steer");
                return Task.FromResult(KernelRecord(context));
            };
            var result = await new AgentKernel(f.Model, f.Tools, f.Store, input: input).RunAsync(
                new AgentRunRequest("run", "turn", "original", new AgentRunLimits(5, 5)), CancellationToken.None);
            AssertEqual(1, f.Tools.Calls.Count, "rest of accepted batch never dispatches");
            AssertEqual(1, result.Summary.ToolCounts.ReadOk, "completed effect remains accounted");
            AssertTrue(f.Store.Events.Any(e => e.Execution != null && e.Execution.Outcome == ToolExecutionOutcome.NotDispatched), "accepted exchange is closed");
        }
        private static async Task InboxSteerWinsFinalRace()
        {
            var f = new KernelFixture(KernelResponse(), KernelResponse()); var input = new KernelInputFake();
            input.OnClose = () => input.Add("One more requirement");
            await new AgentKernel(f.Model, f.Tools, f.Store, input: input).RunAsync(
                new AgentRunRequest("run", "turn", "original", new AgentRunLimits(5, 5)), CancellationToken.None);
            AssertEqual(2, f.Model.Requests.Count, "final does not lose an accepted message");
        }
        private static async Task InboxSteerSupersedesConfirmation()
        {
            var f = new KernelFixture(KernelResponse(KernelCall("confirm")), KernelResponse());
            f.Tools.OnExecute = (context, token) => Task.FromResult(KernelRecord(context, ToolExecutionOutcome.AwaitingConfirmation));
            var paused = await f.RunAsync();
            var input = new KernelInputFake(); input.Add("Use another approach");
            var result = await new AgentKernel(f.Model, f.Tools, f.Store, input: input).ResumeAsync("resumed",
                paused.Summary.PendingConfirmation.PendingId, paused.Continuation, CancellationToken.None, true);
            AssertEqual(1, f.Tools.Calls.Count, "old pending call never executes");
            AssertEqual(paused.Summary.ToolStepsUsed, result.Summary.ToolStepsUsed, "reservation is not reset or charged twice");
            AssertEqual(paused.Summary.IterationsUsed + 1, result.Summary.IterationsUsed, "same model budget continues");
            AssertEqual(RunLifecycle.Completed, result.Summary.Lifecycle, "superseded approval continues through kernel");
        }
        private static void InboxDurabilityAndQueue()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths); var events = new ChatEventStoreAdapter(store);
                var session = NewSession(FakeOfficeAdapter.ForHost("Excel")); store.Save(session);
                var inbox = new ConversationInboxService(events);
                inbox.Submit(session, new ConversationInput { Id = "a", Text = "first", Delivery = InputDelivery.Queue });
                inbox.Submit(session, new ConversationInput { Id = "a", Text = "duplicate", Delivery = InputDelivery.Queue });
                inbox.Submit(session, new ConversationInput { Id = "b", Text = "second", Delivery = InputDelivery.Queue });
                AssertEqual(2, inbox.Snapshot(session).Items.Count, "intake idempotency");
                var accepted = events.Read(session, SessionEventReadMode.RequireComplete).First(e => e.Type == SessionEventTypes.ConversationInputChanged);
                AssertEqual("first", events.ReadPayload(session, accepted), "immutable text body is CAS-backed");
                AssertTrue(accepted.Data["Input"]["Text"].Type == Newtonsoft.Json.Linq.JTokenType.Null, "body is not duplicated in metadata");
                inbox.Update(session, new UpdateChatInputPayload { InputId = "b", ExpectedRevision = 1, Text = "edited" }, "edit");
                AssertEqual("a", inbox.TakeNext(session, false).Id, "FIFO claim");
                inbox.Applied(session, "a");
                var recovered = new ConversationInboxService(events);
                AssertTrue(recovered.Snapshot(session).Paused, "restart pauses");
                AssertTrue(!recovered.TryStartWorker(session), "restart cannot execute retained messages");
                recovered.Resume(session);
                AssertEqual("edited", recovered.TakeNext(session, false).Text, "edit survives replay");
            });
        }
        private static void InboxSharedLifetimeAndRecovery()
        {
            WithTempPaths(paths =>
            {
                var session = NewSession(FakeOfficeAdapter.ForHost("Excel"));
                var firstStore = new ChatStore(paths);
                firstStore.Save(session);
                var first = ConversationInboxService.AcquireShared(paths.Root, new ChatEventStoreAdapter(firstStore));
                var second = ConversationInboxService.AcquireShared(paths.Root,
                    new ChatEventStoreAdapter(new ChatStore(paths)));
                try
                {
                    AssertTrue(ReferenceEquals(first, second), "controllers share one live inbox");
                    second.Submit(session, new ConversationInput { Id = "queued", Text = "retained", Delivery = InputDelivery.Queue });
                    AssertEqual(1, first.Snapshot(session).Items.Count, "other controller observes accepted input");
                }
                finally
                {
                    ConversationInboxService.ReleaseShared(paths.Root, second);
                    ConversationInboxService.ReleaseShared(paths.Root, first);
                }
                var restarted = ConversationInboxService.AcquireShared(paths.Root,
                    new ChatEventStoreAdapter(new ChatStore(paths)));
                try
                {
                    AssertTrue(!ReferenceEquals(first, restarted), "last controller release drops live projection");
                    AssertTrue(restarted.Snapshot(session).Paused, "restart replays queue paused");
                    AssertTrue(!restarted.TryStartWorker(session), "restart cannot dispatch without explicit resume");
                    restarted.Resume(session);
                    AssertEqual("retained", restarted.TakeNext(session, false).Text, "explicit resume retains FIFO input");
                }
                finally { ConversationInboxService.ReleaseShared(paths.Root, restarted); }
            });
        }
        private sealed class InboxFailingEvents : IEventStore
        {
            internal readonly IEventStore Inner;
            internal bool Fail;
            internal InboxFailingEvents(IEventStore inner) { Inner = inner; }
            public SessionEvent Append(ChatSession session, SessionEventWrite write)
            { if (Fail) throw new System.IO.IOException("simulated disk failure"); return Inner.Append(session, write); }
            public IReadOnlyList<SessionEvent> Read(ChatSession session, SessionEventReadMode mode) { return Inner.Read(session, mode); }
            public string ReadPayload(ChatSession session, SessionEvent fact) { return Inner.ReadPayload(session, fact); }
        }
        private static void InboxFailedPauseStillStopsScheduling()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = NewSession(FakeOfficeAdapter.ForHost("Excel")); store.Save(session);
                var events = new InboxFailingEvents(new ChatEventStoreAdapter(store));
                var inbox = new ConversationInboxService(events);
                inbox.Submit(session, new ConversationInput { Id = "a", Text = "retained", Delivery = InputDelivery.Queue });
                events.Fail = true;
                var failed = false;
                try { inbox.Pause(session, "stop"); } catch (System.IO.IOException) { failed = true; }
                AssertTrue(failed, "failed persistence is reported");
                AssertTrue(inbox.Snapshot(session).Paused, "in-memory scheduling fails closed");
                AssertTrue(!inbox.TryStartWorker(session), "failed pause append cannot launch next operation");
            });
        }
        private static void InboxStopAndDocumentIsolation()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths); var events = new ChatEventStoreAdapter(store);
                var session = NewSession(FakeOfficeAdapter.ForHost("Excel")); store.Save(session);
                var inbox = new ConversationInboxService(events); inbox.Bind(session);
                inbox.Submit(session, new ConversationInput { Id = "a", Text = "steer", Delivery = InputDelivery.Steer, DocumentRuntimeKey = "doc-1" });
                AssertTrue(inbox.InterruptToken(session).IsCancellationRequested, "steer interrupts model");
                inbox.Pause(session, "stop");
                AssertTrue(!inbox.HasSteer(session), "stop freezes earlier steer");
                inbox.End(session, false, false);
                AssertTrue(!inbox.TryStartWorker(session), "stop does not restart queued work");
                inbox.Submit(session, new ConversationInput { Id = "b", Text = "new explicit request", Delivery = InputDelivery.Steer, DocumentRuntimeKey = "doc-1" });
                AssertEqual("b", inbox.TakeNext(session, false).Id, "new request may run while old queue stays paused");
                AssertEqual("doc-1", inbox.Find(session, "a").DocumentRuntimeKey, "original target retained");
                AssertTrue(inbox.Snapshot(session).Paused, "old queue remains paused");
            });
        }
    }
}
