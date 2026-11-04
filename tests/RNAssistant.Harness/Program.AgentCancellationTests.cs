using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
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
using RNAssistant.Office.Services;
using RNAssistant.Runtime;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void RegisterCancellationScope(AgentCommunicationService service, ChatStore store,
            ConversationInboxService inbox, ChatSession source, ConversationRunOwner runs = null, ChatStore cancellationStore = null,
            Action<ChatSession> changed = null, string scopeKey = null)
        {
            var scope = StoredAgentScope(store, inbox, source);
            if (scopeKey != null) scope.Key = scopeKey;
            var read = scope.ReadCatalog;
            var leases = ConversationLeaseService.ForOffice(FixturePaths.Value);
            scope.ReadCatalog = () => read().Select(endpoint => {
                endpoint.Reserve = () => leases.Reserve(endpoint.Address.ChatId);
                endpoint.Runs = runs; endpoint.Store = ConversationStore(cancellationStore ?? store);
                endpoint.Changed = changed;
                endpoint.Payloads = new ChatBlobStore(FixturePaths.Value);
                return endpoint;
            }).ToArray();
            service.Register(scope);
        }

        private static void AgentCancelSavedWait()
        {
            foreach (var fault in new[] { "none", "before-write", "after-record", "after-flush", "receipt" })
            WithTempExecutor((executor, adapter) => {
                var store = new ChatStore(FixturePaths.Value);
                var service = new AgentCommunicationService();
                var oldInbox = new ConversationInboxService(EventStore(store));
                executor.AgentCommunication = service; executor.AgentInbox = oldInbox;
                var target = AgentChat(store, "Saved wait");
                var modelCalls = 0;
                var runtime = new ConversationRunService(adapter, executor, ConversationStore(store), EventStore(store),
                    (config, messages, options, stream, token) => {
                        modelCalls++;
                        return Task.FromResult(new LlmCompletionResult { Content = ModelProtocolWire.WriteAcceptedCall("Wait", null,
                            new ConversationToolCall { Name = "agents.wait", Arguments = new JObject { ["timeout_seconds"] = 10 } }) });
                    }, null, saved: current => { if (ConversationInboxService.HasPendingWait(current)) throw new IOException("Process lost"); }, inbox: oldInbox);
                oldInbox.Bind(target);
                RuntimeThrows<RunStoreException>(() => runtime.ExecuteAsync(ChatModes.Agent, "Wait for peers", target, NewContext(adapter),
                    new AppSettings { StreamResponses = false, SmartChatTitles = false, AutoCompressContext = false },
                    executor.GetControllerTools().ToList(), null, null, null, BuiltInSkillProvider.GetSkills(adapter), CancellationToken.None).GetAwaiter().GetResult());
                var previous = new ChatStore(FixturePaths.Value).Load(target.Id);
                var pending = previous.LastRun.KernelState;
                var withUnknown = new AgentRunState(new RunSummary(pending.Summary.RunId, pending.Summary.TurnId,
                    RunLifecycle.Waiting, new ToolCounts(writeUnknown: 1), pending.Summary.IterationsUsed, 2,
                    "", "waiting_agents", null, pending.Summary.PendingWait), pending.Limits);
                AssertEqual(ExecutionHealth.Unknown, AgentKernel.CancelWait(withUnknown).Summary.ExecutionHealth,
                    "cancelling a wait never erases earlier uncertain effects");
                var inbox = new ConversationInboxService(EventStore(store));
                var source = AgentChat(store, "Cancelling peer");
                var cancellationStore = new ChatStore(FixturePaths.Value);
                var notifications = 0;
                RegisterCancellationScope(service, store, inbox, source, cancellationStore: cancellationStore, changed: current => {
                    AssertEqual(RunLifecycle.Cancelled, current.LastRun.KernelState.Summary.Lifecycle, "notification follows terminal read-back");
                    notifications++; throw new InvalidOperationException("View closed");
                });
                service.Send(inbox, source, 20, "source-run", "queued-after-restart", AgentChatAddress.IdFor(target.Id), "Keep this queued", () => { });
                var dispatched = 0;
                AgentCancellationReceipt receipt;
                if (fault == "receipt")
                {
                    var failing = new ConversationInboxService(new AgentReceiptFailureEvents(EventStore(store), AgentDeliveryState.CancelRecorded));
                    RuntimeThrows<IOException>(() => service.Cancel(failing, source, 20, "cancel-saved", AgentChatAddress.IdFor(target.Id), () => dispatched++));
                    receipt = service.Cancel(failing, source, 20, "cancel-saved", AgentChatAddress.IdFor(target.Id), () => dispatched++);
                }
                else
                {
                    if (fault != "none") cancellationStore.AppendFaultPoint = point => { if (point == fault) throw new IOException("cancel " + fault); };
                    if (fault != "none")
                        RuntimeThrows<IOException>(() => service.Cancel(inbox, source, 20, "cancel-saved", AgentChatAddress.IdFor(target.Id), () => dispatched++));
                    receipt = service.Cancel(inbox, source, 20, "cancel-saved", AgentChatAddress.IdFor(target.Id), () => dispatched++);
                    cancellationStore.AppendFaultPoint = null;
                }
                if (fault != "none")
                {
                    AssertEqual(AgentCancellationStatus.Unconfirmed, receipt.Status, "fault does not invent terminal proof: " + fault);
                    var durable = new ChatStore(FixturePaths.Value).Load(target.Id);
                    if (fault == "before-write") AssertTrue(ConversationInboxService.HasPendingWait(durable), "refused cancellation retains the wait");
                    if (fault == "after-flush" || fault == "receipt")
                        AssertEqual(RunLifecycle.Cancelled, durable.LastRun.KernelState.Summary.Lifecycle, "durable cancellation survives failed receipt");
                    AssertTrue(durable.Messages.Count(item => item.Activity == null && item.ExecutionProgress != null) <= 1, "at most one terminal result after partial append");
                    var faultRecovery = new AgentCommunicationService(); var faultInbox = new ConversationInboxService(EventStore(store));
                    RegisterCancellationScope(faultRecovery, store, faultInbox, source);
                    var repeated = faultRecovery.Cancel(faultInbox, store.Load(source.Id), 20, "cancel-saved", receipt.AgentId, () => dispatched++);
                    AssertTrue(repeated.Status == AgentCancellationStatus.Unconfirmed && dispatched == 1, "unknown operation never replays after restart");
                    AssertEqual(durable.Revision, store.Load(target.Id).Revision, "retry does not write the target");
                    AssertTrue(faultInbox.Snapshot(durable).Paused, "fault recovery retains the pause");
                    AssertEqual(1, modelCalls, "fault handling never dispatches the model");
                    return;
                }
                AssertEqual(AgentCancellationStatus.Cancelled, receipt.Status, "cold wait cancellation is read back");
                AssertTrue(receipt.Changed, "first cancellation changed the target");
                AssertEqual(1, notifications, "a failed view notification cannot invalidate cancellation");
                var saved = new ChatStore(FixturePaths.Value).Load(target.Id);
                AssertEqual(previous.LastRun.RunId, saved.LastRun.RunId, "same run identity");
                AssertEqual(RunLifecycle.Cancelled, saved.LastRun.KernelState.Summary.Lifecycle, "terminal persisted");
                AssertTrue(saved.LastRun.KernelState.Summary.PendingWait == null, "wait cleared");
                AssertEqual(previous.LastRun.ToolStepsUsed, saved.LastRun.ToolStepsUsed, "cancellation adds no tool charge");
                var result = saved.Messages.Single(item => item.Activity == null && item.ExecutionProgress != null);
                AssertEqual(previous.LastRun.KernelState.Summary.PendingWait.Context.Call.Id, result.ToolCallId, "original call closed once");
                AssertEqual(previous.Messages.Single(item => item.ProtocolMessage && item.ToolCallId == result.ToolCallId).ToolResultRole,
                    result.Role, "original result role retained");
                AssertEqual(RNAssistant.Core.Tools.ToolExecutionOutcome.NotDispatched, result.ExecutionProgress.Outcome, "effect-free cancellation evidence");
                var snapshot = inbox.Snapshot(saved);
                AssertTrue(snapshot.Paused && !snapshot.HasPendingWait && snapshot.Items.Count == 1, "queued input retained on pause");
                AssertEqual(1, modelCalls, "cold cancellation invokes no model");
                var recovered = new AgentCommunicationService();
                var recoveredInbox = new ConversationInboxService(EventStore(store));
                RegisterCancellationScope(recovered, store, recoveredInbox, source);
                AssertEqual(AgentCancellationStatus.Cancelled, recovered.Cancel(recoveredInbox, store.Load(source.Id), 20,
                    "cancel-saved", receipt.AgentId, () => dispatched++).Status, "persisted receipt is idempotent");
                AssertEqual(1, dispatched, "retry does not dispatch");
                AssertTrue(!recovered.Cancel(recoveredInbox, store.Load(source.Id), 20, "already-cancelled", receipt.AgentId, () => { }).Changed,
                    "already cancelled reports no new target effect");
            });
        }

        private static void AgentCancelLiveAndStaleInvocation()
        {
            WithTempExecutor((executor, adapter) => {
                var store = new ChatStore(FixturePaths.Value); var inbox = new ConversationInboxService(EventStore(store));
                var source = AgentChat(store, "Source"); var target = AgentChat(store, "Target");
                target.LastRun = new ChatRunRecord { RunId = "same-run", TurnId = "same-run", Status = "running",
                    KernelState = new AgentRunState(new RunSummary("same-run", "same-run", RunLifecycle.Running,
                        new ToolCounts(writeUnknown: 1), 1, 1, "", "", null), new AgentRunLimits(8, 8)) };
                store.Commit(target, new ConversationCommit());
                var owner = new ConversationRunOwner(ConversationLeaseService.ForOffice(FixturePaths.Value), session => session);
                var service = new AgentCommunicationService(); RegisterCancellationScope(service, store, inbox, source, owner);
                var cancellation = new CancellationTokenSource();
                var handle = owner.Start(target.Id, target.LastRun.RunId, target, cancellation); inbox.Bind(target);
                try
                {
                    var first = service.Cancel(inbox, source, 20, "live", AgentChatAddress.IdFor(target.Id), () => { });
                    AssertEqual(AgentCancellationStatus.Requested, first.Status, "signal is not terminal proof");
                    AssertTrue(cancellation.IsCancellationRequested && inbox.Snapshot(target).Paused, "live owner signalled and queue paused");
                    AssertEqual(RunLifecycle.Running, store.Load(target.Id).LastRun.KernelState.Summary.Lifecycle, "only kernel may finish live work");
                    AssertEqual(1, store.Load(target.Id).LastRun.KernelState.Summary.ToolCounts.WriteUnknown, "unknown effects remain");
                    handle.Dispose(); inbox.EndRun(target);
                    inbox.Resume(target);
                    cancellation = new CancellationTokenSource();
                    handle = owner.Start(target.Id, target.LastRun.RunId, target, cancellation); inbox.Bind(target);
                    CancellationTokenSource replacement = null;
                    var stale = service.Cancel(inbox, source, 20, "stale-invocation", AgentChatAddress.IdFor(target.Id), () => {
                        handle.Dispose(); inbox.EndRun(target);
                        inbox.Resume(target);
                        replacement = new CancellationTokenSource();
                        handle = owner.Start(target.Id, target.LastRun.RunId, target, replacement); inbox.Bind(target);
                    });
                    AssertEqual(AgentCancellationStatus.Changed, stale.Status, "same Run with new owner is not cancelled");
                    AssertTrue(!replacement.IsCancellationRequested, "stale signal cannot reach replacement lifetime");
                    AssertTrue(!inbox.Snapshot(target).Paused, "stale control does not pause the replacement");
                    var dispatches = 0;
                    AssertEqual(AgentCancellationStatus.Requested, service.Cancel(inbox, source, 20, "live", AgentChatAddress.IdFor(target.Id),
                        () => dispatches++).Status, "old accepted receipt remains historical");
                    AssertTrue(dispatches == 0 && !replacement.IsCancellationRequested, "repeat cannot stop a later invocation");
                    foreach (var address in new[] { AgentChatAddress.IdFor(source.Id), "a-missing" })
                        RuntimeThrows<AgentCommunicationException>(() => service.Cancel(inbox, source, 20, "invalid-" + address, address, () => dispatches++));
                    RuntimeThrows<AgentCommunicationException>(() => service.Cancel(inbox, source, 0, "disabled", AgentChatAddress.IdFor(target.Id), () => dispatches++));
                    AssertEqual(0, dispatches, "self, foreign and disabled requests do not dispatch");
                    handle.Dispose(); inbox.EndRun(target);
                    var changed = service.Cancel(inbox, source, 20, "different-run", AgentChatAddress.IdFor(target.Id), () => {
                        target.LastRun = new ChatRunRecord { RunId = "replacement-run", TurnId = "replacement-run" };
                        store.Commit(target, new ConversationCommit());
                    });
                    AssertEqual(AgentCancellationStatus.Changed, changed.Status, "cold admission never follows a different run");
                    using (ConversationLeaseService.ForOffice(FixturePaths.Value).Reserve(target.Id))
                        AssertEqual(AgentCancellationStatus.Unavailable, service.Cancel(inbox, source, 20, "remote-owner",
                            AgentChatAddress.IdFor(target.Id), () => { }).Status, "unowned live lease is not taken over");
                }
                finally { handle.Dispose(); inbox.EndRun(target); }
            });
        }

        private static void AgentCancelWorkspaceWire(bool cold)
        {
            WithPluginPaths(paths => {
                var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
                using (var listener = new HttpListener())
                using (var projects = new RNAssistant.Application.PluginApplicationService(paths))
                using (var runtime = new WorkspaceConversationService(paths))
                {
                    var id = projects.CreateProjectWorkspace(new ProjectConnectionRequest { RootPath = Path.Combine(paths.Root, "cancel-team") }).SelectedWorkspaceId;
                    var workspace = new WorkspaceStore(paths).OpenConnected(id);
                    var target = runtime.CreateSession(workspace, "Waiting peer"); var source = runtime.CreateSession(workspace, "Controller peer");
                    var settings = new AppSettings { BaseUrl = "http://127.0.0.1:" + port, Model = "cancel-model", AgentResponseMode = "json_object",
                        StreamResponses = false, SmartChatTitles = false, AutoCompressContext = false, MaxAgentIterations = 4 };
                    listener.Prefixes.Add(settings.BaseUrl + "/"); listener.Start();
                    var calls = 0;
                    var activeRuntime = runtime;
                    var server = Task.Run(async () => {
                        for (var index = 0; index < 3; index++)
                        {
                            var request = await listener.GetContextAsync().ConfigureAwait(false); Interlocked.Increment(ref calls);
                            if (index == 2)
                            {
                                using (var reader = new StreamReader(request.Request.InputStream))
                                {
                                    var body = JObject.Parse(await reader.ReadToEndAsync().ConfigureAwait(false));
                                    AssertTrue(body["messages"].Values<JObject>().Any(message => (message["content"]?.ToString() ?? "")
                                        .Contains("\"Status\":\"" + (cold ? "Cancelled" : "Requested") + "\"")), "model receives the typed cancellation receipt");
                                }
                            }
                            await SteeringReply(request, index == 0 ? ModelProtocolWire.WriteAcceptedCall("Wait", null,
                                new ConversationToolCall { Name = "agents.wait", Arguments = new JObject { ["timeout_seconds"] = 10 } }) :
                                index == 1 ? ModelProtocolWire.WriteAcceptedCall("Stop peer", null,
                                new ConversationToolCall { Name = "agents.cancel", Arguments = new JObject { ["agent_id"] = AgentChatAddress.IdFor(target.Id) } }) :
                                ScriptedVerifiedFinish(new ChatStore(paths).Load(source.Id), "Signal sent", "Cancellation requested.",
                                    "Only the cancellation request was accepted; target termination is not claimed", "agents.cancel")).ConfigureAwait(false);
                        }
                    });
                    try
                    {
                        var waiting = new ManualResetEventSlim();
                        var peer = runtime.SubmitInputAsync(workspace, new WorkspaceSubmitInputRequest { SessionId = target.Id,
                            RequestId = "wait", Message = "Wait for peers" }, settings, () => "test", frame => { if (frame.Kind == "ToolWaiting") waiting.Set(); });
                        AssertTrue(waiting.Wait(5000), "target has a live waiting run");
                        if (cold)
                        {
                            runtime.CloseAsync().GetAwaiter().GetResult();
                            AssertEqual(RunLifecycle.Waiting, peer.GetAwaiter().GetResult().Summary.Lifecycle, "shutdown retains the unresolved wait");
                            activeRuntime = new WorkspaceConversationService(paths);
                        }
                        var controller = activeRuntime.SubmitInputAsync(workspace, new WorkspaceSubmitInputRequest { SessionId = source.Id,
                            RequestId = "cancel", Message = "Cancel the waiting peer" }, settings, () => "test");
                        if (!Task.WaitAll(new Task[] { peer, controller }, 6000))
                        {
                            foreach (var chatId in new[] { target.Id, source.Id })
                            {
                                var observed = new ChatStore(paths).Load(chatId);
                                Console.WriteLine("cancel pending: " + observed.Title + " " + observed.LastRun.KernelState.Summary.Lifecycle + " " + observed.LastRun.KernelState.Summary.Reason);
                                foreach (var item in observed.Messages.Where(message => message.ProtocolMessage).TakeLast(2))
                                    Console.WriteLine(item.Content);
                            }
                            throw new InvalidOperationException("Both independent owners did not settle.");
                        }
                        AssertEqual(cold ? RunLifecycle.Waiting : RunLifecycle.Cancelled, peer.Result.Summary.Lifecycle, "original worker terminal state");
                        AssertEqual(RunLifecycle.Completed, controller.Result.Summary.Lifecycle, "sender continues independently");
                        AssertEqual(3, calls, "no extra target model dispatch");
                        AssertTrue(server.Wait(1000), "all scripted model requests completed");
                        var saved = new ChatStore(paths).Load(target.Id);
                        AssertEqual(RunLifecycle.Cancelled, saved.LastRun.KernelState.Summary.Lifecycle, "terminal survives reload");
                    }
                    catch (Exception error) { Console.WriteLine("cancel wire diagnostic: calls=" + calls + " server=" + server.Status + " " + server.Exception + " " + error); throw; }
                    finally { listener.Stop(); activeRuntime.CloseAsync().GetAwaiter().GetResult();
                        if (!ReferenceEquals(activeRuntime, runtime)) activeRuntime.Dispose();
                        try { server.Wait(1000); } catch (AggregateException) { } }
                }
            });
        }
    }
}
