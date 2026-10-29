using RNAssistant.Application.Conversations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
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
            var result = await new AgentKernel(f.Model, f.Tools, f.Store, input: input).ResumeAsync(paused.Summary.RunId,
                paused.Summary.PendingConfirmation.PendingId, paused.Continuation, CancellationToken.None, true);
            AssertEqual(1, f.Tools.Calls.Count, "old pending call never executes");
            AssertEqual(paused.Summary.ToolStepsUsed, result.Summary.ToolStepsUsed, "reservation is not reset or charged twice");
            AssertEqual(paused.Summary.IterationsUsed + 1, result.Summary.IterationsUsed, "same model budget continues");
            AssertEqual(RunLifecycle.Completed, result.Summary.Lifecycle, "superseded approval continues through kernel");
        }
        private static ConversationInput InboxInput(string id, string text, InputDelivery delivery, string documentKey = null)
        {
            var input = new ConversationInput { Id = id, OperationId = id, Text = text, Delivery = delivery, DocumentRuntimeKey = documentKey };
            input.RequestFingerprint = ConversationInputJournal.Fingerprint(input);
            return input;
        }
        private static void InboxDurabilityAndQueue()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths); var events = new ChatEventStoreAdapter(store);
                var session = NewSession(FakeOfficeAdapter.ForHost("Excel")); store.Save(session);
                var inbox = new ConversationInboxService(events);
                inbox.Submit(session, InboxInput("a", "first", InputDelivery.Queue));
                inbox.Submit(session, InboxInput("a", "first", InputDelivery.Queue));
                var conflict = false;
                try { inbox.Submit(session, InboxInput("a", "different", InputDelivery.Queue)); }
                catch (ArgumentException) { conflict = true; }
                AssertTrue(conflict, "reusing an operation with different content is a conflict");
                var idCollision = InboxInput("a", "first", InputDelivery.Queue);
                idCollision.OperationId = "another-operation";
                var refusedCollision = false;
                try { inbox.Submit(session, idCollision); }
                catch (ArgumentException) { refusedCollision = true; }
                AssertTrue(refusedCollision, "one input ID cannot be accepted under another operation");
                inbox.Submit(session, InboxInput("b", "second", InputDelivery.Queue));
                AssertEqual(2, inbox.Snapshot(session).Items.Count, "intake idempotency");
                var accepted = events.Read(session, SessionEventReadMode.RequireComplete).First(e => e.Type == SessionEventTypes.ConversationInputChanged);
                AssertEqual("first", events.ReadPayload(session, accepted), "immutable text body is CAS-backed");
                AssertTrue(accepted.Data["Input"]["Text"].Type == Newtonsoft.Json.Linq.JTokenType.Null, "body is not duplicated in metadata");
                inbox.Update(session, new ConversationInputUpdate { InputId = "b", ExpectedRevision = 1, Text = "edited" }, "edit");
                AssertEqual("a", inbox.TakeNext(session).Id, "FIFO claim");
                var delivered = new ChatMessage { Id = "a", Role = "user", Content = "first" };
                session.Messages.Add(delivered);
                store.Commit(session, ConversationCommit.UserInput(delivered));
                inbox.Applied(session, "a");
                inbox.Submit(session, InboxInput("removed", "discard", InputDelivery.Queue));
                var removed = inbox.Update(session, new ConversationInputUpdate { InputId = "removed", ExpectedRevision = 1 }, "remove");
                AssertEqual(ConversationInputStatus.Removed, removed.Status, "removal returns its receipt identity");
                AssertEqual(removed.Revision, inbox.Update(session,
                    new ConversationInputUpdate { InputId = "removed", ExpectedRevision = 1 }, "remove").Revision, "removal retry is idempotent");
                AssertEqual(0, inbox.Snapshot(session).Receipts.Count, "normal snapshots do not replay terminal history");
                var recovered = new ConversationInboxService(events);
                var receipts = recovered.Snapshot(session, new[] { "a", "removed", "unknown", "a" }).Receipts;
                AssertEqual(2, receipts.Count, "bounded lookup deduplicates and only returns known operations");
                AssertEqual(ConversationInputStatus.Applied, receipts.Single(item => item.OperationId == "a").Status, "applied receipt survives restart");
                AssertEqual(ConversationInputStatus.Removed, receipts.Single(item => item.OperationId == "removed").Status, "removed receipt survives restart");
                AssertEqual(removed.Revision, receipts.Single(item => item.OperationId == "removed").Revision, "terminal receipt preserves input revision");
                var excessive = false;
                try { recovered.Snapshot(session, Enumerable.Repeat("a", 129).ToArray()); }
                catch (ArgumentException) { excessive = true; }
                AssertTrue(excessive, "receipt lookup is bounded");
                AssertTrue(recovered.Snapshot(session).Paused, "restart pauses");
                AssertTrue(!recovered.TryStartWorker(session), "restart cannot execute retained messages");
                AssertEqual("edited", recovered.FindOperation(session, "b", InboxInput("b", "second", InputDelivery.Queue).RequestFingerprint).Text,
                    "replay retains the original request fingerprint across edits");
                recovered.Resume(session);
                AssertEqual("edited", recovered.TakeNext(session).Text, "edit survives replay");
                session.Messages.Remove(delivered);
                store.Commit(session, ConversationCommit.RewriteHistory(new[] { delivered.Id }, session.Messages));
                var reloaded = store.Load(session.Id);
                var afterDelete = new ConversationInboxService(events);
                AssertEqual(ConversationInputStatus.Applied,
                    afterDelete.Snapshot(reloaded, new[] { "a" }).Receipts.Single().Status,
                    "history deletion retains the original applied receipt without breaking chat reads");
                AssertEqual(0, afterDelete.ReadUndelivered(reloaded).Count(input => input.Id == "a"),
                    "deleted applied input is not replayed");
                var shadow = InboxInput("shadow", "second", InputDelivery.Queue);
                shadow.OperationId = "b";
                shadow.RequestFingerprint = InboxInput("b", "second", InputDelivery.Queue).RequestFingerprint;
                shadow.Revision = 1;
                new ConversationInputJournal(events).Append(session, new ConversationInputEvent { Input = shadow });
                var ambiguous = false;
                try { new ConversationInboxService(events).FindOperation(session, "b", shadow.RequestFingerprint); }
                catch (InvalidOperationException) { ambiguous = true; }
                AssertTrue(ambiguous, "replay refuses one operation ID assigned to two inputs");
            });
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths); var events = new ChatEventStoreAdapter(store);
                var session = NewSession(FakeOfficeAdapter.ForHost("Excel")); store.Save(session);
                var inbox = new ConversationInboxService(events);
                inbox.Submit(session, InboxInput("missing", "retained", InputDelivery.Queue));
                var claimed = inbox.TakeNext(session);
                var premature = false;
                try { inbox.Applied(session, claimed.Id); }
                catch (InvalidOperationException) { premature = true; }
                AssertTrue(premature, "input cannot be marked applied before its user message is saved");
                claimed.Status = ConversationInputStatus.Applied; claimed.Revision++;
                new ConversationInputJournal(events).Append(session, new ConversationInputEvent { Input = claimed });
                var falseReceipt = false;
                try { new ConversationInboxService(events).Snapshot(session, new[] { claimed.OperationId }); }
                catch (InvalidOperationException error)
                { falseReceipt = error.Message.Contains("no persisted user message"); }
                AssertTrue(falseReceipt, "replay rejects an applied event with no durable user message");
                var wrongRole = new ChatMessage { Id = claimed.Id, Role = "assistant", Content = "not input" };
                session.Messages.Add(wrongRole);
                store.Commit(session, new ConversationCommit(new[] { wrongRole }));
                var idCollision = false;
                try { new ConversationInputJournal(events).Read(session); }
                catch (InvalidOperationException error)
                { idCollision = error.Message.Contains("does not identify its persisted user message"); }
                AssertTrue(idCollision, "a non-user message with the input ID cannot authorize applied replay");
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
                    second.Submit(session, InboxInput("queued", "retained", InputDelivery.Queue));
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
                    AssertEqual("retained", restarted.TakeNext(session).Text, "explicit resume retains FIFO input");
                }
                finally { ConversationInboxService.ReleaseShared(paths.Root, restarted); }
            });
        }

        private static void InboxRecoveryReconcilesBothConsumers()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = NewSession(FakeOfficeAdapter.ForHost("Excel"));
                store.Save(session);
                var events = new ChatEventStoreAdapter(store);
                var inbox = new ConversationInboxService(events);
                inbox.Submit(session, InboxInput("office", "Saved Office input", InputDelivery.Queue));
                var officeAccepted = inbox.FindAccepted(session, "office",
                    InboxInput("office", "Saved Office input", InputDelivery.Queue).RequestFingerprint);
                AssertEqual(1L, officeAccepted.Initial.Revision,
                    "Office live lookup retains the first accepted revision");
                var claimed = inbox.TakeNext(session);
                var message = new ChatMessage { Id = claimed.Id, Role = "user", Content = claimed.Text };
                session.Messages.Add(message);
                store.Commit(session, ConversationCommit.UserInput(message));
                AssertEqual(0, inbox.Snapshot(session).Items.Count,
                    "Office snapshot does not show a persisted message as undelivered");
                inbox.Resume(session);
                AssertEqual(ConversationInputStatus.Applied, inbox.Find(session, claimed.Id).Status,
                    "Office explicit resume projects the persisted input as applied");
                AssertEqual(officeAccepted.Event.Sequence, inbox.FindAccepted(session, "office").Event.Sequence,
                    "Office live receipt keeps its original accepted event after delivery");
                AssertEqual(ConversationInputStatus.Applied,
                    new ConversationInboxService(events).Find(session, claimed.Id).Status,
                    "Office recovery writes one durable applied receipt");
            });
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var workspace = new WorkspaceStore(paths).Open(System.IO.Path.Combine(paths.Root, "receipt-recovery"));
                var session = new WorkspaceStore(paths).CreateSession(store, workspace, "Recovered input");
                var events = new ChatEventStoreAdapter(store);
                var inbox = new ConversationInboxService(events);
                AssertEqual(0, inbox.Snapshot(session).Items.Count, "Workspace inbox projection is already loaded");
                var input = InboxInput("workspace", "Saved Workspace input", InputDelivery.Queue);
                input.AcceptedRunId = "accepted-run";
                input.Status = ConversationInputStatus.Delivering;
                input.Revision = 1;
                var journal = new ConversationInputJournal(events);
                journal.Append(session, new ConversationInputEvent { Input = input });
                AssertEqual(input.Text, inbox.ReadUndelivered(session, InputDelivery.Queue).Single().Text,
                    "Workspace snapshot reads the pending body from canonical events");
                var message = new ChatMessage { Id = input.Id, RunId = input.AcceptedRunId,
                    Role = "user", Content = input.Text };
                var beforeDelivery = store.Load(session.Id);
                session.Messages.Add(message);
                store.Commit(session, ConversationCommit.UserInput(message));
                AssertEqual(0, inbox.ReadUndelivered(session, InputDelivery.Queue).Count,
                    "Workspace snapshot hides a message already persisted before the receipt transition");
                inbox.ReconcileAcceptedDelivery(session);
                AssertEqual(ConversationInputStatus.Delivering,
                    inbox.ReadAccepted(beforeDelivery, input.OperationId,
                        input.RequestFingerprint, requireRunId: true).Current.Status,
                    "a stale message snapshot cannot observe a newer Applied event");
                AssertEqual(1, inbox.ReadUndelivered(beforeDelivery, InputDelivery.Queue).Count,
                    "pending input remains visible in its own event prefix");
                var workspaceAccepted = inbox.ReadAccepted(session, input.OperationId,
                    input.RequestFingerprint, requireRunId: true);
                AssertEqual(1L, workspaceAccepted.Initial.Revision,
                    "Workspace canonical lookup retains the first accepted revision");
                AssertEqual(ConversationInputStatus.Applied, workspaceAccepted.Current.Status,
                    "Workspace canonical lookup observes the current delivery state");
                AssertEqual(ConversationInputStatus.Applied, inbox.FindOperation(session, input.OperationId).Status,
                    "Workspace recovery updates the already loaded receipt projection");
                AssertTrue(!inbox.Snapshot(session).Paused,
                    "Workspace recovery clears the restart pause only after all claims are proven delivered");
                AssertEqual(ConversationInputStatus.Applied,
                    new ConversationInboxService(events).FindOperation(session, input.OperationId).Status,
                    "Workspace recovery persists the applied receipt");
                inbox.ReconcileAcceptedDelivery(session);
                AssertEqual(2, journal.Read(session).Count, "repeated recovery does not duplicate the transition");
            });
        }

        private static void InboxScopeSeparatesIdenticalChatIds()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var excel = NewSession(FakeOfficeAdapter.ForHost("Excel"));
                store.Save(excel);
                var word = NewSession(FakeOfficeAdapter.ForHost("Word"));
                word.Id = excel.Id;
                store.Save(word);
                var inbox = new ConversationInboxService(new ChatEventStoreAdapter(store));
                inbox.Submit(excel, InboxInput("same", "Excel input", InputDelivery.Queue));
                inbox.Submit(word, InboxInput("same", "Word input", InputDelivery.Queue));
                AssertEqual("Excel input", inbox.Snapshot(excel).Items.Single().Text,
                    "Excel input stays in its document scope");
                AssertEqual("Word input", inbox.Snapshot(word).Items.Single().Text,
                    "equal chat and operation IDs do not cross document scopes");
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
            public ISessionEventReader OpenReader(ChatSession session) { return Inner.OpenReader(session); }
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
                inbox.Submit(session, InboxInput("a", "retained", InputDelivery.Queue));
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
                inbox.Submit(session, InboxInput("a", "steer", InputDelivery.Steer, "doc-1"));
                AssertTrue(inbox.InterruptToken(session).IsCancellationRequested, "steer interrupts model");
                inbox.Pause(session, "stop");
                AssertTrue(!inbox.HasSteer(session), "stop freezes earlier steer");
                inbox.End(session, false, false);
                AssertTrue(!inbox.TryStartWorker(session), "stop does not restart queued work");
                inbox.Submit(session, InboxInput("b", "new explicit request", InputDelivery.Steer, "doc-1"));
                AssertEqual("b", inbox.TakeNext(session).Id, "new request may run while old queue stays paused");
                AssertEqual("doc-1", inbox.Find(session, "a").DocumentRuntimeKey, "original target retained");
                AssertTrue(inbox.Snapshot(session).Paused, "old queue remains paused");
            });
        }

        private static void InboxSteerRequiresExactLiveTarget()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = NewSession(FakeOfficeAdapter.ForHost("Excel"));
                session.LastRun = new ChatRunRecord { RunId = "run-1", TurnId = "turn-1", Status = "running",
                    KernelState = new AgentRunState(new RunSummary("run-1", "turn-1", RunLifecycle.Running,
                        new ToolCounts(), 1, 0, "", null, null), new AgentRunLimits(4, 4)) };
                store.Save(session);
                var inbox = new ConversationInboxService(new ChatEventStoreAdapter(store));
                inbox.Bind(session);
                var steer = InboxInput("steer", "change active run", InputDelivery.Steer);
                steer.AcceptedRunId = "run-1";
                steer.RequestFingerprint = ConversationInputJournal.Fingerprint(new { steer.Text, steer.AcceptedRunId });
                inbox.SubmitToRun(session, "run-1", steer);
                AssertTrue(inbox.InterruptToken(session).IsCancellationRequested,
                    "accepted exact steer interrupts the active model request");
                AssertTrue(!inbox.TryClose(session), "accepted steer keeps the kernel input boundary open");
                session.LastRun.Status = "completed";
                session.LastRun.KernelState = new AgentRunState(new RunSummary("run-1", "turn-1",
                    RunLifecycle.Completed, new ToolCounts(), 1, 0, "Done", null, null), new AgentRunLimits(4, 4));
                inbox.End(session, true, false);
                AssertTrue(!inbox.TryStartWorker(session), "an undelivered exact steer never becomes a new turn");
                AssertTrue(inbox.TakeNext(session) == null, "late exact steer remains retained for inspection");
                var retry = InboxInput("retry", steer.Text, InputDelivery.Steer);
                retry.OperationId = steer.OperationId; retry.AcceptedRunId = "run-1";
                retry.RequestFingerprint = steer.RequestFingerprint;
                inbox.SubmitToRun(session, "run-1", retry);
                AssertEqual("steer", inbox.Snapshot(session).Items.Single().Id,
                    "lost acknowledgement returns the original exact steer after run completion");
                retry.RequestFingerprint = "different";
                var conflict = false;
                try { inbox.SubmitToRun(session, "run-1", retry); }
                catch (ArgumentException) { conflict = true; }
                AssertTrue(conflict, "same operation cannot change content after the target closes");
                var late = InboxInput("late", "too late", InputDelivery.Steer);
                late.AcceptedRunId = "run-1";
                var refused = false;
                try { inbox.SubmitToRun(session, "run-1", late); }
                catch (InvalidOperationException) { refused = true; }
                AssertTrue(refused, "admission refuses a completed run");
                inbox.Submit(session, InboxInput("queue", "new task", InputDelivery.Queue));
                AssertTrue(inbox.TryStartWorker(session), "an explicit queue item may start a new turn");
                AssertEqual("queue", inbox.TakeNext(session).Id, "stale steer does not block FIFO queue");
                inbox.EndWorker(session);
            });
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = NewSession(FakeOfficeAdapter.ForHost("Excel"));
                var call = new ToolCall("call-1", "test.write", "{}");
                var policy = new ToolPolicySnapshot(call.Name, "policy-1", true, true);
                var pending = new PendingConfirmation("pending-1", call, policy, "step-1", 1);
                session.LastRun = new ChatRunRecord { RunId = "run-2", TurnId = "turn-2", Status = "awaitingconfirmation",
                    KernelState = new AgentRunState(new RunSummary("run-2", "turn-2", RunLifecycle.AwaitingConfirmation,
                        new ToolCounts(), 1, 1, "Confirm", null, pending), new AgentRunLimits(4, 4)) };
                store.Save(session);
                var events = new ChatEventStoreAdapter(store);
                var inbox = new ConversationInboxService(events);
                var steer = InboxInput("pending-steer", "another approach", InputDelivery.Steer);
                steer.AcceptedRunId = "run-2"; steer.PendingId = "pending-1";
                steer.RequestFingerprint = ConversationInputJournal.Fingerprint(new { steer.Text, steer.AcceptedRunId, steer.PendingId });
                inbox.SubmitToPending(session, "run-2", "pending-1", steer);
                AssertTrue(inbox.TryStartWorker(session), "exact pending steer resumes the waiting continuation");
                AssertEqual("pending-steer", inbox.TakeNext(session).Id, "only the matching pending target is selected");
                inbox.EndWorker(session);
                var changed = new PendingConfirmation("pending-2", call, policy, "step-2", 1);
                session.LastRun.KernelState = new AgentRunState(new RunSummary("run-2", "turn-2", RunLifecycle.AwaitingConfirmation,
                    new ToolCounts(), 1, 1, "Confirm", null, changed), new AgentRunLimits(4, 4));
                AssertTrue(!inbox.TryStartWorker(session), "changed pending ID cannot consume the old steer");
                var retry = InboxInput("pending-retry", steer.Text, InputDelivery.Steer);
                retry.OperationId = steer.OperationId; retry.AcceptedRunId = "run-2";
                retry.PendingId = "pending-1"; retry.RequestFingerprint = steer.RequestFingerprint;
                inbox.SubmitToPending(session, "run-2", "pending-1", retry);
                AssertEqual("pending-steer", inbox.Snapshot(session).Items.Single().Id,
                    "lost acknowledgement returns the original steer after pending ID changes");
                var stale = InboxInput("stale-pending", "stale", InputDelivery.Steer);
                stale.AcceptedRunId = "run-2"; stale.PendingId = "pending-1";
                var refused = false;
                try { inbox.SubmitToPending(session, "run-2", "pending-1", stale); }
                catch (InvalidOperationException) { refused = true; }
                AssertTrue(refused, "admission refuses a changed confirmation");
            });
        }

        private static void ConversationWorkShutdownDrainsContinuation()
        {
            var owner = new ConversationWorkService();
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var continuation = owner.ExecuteAsync(async token =>
            {
                entered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, token);
                return "unexpected";
            }, CancellationToken.None);
            AssertTrue(entered.Task.Wait(3000), "continuation entered application ownership");
            var closing = owner.CloseAsync();
            var cancelled = false;
            try { continuation.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { cancelled = true; }
            AssertTrue(cancelled, "shutdown cancels a terminal-waiting continuation");
            AssertTrue(closing.Wait(3000) && owner.Drained.IsCompleted, "shutdown drains the tracked continuation");
            var rejected = false;
            try { owner.ExecuteAsync(token => Task.FromResult(1), CancellationToken.None); }
            catch (ObjectDisposedException) { rejected = true; }
            AssertTrue(rejected, "closed owner rejects another continuation");
        }

        private sealed class ConversationQueueFake : IConversationQueuePort
        {
            internal readonly List<string> Seen = new List<string>();
            internal readonly TaskCompletionSource<bool> Completed =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly ConversationInboxService Inbox;
            internal readonly ChatStore Store;
            internal ConversationQueueFake(ConversationInboxService inbox, ChatStore store)
            { Inbox = inbox; Store = store; }
            public bool IsClosing { get { return false; } }
            public ChatSession Reload(string chatId) { return Store.Load(chatId); }
            public void ValidateBinding(ChatSession session, ConversationInput input) { }
            public Task ContinuePendingAsync(ChatSession session, string pendingId, CancellationToken token)
            { throw new InvalidOperationException("No pending confirmation in the queue fixture."); }
            public Task StartTurnAsync(ChatSession session, ConversationInput input, CancellationToken token)
            {
                Seen.Add(input.Id);
                // Domain execution reloads under its run lease after the claim.
                session = Store.Load(session.Id);
                Inbox.Bind(session);
                var message = new ChatMessage { Id = input.Id, Role = "user", Content = input.Text };
                session.Messages.Add(message);
                Store.Commit(session, ConversationCommit.UserInput(message));
                Inbox.Applied(session, input.Id);
                Inbox.End(session, completed: true, awaiting: false);
                return Task.FromResult(0);
            }
            public void Changed(ChatSession session) { Completed.TrySetResult(true); }
            public void Failed(Exception error) { Completed.TrySetException(error); }
        }

        private static void ConversationApplicationOwnsQueue()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = NewSession(FakeOfficeAdapter.ForHost("Excel"));
                store.Save(session);
                var inbox = new ConversationInboxService(new ChatEventStoreAdapter(store));
                var runs = new ConversationRunOwner(ConversationLeaseService.ForOffice(paths));
                var application = new ConversationApplicationService(inbox, runs);
                var port = new ConversationQueueFake(inbox, store);
                inbox.Submit(session, InboxInput("first", "one", InputDelivery.Queue));
                inbox.Submit(session, InboxInput("second", "two", InputDelivery.Queue));
                application.StartQueue(session, port);
                AssertTrue(port.Completed.Task.Wait(3000) && application.Drained.Wait(3000),
                    "one Application worker drains accepted FIFO input");
                AssertEqual("first,second", string.Join(",", port.Seen), "the owner selects FIFO order");
                AssertEqual(2, store.Load(session.Id).Messages.Count(message => message.Role == "user"),
                    "each input has one persisted user message");
                AssertTrue(!inbox.Snapshot(session).Running, "worker is no longer presented as an active run");
                AssertTrue(application.CloseAsync().Wait(3000), "shutdown drains the owner");
            });
        }
    }
}
