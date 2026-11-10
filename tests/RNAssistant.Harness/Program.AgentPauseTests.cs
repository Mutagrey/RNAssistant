using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Application.Conversations;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RNAssistant.Runtime;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private sealed class KernelPauseInput : IRunInputChannel, IRunPauseChannel, IRunPauseSignal
        {
            private string _pauseId;
            private TaskCompletionSource<bool> _requested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public string PauseId { get { return _pauseId; } set {
                _pauseId = value;
                if (value == null) _requested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                else _requested.TrySetResult(true);
            } }
            public async Task WaitForPauseRequestAsync(CancellationToken token)
            {
                var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (token.Register(() => cancelled.TrySetCanceled()))
                    await await Task.WhenAny(_requested.Task, cancelled.Task);
            }
            public CancellationToken InterruptToken => CancellationToken.None;
            public bool HasPendingInput => false;
            public Task<IReadOnlyList<AgentMessage>> TakeInputAsync(CancellationToken token)
                => Task.FromResult<IReadOnlyList<AgentMessage>>(new AgentMessage[0]);
            internal Action BeforeClose;
            public bool TryCloseInput() { BeforeClose?.Invoke(); return PauseId == null; }
            public void AcknowledgePause(string pauseId) { }
            public Task WaitForResumeAsync(string pauseId, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                AssertTrue(PauseId == null, "test explicitly released its gate");
                return Task.CompletedTask;
            }
        }

        private static async Task AgentPauseKernelBoundaries()
        {
            foreach (var boundary in new[] { "before-model", "during-model", "during-tool", "during-batch", "before-dispatch", "finish-boundary" })
            {
                var batch = boundary == "during-batch" || boundary == "before-dispatch";
                var f = new KernelFixture(batch ? KernelResponse(KernelCall("read"), KernelCall("read", "{\"next\":true}"))
                    : KernelResponse(KernelCall("write")), KernelResponse());
                var input = new KernelPauseInput();
                if (boundary == "before-model") input.PauseId = "pause-1";
                if (boundary == "during-model") f.Model.OnSend = (request, token) => {
                    input.PauseId = "pause-1";
                    return Task.FromResult(AgentModelResult.Accepted(KernelResponse(KernelCall("write"))));
                };
                if (boundary == "finish-boundary")
                {
                    f.Model.OnSend = (request, token) => Task.FromResult(AgentModelResult.Accepted(KernelResponse()));
                    input.BeforeClose = () => input.PauseId = "pause-1";
                }
                if (boundary == "before-dispatch") f.Tools.BeforeDescribe = () => input.PauseId = "pause-1";
                f.Tools.OnExecute = (context, token) => {
                    if (boundary == "during-tool" || boundary == "during-batch") input.PauseId = "pause-1";
                    AssertTrue(!token.IsCancellationRequested, "peer pause never cancels an in-flight tool/read-back");
                    return Task.FromResult(KernelRecord(context, boundary == "during-tool" ? ToolExecutionOutcome.Unknown : ToolExecutionOutcome.Ok));
                };
                var scheduler = new ConversationExecutionScheduler(() => 1);
                var kernel = new AgentKernel(f.Model, f.Tools, f.Store, input: input, admission: scheduler);
                var paused = await kernel.RunAsync(new AgentRunRequest("run", "turn", "Task", new AgentRunLimits(4, 4)), CancellationToken.None);
                AssertEqual(RunLifecycle.Waiting, paused.Summary.Lifecycle, "cooperative pause is recoverable: " + boundary + " " + paused.Summary.Reason + " " + paused.Summary.AssistantMessage);
                AssertEqual("pause-1", paused.Summary.PauseId, "runtime identity retained");
                AssertEqual(0, scheduler.ActiveCount, "safe pause releases the execution slot");
                AssertTrue(paused.Summary.PendingWait == null, "pause is not a fabricated wait tool call");
                AssertEqual(boundary == "during-tool" || boundary == "during-batch" ? 1 : 0, f.Tools.Calls.Count, "only the already dispatched tool may run");
                AssertEqual(boundary == "before-model" ? 0 : 1, f.Model.Requests.Count, "pause does not poll the model");
                var completed = f.Store.Events.Where(e => e.Kind == AgentRunEventKind.ToolCompleted).ToArray();
                AssertEqual(batch ? 2 : boundary == "during-tool" ? 1 : 0, completed.Length, "accepted exchanges are closed");
                if (boundary == "during-tool") AssertEqual(1, paused.Summary.ToolCounts.WriteUnknown, "pause cannot erase an uncertain effect");
                var checkpoint = JsonConvert.DeserializeObject<AgentRunState>(JsonConvert.SerializeObject(new AgentRunState(
                    paused.Summary, paused.Continuation.Limits, consecutiveReflections: paused.Continuation.ConsecutiveReflections,
                    reflectionFeedbackIssued: paused.Continuation.ReflectionFeedbackIssued, finishCorrection: paused.Continuation.FinishCorrection)));
                var restored = AgentRunContinuation.RestoreWait(checkpoint, paused.Continuation.Revision, paused.AcceptedMessages);
                AssertEqual(RunLifecycle.Cancelled, AgentKernel.CancelWait(checkpoint).Summary.Lifecycle, "cold pause can be cancelled without dispatch");
                input.PauseId = null; input.BeforeClose = null; f.Tools.BeforeDescribe = null;
                f.Model.OnSend = (request, token) => Task.FromResult(AgentModelResult.Accepted(KernelResponse()));
                var result = await kernel.ResumeWaitAsync(restored, CancellationToken.None);
                AssertEqual(boundary == "during-tool" ? RunLifecycle.Failed : RunLifecycle.Completed, result.Summary.Lifecycle,
                    "resumed run retains the inspection guard: " + boundary + " " + result.Summary.Reason);
                if (boundary == "during-tool") AssertEqual("repeated_finish_no_progress", result.Summary.Reason, "pause does not waive investigation of unknown effects");
                AssertEqual("run", result.Summary.RunId, "same identity");
                AssertEqual(boundary == "during-tool" ? 4 : paused.Summary.IterationsUsed + 1, result.Summary.IterationsUsed, "spent model budget retained");
                AssertEqual(paused.Summary.ToolStepsUsed, result.Summary.ToolStepsUsed, "no redispatch or tool budget reset");
                AssertEqual(paused.Summary.ToolCounts.WriteUnknown, result.Summary.ToolCounts.WriteUnknown, "unknown effects retained after resume");
                await KernelThrowsAsync<RunStoreException>(() => kernel.ResumeWaitAsync(restored, CancellationToken.None));
            }
        }

        private static async Task AgentPauseAfterWait()
        {
            var f = new KernelFixture(KernelResponse(KernelCall("wait")), KernelResponse());
            var input = new KernelPauseInput();
            f.Tools.Policies["wait"] = new ToolPolicySnapshot("wait", "v1", new ToolPolicy(ToolEffect.Read,
                ToolVerification.None, false, false, new[] { "agent" }, evidenceEligibility: EvidenceEligibility.None));
            f.Tools.OnExecute = (context, token) => Task.FromResult(new ToolExecutionRecord(context,
                ToolExecutionOutcome.Waiting, context.StartedUtc, mayHaveDispatched: false,
                evidence: new ToolExecutionEvidence(ToolDispatchEvidence.NotDispatched, ToolEffectEvidence.None),
                waitCondition: new AgentWaitCondition(context.StartedUtc, context.StartedUtc.AddSeconds(1))));
            var kernel = new AgentKernel(f.Model, f.Tools, f.Store, input: input);
            var waiting = await kernel.RunAsync(new AgentRunRequest("run", "turn", "Wait", new AgentRunLimits(4, 4)), CancellationToken.None);
            input.PauseId = "during-wait";
            f.Tools.OnWait = (pending, token) => {
                AssertEqual(waiting.Summary.PendingWait.Condition.DeadlineUtc, pending.Condition.DeadlineUtc, "pause never extends the wait deadline");
                return Task.FromResult(new ToolExecutionRecord(pending.Context, ToolExecutionOutcome.Ok, pending.Condition.DeadlineUtc,
                    mayHaveDispatched: false, result: RNAssistant.Core.Tools.Contracts.ToolResult.Ok("Deadline elapsed"),
                    evidence: new ToolExecutionEvidence(ToolDispatchEvidence.NotDispatched, ToolEffectEvidence.None), waitTimedOut: true));
            };
            var paused = await kernel.ResumeWaitAsync(waiting.Continuation, CancellationToken.None);
            AssertEqual("during-wait", paused.Summary.PauseId, "wait closes before execution pause");
            AssertTrue(paused.Summary.PendingWait == null, "no unresolved wait left in paused context");
            AssertEqual(0, paused.Summary.ToolCounts.ReadError, "cooperative pause is not a tool error");
            AssertEqual(1, f.Model.Requests.Count, "no model call between timeout and pause");
            input.PauseId = null;
            var final = await kernel.ResumeWaitAsync(paused.Continuation, CancellationToken.None);
            AssertEqual(RunLifecycle.Completed, final.Summary.Lifecycle, "continues after ordinary timeout");
            AssertEqual(1, final.Summary.ToolStepsUsed, "wait charged once");
        }

        private static void AgentPauseAuthorityAndRaces()
        {
            WithTempExecutor((executor, adapter) => {
                var policy = ConversationRunPolicy.For(ChatModes.Plan, MutationPolicy.Deny);
                foreach (var tool in AgentCommunicationToolCatalog.GetTools().Where(item => item.Id == AgentCommunicationToolCatalog.Pause || item.Id == AgentCommunicationToolCatalog.Resume))
                {
                    AssertTrue(policy.Allows(tool.Id, tool.Policy, tool.Binding) &&
                        !policy.EffectiveToolPolicy(tool.Id, tool.Policy, tool.Binding).RequiresConfirmation, "coordination is available without document mutation grants");
                    AssertTrue(!policy.Allows(tool.Id, tool.Policy, new ToolBinding("spoof")), "exact binding remains mandatory");
                }
                var store = new ChatStore(FixturePaths.Value); var inbox = new ConversationInboxService(EventStore(store));
                var source = AgentChat(store, "Source"); var target = AgentChat(store, "Target");
                target.LastRun = new ChatRunRecord { RunId = "run", TurnId = "run", Status = "running",
                    KernelState = new AgentRunState(new RunSummary("run", "run", RunLifecycle.Running, new ToolCounts(), 1, 0, "", "", null), new AgentRunLimits(8, 8)) };
                store.Commit(target, new ConversationCommit());
                var owner = new ConversationRunOwner(ConversationLeaseService.ForOffice(FixturePaths.Value));
                var service = new AgentCommunicationService(); RegisterCancellationScope(service, store, inbox, source, owner);
                var address = AgentChatAddress.IdFor(target.Id);
                using (var cancellation = new CancellationTokenSource())
                using (var handle = owner.Start(target.Id, "run", target, cancellation))
                {
                    inbox.Bind(target);
                    var requested = service.Pause(inbox, source, 20, "p1", address, false, () => { });
                    AssertEqual(AgentPauseStatus.Requested, requested.Status, "request is distinct from a reached boundary");
                    AssertTrue(!inbox.Snapshot(target).Paused && !cancellation.IsCancellationRequested, "current tool is allowed to complete");
                    AssertEqual(AgentPauseStatus.Released, service.Pause(inbox, source, 20, "r1", address, true, () => { }).Status, "early release");
                    AssertTrue(inbox.WaitForResumeAsync(target, "p1", cancellation.Token).IsCompleted, "resume-before-registration is not lost");
                    service.Pause(inbox, source, 20, "p2", address, false, () => { });
                    var gate = inbox.WaitForResumeAsync(target, "p2", cancellation.Token);
                    AssertTrue(!gate.IsCompleted && inbox.Snapshot(target).Paused, "boundary is acknowledged without blocking the caller");
                    AssertEqual(AgentPauseStatus.Paused, service.Pause(inbox, source, 20, "p2-check", address, false, () => { }).Status, "repeated pause observes safe boundary");
                    var stale = service.Pause(inbox, source, 20, "stale-resume", address, true, () => {
                        inbox.Resume(target);
                        service.Pause(inbox, source, 20, "p3", address, false, () => { });
                    });
                    AssertEqual(AgentPauseStatus.Changed, stale.Status, "delayed resume does not release a newer pause on the same Run");
                    AssertEqual("p3", inbox.RunPauseId(target), "replacement pause survives");
                    AssertTrue(gate.Wait(1000), "old gate yields to the new boundary");
                    gate = inbox.WaitForResumeAsync(target, "p3", cancellation.Token);
                    inbox.Pause(target, "Human Stop");
                    AssertEqual(AgentPauseStatus.Protected, service.Pause(inbox, source, 20, "peer-resume-stop", address, true, () => { }).Status, "peer cannot release human Stop");
                    AssertEqual(AgentPauseStatus.Protected, service.Pause(inbox, source, 20, "peer-pause-stop", address, false, () => { }).Status, "peer cannot relabel human authority");
                    AssertTrue(!gate.IsCompleted, "user authority remains pending");
                    inbox.Resume(target);
                    AssertTrue(gate.Wait(1000), "explicit human resume releases the gate");
                    var failing = new ConversationInboxService(new AgentReceiptFailureEvents(EventStore(store), AgentDeliveryState.PauseRecorded));
                    RuntimeThrows<IOException>(() => service.Pause(failing, source, 20, "lost-receipt", address, false, () => { }));
                    inbox.Resume(target);
                    var dispatches = 0;
                    AssertEqual(AgentPauseStatus.Unconfirmed, service.Pause(failing, source, 20, "lost-receipt", address, false, () => dispatches++).Status, "prepared-only outcome stays unknown");
                    AssertEqual(0, dispatches, "unknown pause is never re-executed");
                    foreach (var invalid in new[] { AgentChatAddress.IdFor(source.Id), "missing-peer" })
                        RuntimeThrows<AgentCommunicationException>(() => service.Pause(inbox, source, 20, "bad-" + invalid, invalid, false, () => dispatches++));
                    RuntimeThrows<AgentCommunicationException>(() => service.Pause(inbox, source, 0, "disabled", address, true, () => dispatches++));
                    AssertEqual(0, dispatches, "self/cross-scope/disabled controls do not dispatch");
                    ConversationRunHandle replacement = null;
                    using (var replacementCancellation = new CancellationTokenSource())
                    try
                    {
                        var stalePause = service.Pause(inbox, source, 20, "stale-lifetime", address, false, () => {
                            handle.Dispose(); inbox.EndRun(target); inbox.Resume(target);
                            replacement = owner.Start(target.Id, "run", target, replacementCancellation); inbox.Bind(target);
                        });
                        AssertEqual(AgentPauseStatus.Changed, stalePause.Status, "old invocation cannot pause a new invocation of the same Run");
                        AssertTrue(inbox.RunPauseId(target) == null && !inbox.Snapshot(target).Paused, "replacement is unaffected");
                    }
                    finally { replacement?.Dispose(); inbox.EndRun(target); }
                }
            });
        }

        private static void AgentPauseOfficeRecovery() { AgentPauseOfficeRecovery(false); }
        private static void AgentPauseOfficeRecovery(bool userControl)
        {
            foreach (var cancel in new[] { false, true })
            WithTempExecutor((executor, adapter) => {
                var store = new ChatStore(FixturePaths.Value); var inbox = new ConversationInboxService(EventStore(store));
                var target = AgentChat(store, "Office pause"); var source = AgentChat(store, "Peer");
                var service = new AgentCommunicationService();
                RegisterCancellationScope(service, store, inbox, source, scopeKey: userControl ? "office" : null);
                var calls = 0;
                var settings = new AppSettings { StreamResponses = false, SmartChatTitles = false, AutoCompressContext = false };
                LlmCompletionDelegate model = (config, messages, options, stream, token) => {
                    if (++calls == 1)
                    {
                        bool changed;
                        inbox.ControlPause(target, target.LastRun.RunId, "durable-pause", false, out changed);
                    }
                    return Task.FromResult(new LlmCompletionResult { Content = ModelProtocolWire.WriteAcceptedDecision(
                        ConversationResponse.FinishAction, "Done", "Finished", results: new ConversationResultClaim[0], outcome: ConversationOutcome.Completed) });
                };
                var runtime = new ConversationRunService(adapter, executor, ConversationStore(store), EventStore(store), model, null,
                    saved: current => { if (current.LastRun?.KernelState?.Summary.PauseId != null) throw new IOException("Process lost after pause commit"); }, inbox: inbox);
                inbox.Bind(target);
                RuntimeThrows<RunStoreException>(() => runtime.ExecuteAsync(ChatModes.Agent, "Task", target, NewContext(adapter), settings,
                    executor.GetControllerTools().ToList(), null, null, null, BuiltInSkillProvider.GetSkills(adapter), CancellationToken.None).GetAwaiter().GetResult());
                var saved = store.Load(target.Id); var runId = saved.LastRun.RunId;
                var recoveredInbox = new ConversationInboxService(EventStore(store));
                RegisterCancellationScope(service, store, recoveredInbox, source, scopeKey: userControl ? "office" : null);
                AssertEqual("durable-pause", saved.LastRun.KernelState.Summary.PauseId, "pause survives event replay");
                AssertEqual(AgentPauseStatus.Protected, service.Pause(recoveredInbox, source, 20, "restart-resume", AgentChatAddress.IdFor(target.Id), true, () => { }).Status, "peer cannot resume after restart");
                AssertEqual(1, calls, "inspection/control cannot dispatch the model");
                if (cancel)
                {
                    var messages = saved.Messages.Count;
                    if (userControl)
                    {
                        var shown = recoveredInbox.Snapshot(saved);
                        AssertTrue(shown.RunPaused && shown.WaitTarget.RunId == runId && shown.WaitTarget.Revision == saved.Revision,
                            "Office Inbox exposes typed pause and exact user control target");
                        var command = new AgentWaitCancelCommand { Source = "office", SessionId = target.Id, Target = shown.WaitTarget };
                        var revision = shown.WaitTarget.Revision;
                        command.Target.Revision--;
                        AssertEqual(AgentWaitCancelStatus.Changed, service.CancelSavedWait(command).Status, "stale button cannot cancel");
                        command.Target.Revision = revision;
                        using (ConversationLeaseService.ForOffice(FixturePaths.Value).Reserve(target.Id))
                            AssertEqual(AgentWaitCancelStatus.Unavailable, service.CancelSavedWait(command).Status, "cold control cannot take over a live lease");
                        command.Target.SessionId = "foreign";
                        AssertEqual(AgentWaitCancelStatus.Unavailable, service.CancelSavedWait(command).Status, "foreign target cannot be controlled");
                        command.Target.SessionId = target.Id;
                        store.AppendFaultPoint = point => { if (point == "before-write") throw new IOException("Lost write"); };
                        AssertEqual(AgentWaitCancelStatus.Unconfirmed, service.CancelSavedWait(command).Status, "write failure never claims cancellation");
                        store.AppendFaultPoint = null;
                        command.Target = recoveredInbox.Snapshot(store.Load(target.Id)).WaitTarget;
                        AssertEqual(AgentWaitCancelStatus.Cancelled, service.CancelSavedWait(command).Status, "explicit user cancellation confirms durable state");
                        AssertEqual("Остановлено пользователем", recoveredInbox.Snapshot(store.Load(target.Id)).PauseReason,
                            "human cancellation retains its own pause reason");
                        AssertEqual(AgentWaitCancelStatus.Cancelled, service.CancelSavedWait(command).Status, "historical cancellation receipt cannot redispatch work");
                    }
                    else
                        AssertEqual(AgentCancellationStatus.Cancelled, service.Cancel(recoveredInbox, source, 20, "cancel-pause", AgentChatAddress.IdFor(target.Id), () => { }).Status, "cold pause cancellation is terminal");
                    var terminal = store.Load(target.Id);
                    AssertEqual(messages, terminal.Messages.Count, "pause cancellation fabricates no tool exchange");
                    AssertTrue(terminal.LastRun.KernelState.Summary.PauseId == null, "terminal clears pause");
                    AssertEqual(1, calls, "cold cancel runs no model");
                    return;
                }
                if (userControl)
                {
                    var receipt = service.ResumeSelectedWaits(new AgentRecoveryCommand { Source = "office", SessionId = target.Id,
                        Targets = new List<AgentWaitResumeTarget> { recoveredInbox.Snapshot(saved).WaitTarget } }).Receipts.Single();
                    AssertEqual(AgentWaitResumeStatus.Requested, receipt.Status, "user resume uses exact saved target");
                    saved = store.Load(target.Id);
                }
                else recoveredInbox.Resume(saved);
                recoveredInbox.Bind(saved);
                var resumed = new ConversationRunService(adapter, executor, ConversationStore(store), EventStore(store), model, null, inbox: recoveredInbox);
                resumed.ResumeWaitAsync(saved, new ConversationRunInput(settings, NewContext(adapter), executor.GetControllerTools().ToList(),
                    BuiltInSkillProvider.GetSkills(adapter)), null, null, CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(RunLifecycle.Completed, saved.LastRun.KernelState.Summary.Lifecycle, "explicit user recovery completes");
                AssertEqual(runId, saved.LastRun.RunId, "original Run resumes");
                AssertEqual(2, calls, "only one further model request");
                AssertEqual(2, saved.LastRun.KernelState.Summary.IterationsUsed, "spent iteration retained");
            });
        }
        private static void AgentPauseWorkspaceWire(string action)
        {
            WithPluginPaths(paths => {
                var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
                using (var listener = new HttpListener())
                using (var projects = new RNAssistant.Application.PluginApplicationService(paths))
                using (var runtime = new WorkspaceConversationService(paths))
                using (var firstRequest = new ManualResetEventSlim())
                {
                    var id = projects.CreateProjectWorkspace(new ProjectConnectionRequest { RootPath = Path.Combine(paths.Root, "pause-team") }).SelectedWorkspaceId;
                    var workspace = new WorkspaceStore(paths).OpenConnected(id);
                    var target = runtime.CreateSession(workspace, "Target"); var source = runtime.CreateSession(workspace, "Controller");
                    var store = new ChatStore(paths);
                    var settings = new AppSettings { BaseUrl = "http://127.0.0.1:" + port, Model = "pause-model", AgentResponseMode = "json_object",
                        StreamResponses = false, SmartChatTitles = false, AutoCompressContext = false, MaxAgentIterations = 5 };
                    listener.Prefixes.Add(settings.BaseUrl + "/"); listener.Start();
                    var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var expected = action == "resume" ? 6 : action == "restart" || action == "user-resume" ? 4 : 3;
                    var targetCalls = 0; var sourceCalls = 0;
                    var active = runtime;
                    var server = Task.Run(async () => {
                        Task pendingFirst = null;
                        for (var i = 0; i < expected; i++)
                        {
                            var request = await listener.GetContextAsync().ConfigureAwait(false);
                            string body; using (var reader = new StreamReader(request.Request.InputStream)) body = await reader.ReadToEndAsync().ConfigureAwait(false);
                            if (body.Contains("target-pause-marker"))
                            {
                                Interlocked.Increment(ref targetCalls);
                                var final = ModelProtocolWire.WriteAcceptedDecision(ConversationResponse.FinishAction, "Done", "Finished target",
                                    results: new ConversationResultClaim[0], outcome: ConversationOutcome.Completed);
                                if (targetCalls == 1)
                                {
                                    firstRequest.Set();
                                    pendingFirst = Task.Run(async () => { await releaseFirst.Task.ConfigureAwait(false); await SteeringReply(request, final).ConfigureAwait(false); });
                                }
                                else await SteeringReply(request, final).ConfigureAwait(false);
                            }
                            else
                            {
                                var n = ++sourceCalls;
                                var tool = n <= 2 ? "agents.pause" : "agents.resume";
                                if (n % 2 == 0) AssertTrue(body.Contains(n == 2 ? "Requested" : "Released"), "model receives a typed control receipt");
                                await SteeringReply(request, n % 2 == 1 ? ModelProtocolWire.WriteAcceptedCall("Control peer", null,
                                    new ConversationToolCall { Name = tool, Arguments = new JObject { ["agent_id"] = AgentChatAddress.IdFor(target.Id) } }) :
                                    ScriptedVerifiedFinish(store.Load(source.Id), "Control accepted", "Control requested",
                                        "Only control intake or release is claimed", tool)).ConfigureAwait(false);
                            }
                        }
                        if (pendingFirst != null) await pendingFirst.ConfigureAwait(false);
                    });
                    try
                    {
                        var peer = runtime.SubmitInputAsync(workspace, new WorkspaceSubmitInputRequest { SessionId = target.Id,
                            RequestId = "target", Message = "target-pause-marker" }, settings, () => "test");
                        AssertTrue(firstRequest.Wait(5000), "target is inside a model request");
                        var controller = runtime.SubmitInputAsync(workspace, new WorkspaceSubmitInputRequest { SessionId = source.Id,
                            RequestId = "pause", Message = "Pause the target" }, settings, () => "test");
                        AssertTrue(controller.Wait(5000), "controller can complete while the target request is in flight");
                        AssertEqual(RunLifecycle.Completed, controller.Result.Summary.Lifecycle, "pause receipt admits source finish");
                        AssertTrue(!peer.IsCompleted, "requesting pause does not cancel the model");
                        releaseFirst.TrySetResult(true);
                        AssertTrue(SpinWait.SpinUntil(() => {
                            var saved = store.Load(target.Id);
                            return saved.LastRun?.KernelState?.Summary.PauseId != null &&
                                new ConversationInputJournal(EventStore(store)).Read(saved, false).LastOrDefault()?.Change.Paused == true;
                        }, 5000), "kernel reaches and persists a safe pause");
                        var checkpoint = store.Load(target.Id); var runId = checkpoint.LastRun.RunId;
                        var shown = runtime.ReadState(workspace, target.Id);
                        AssertTrue(shown.RunPaused && shown.HasPendingWait && shown.WaitTarget.RunId == runId &&
                            shown.WaitTarget.Revision == checkpoint.Revision, "typed live pause and exact target reach Workspace projection");
                        AssertEqual(1, targetCalls, "paused owner makes no background model calls");
                        if (action == "resume")
                        {
                            controller = runtime.SubmitInputAsync(workspace, new WorkspaceSubmitInputRequest { SessionId = source.Id,
                                RequestId = "resume", Message = "Resume the target" }, settings, () => "test");
                            AssertTrue(Task.WaitAll(new Task[] { peer, controller }, 5000), "independent owners settle after peer release");
                            AssertEqual(RunLifecycle.Completed, peer.Result.Summary.Lifecycle, "peer resumed");
                            AssertEqual(RunLifecycle.Completed, controller.Result.Summary.Lifecycle, "resume receipt admits source finish");
                        }
                        else if (action == "user-resume")
                        {
                            var request = new AgentRecoveryCommand { Source = "workspace", WorkspaceId = id, SessionId = target.Id,
                                Targets = new List<AgentWaitResumeTarget> { shown.WaitTarget } };
                            shown.WaitTarget.Revision--;
                            AssertEqual(AgentWaitResumeStatus.Changed, runtime.AgentCommunication.ResumeSelectedWaits(request).Receipts.Single().Status,
                                "late user resume does not release a changed pause");
                            shown.WaitTarget.Revision++;
                            request.Targets.Add(new AgentWaitResumeTarget { SessionId = source.Id, RunId = "stale-run", Revision = 1 });
                            var batch = runtime.AgentCommunication.ResumeSelectedWaits(request);
                            AssertEqual(AgentWaitResumeStatus.Unavailable, batch.Receipts[0].Status,
                                "batch recovery cannot release a live owner before other admissions");
                            AssertTrue(!peer.IsCompleted, "live pause remains held after the rejected batch");
                            request.Targets.RemoveAt(1);
                            AssertEqual(AgentWaitResumeStatus.Requested, runtime.AgentCommunication.ResumeSelectedWaits(request).Receipts.Single().Status,
                                "explicit user releases the live pause");
                            AssertTrue(peer.Wait(5000), "same live owner completes after user resume");
                        }
                        else if (action == "user-cancel")
                        {
                            runtime.CloseAsync().GetAwaiter().GetResult();
                            AssertEqual(RunLifecycle.Waiting, peer.GetAwaiter().GetResult().Summary.Lifecycle, "shutdown preserves the saved Run");
                            active = new WorkspaceConversationService(paths);
                            active.RegisterAgentScope(workspace, settings, () => "test");
                            shown = active.ReadState(workspace, target.Id);
                            AssertTrue(shown.RunPaused && !shown.CanSteer, "cold pause is visible without live ownership");
                            var receipt = active.AgentCommunication.CancelSavedWait(new AgentWaitCancelCommand {
                                Source = "workspace", WorkspaceId = id, SessionId = target.Id, Target = shown.WaitTarget });
                            AssertEqual(AgentWaitCancelStatus.Cancelled, receipt.Status, "explicit user cancels the saved pause without continuing it");
                        }
                        else if (action == "restart")
                        {
                            runtime.CloseAsync().GetAwaiter().GetResult();
                            AssertEqual(RunLifecycle.Waiting, peer.GetAwaiter().GetResult().Summary.Lifecycle, "shutdown preserves pause checkpoint");
                            active = new WorkspaceConversationService(paths);
                            AssertTrue(active.ReadState(workspace, target.Id).HasPendingWait, "cold recovery exposes existing shared resume path");
                            active.UpdateInboxAsync(workspace, new WorkspaceInboxCommand { SessionId = target.Id, Action = WorkspaceInboxAction.Resume },
                                settings, () => "test", null, CancellationToken.None).GetAwaiter().GetResult();
                            AssertTrue(active.RunsDrained.Wait(5000), "explicit user recovery settles without opening a view");
                        }
                        else
                        {
                            var receipt = runtime.CancelRunAsync(workspace, new WorkspaceRunControlRequest { SessionId = target.Id,
                                RunId = runId, Cursor = ConversationEventFeed.Cursor(checkpoint) }).GetAwaiter().GetResult();
                            AssertEqual(WorkspaceCancellationStatus.Requested, receipt.Status, "user Stop reaches paused owner");
                            AssertTrue(peer.Wait(5000), "paused async wait responds to Stop");
                        }
                        var finished = store.Load(target.Id);
                        AssertEqual(runId, finished.LastRun.RunId, "control and recovery preserve original Run");
                        AssertEqual(action == "stop" || action == "user-cancel" ? RunLifecycle.Cancelled : RunLifecycle.Completed, finished.LastRun.KernelState.Summary.Lifecycle, "durable terminal state");
                        AssertEqual(action == "stop" || action == "user-cancel" ? 1 : 2, targetCalls, "no replay or polling model calls");
                        AssertEqual(action == "stop" || action == "user-cancel" ? 1 : 2, finished.LastRun.KernelState.Summary.IterationsUsed, "no budget reset");
                        AssertTrue(server.Wait(1000), "all expected wire requests completed");
                    }
                    finally
                    {
                        releaseFirst.TrySetResult(true); listener.Stop(); active.CloseAsync().GetAwaiter().GetResult();
                        if (!ReferenceEquals(active, runtime)) active.Dispose();
                        try { server.Wait(1000); } catch (AggregateException) { }
                    }
                }
            });
        }
    }
}
