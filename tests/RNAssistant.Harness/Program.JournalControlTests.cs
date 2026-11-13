using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RNAssistant.Application.Conversations;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Runtime;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void JournalControlParity(string scenario)
        {
            foreach (var office in new[] { true, false }) WithJournalInput(office, (paths, journal, source, scope) => {
                IConversationStore store = journal;
                var workspace = office ? null : new WorkspaceStore(paths).OpenConnected(source.WorkspaceId);
                var target = store.CreateTransient(source.Host, source.DocumentKey, source.DocumentTitle, "Controlled peer");
                store.Commit(target, new ConversationCommit());
                var inbox = new ConversationInboxService(journal);
                var leases = ConversationLeaseService.ForScope(paths, scope);
                var runs = new ConversationRunOwner(leases);
                var cancel = scenario.StartsWith("cancel", StringComparison.Ordinal);
                var cold = scenario.StartsWith("cancel-cold", StringComparison.Ordinal);
                var pausedCancel = scenario == "cancel-paused" || scenario == "cancel-cold-paused";
                var unknown = scenario == "unknown" || scenario == "cancel-unknown";
                var communication = new AgentCommunicationService(() => 2);
                var settings = new AppSettings { Model = "journal-control", MaxCollaborationAgents = 2,
                    StreamResponses = false, SmartChatTitles = false, AutoCompressContext = false,
                    ContextWindowOverrideTokens = 65536, RequestTimeoutSeconds = 5, MaxAgentFormatRetries = 0 };
                var catalogReads = 0; var validations = 0; var notifications = 0;
                Func<AgentChatAddress, AgentConversationEndpoint> endpoint = address => new AgentConversationEndpoint {
                    Address = address, Inbox = inbox, Runs = runs, Store = store, Load = () => store.Load(address.ChatId),
                    Reserve = () => leases.Reserve(address.ChatId), Payloads = new ChatBlobStore(paths),
                    Validate = chat => {
                        if (++validations == 1 && (scenario == "ack" || scenario == "cancel-ack"))
                            journal.Journal.Fault = point => { if (point == "after_flush") {
                                journal.Journal.Fault = null; throw new IOException("lost control acknowledgement"); } };
                        if (validations == 1 && unknown)
                            journal.Fault = point => { if (point == "before_peer_control_readback") throw new IOException("control proof unavailable"); };
                        if (validations == 2 && scenario == "cancel-cold-ack")
                            journal.Journal.Fault = point => { if (point == "after_flush") {
                                journal.Journal.Fault = null; throw new IOException("lost target terminal acknowledgement"); } };
                        return chat.WorkspaceId ?? chat.DocumentKey;
                    }, Changed = chat => notifications++ };
                communication.Register(new AgentConversationScope { Key = scope,
                    Contains = chat => chat != null && chat.Host == source.Host && chat.DocumentKey == source.DocumentKey,
                    Endpoint = endpoint, ReadCatalog = () => { catalogReads++; return new[] {
                        endpoint(AgentCommunicationService.Address(source)), endpoint(AgentCommunicationService.Address(target)) }; } });
                var resources = new ResourceJournal(paths); var payloads = new ChatBlobStore(paths);
                var files = new WorkspaceFileService(paths, resources, resources, resources);
                var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
                settings.BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
                var requested = new ManualResetEventSlim();
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var sourceRound = 0; var targetRound = 0; var tool = cancel ? AgentCommunicationToolCatalog.Cancel : AgentCommunicationToolCatalog.Pause;
                var rejected = scenario == "cancel-cold-ack" || scenario == "cancel-cold-busy"; Task peer = null;
                IDisposable heldLease = null;
                Action manualResume = null;
                using (var cancellation = new CancellationTokenSource())
                using (var listener = new HttpListener())
                {
                    listener.Prefixes.Add(settings.BaseUrl + "/"); listener.Start();
                    var provider = Task.Run(async () => {
                        Task firstReply = null;
                        try
                        {
                            while (true)
                            {
                                var request = await listener.GetContextAsync().ConfigureAwait(false);
                                string wire; using (var reader = new StreamReader(request.Request.InputStream)) wire = await reader.ReadToEndAsync().ConfigureAwait(false);
                                if (wire.Contains("target-control-marker"))
                                {
                                    var finish = ModelProtocolWire.WriteAcceptedDecision("finish", "Done", "Target complete.",
                                        outcome: ConversationOutcome.Completed, results: new ConversationResultClaim[0]);
                                    if (++targetRound == 1)
                                    {
                                        if (scenario == "wait" || cold && !pausedCancel) await SteeringReply(request, ModelProtocolWire.WriteAcceptedCall("Observe input", null,
                                            new ConversationToolCall { Name = AgentCommunicationToolCatalog.Wait,
                                                Arguments = new JObject { ["timeout_seconds"] = 1 } })).ConfigureAwait(false);
                                        else { requested.Set(); firstReply = Task.Run(async () => {
                                            await release.Task.ConfigureAwait(false);
                                            try { await SteeringReply(request, finish).ConfigureAwait(false); }
                                            catch (HttpListenerException) when (cancel) { }
                                            catch (IOException) when (cancel) { }
                                        }); }
                                    }
                                    else await SteeringReply(request, scenario == "wait" ? ScriptedVerifiedFinish(store.Load(target.Id),
                                        "Observation ended", "Target complete.", "Only the input observation is claimed", AgentCommunicationToolCatalog.Wait) : finish).ConfigureAwait(false);
                                }
                                else
                                {
                                    AssertTrue(++sourceRound <= (scenario == "cancel-cold-ack" ? 4 : 2), "finish correction is bounded without repeating control");
                                    var args = new JObject { ["agent_id"] = AgentChatAddress.IdFor(target.Id) };
                                    if (tool == AgentCommunicationToolCatalog.Send) args["text"] = "queued native peer input";
                                    var response = sourceRound == 1 ? ModelProtocolWire.WriteAcceptedCall("Control the peer", null,
                                        new ConversationToolCall { Name = tool, Arguments = args }) : rejected
                                        ? ModelProtocolWire.WriteAcceptedDecision("finish", "The queue is protected", "No release performed.",
                                            outcome: ConversationOutcome.UnableToComplete, results: new[] { new ConversationResultClaim("Release the peer pause",
                                                new[] { store.Load(source.Id).Messages.Last(item => item.ToolName == tool && item.EvidenceReference != null).EvidenceReference.Alias },
                                                scenario == "cancel-cold-ack" ? ConversationAssessment.Unverified : ConversationAssessment.NotSatisfied) })
                                        : ScriptedVerifiedFinish(store.Load(source.Id), "Receipt observed", "Control accepted.", "Only intake or release is proven", tool);
                                    await SteeringReply(request, response).ConfigureAwait(false);
                                }
                            }
                        }
                        catch (HttpListenerException) { }
                        catch (ObjectDisposedException) { }
                        catch (Exception error) { Console.WriteLine(error); listener.Abort(); throw; }
                        finally { if (firstReply != null) await firstReply.ConfigureAwait(false); }
                    });
                    Action<ChatSession, CancellationTokenSource> run = (chat, runCancellation) => {
                        var id = Guid.NewGuid().ToString("N");
                        chat.LastRun = new ChatRunRecord { RunId = id, TurnId = id, StartedUtc = DateTime.UtcNow,
                            ConversationPolicy = ConversationRunPolicy.ForSession(chat) };
                        var message = new ChatMessage { Role = "user", RunId = id,
                            Content = chat.Id == target.Id ? "target-control-marker" : "Control this exact peer" };
                        chat.Messages.Add(message); store.Commit(chat, ConversationCommit.UserInput(message));
                        using (runs.Start(chat.Id, id, chat, runCancellation))
                        {
                            inbox.Bind(chat);
                            try
                            {
                                if (office)
                                {
                                    var adapter = FakeOfficeAdapter.ForHost("Excel");
                                    var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths), new ToolStore(paths),
                                        () => settings, paths: paths, resourceAuthorityStore: resources, resourceMutationJournal: resources,
                                        resourceOrigin: (session, context) => journal.MutationOrigin(session, context));
                                    executor.AgentCommunication = communication; executor.AgentInbox = inbox;
                                    var client = new LlmClient(() => "fixture-key");
                                    var service = new ConversationRunService(adapter, executor, store, journal,
                                        (config, messages, options, stream, token) => client.CompleteAsync(config, messages, options, stream, token),
                                        null, inbox: inbox, suspendOnCancellation: () => scenario == "restart" || cold);
                                    service.ExecuteAsync(chat.Mode, message.Content, chat, chat.Context, settings,
                                        executor.GetControllerTools().ToArray(), null, (phase, text, activity) => {
                                            if (chat.Id == target.Id && cold && phase == "waiting_agents") requested.Set();
                                            if (chat.Id == target.Id && scenario == "wait" && phase == "waiting_agents")
                                            { requested.Set(); AssertTrue(release.Task.Wait(5000), "control precedes the pending-wait claim"); }
                                        }, null, null, runCancellation?.Token ?? CancellationToken.None,
                                        appendUserMessage: false).GetAwaiter().GetResult();
                                }
                                else
                                {
                                    var ports = new WorkspaceConversationService.WorkspacePorts(paths, store, journal,
                                        new DocumentArtifactStore(resources, resources, payloads), files, payloads,
                                        new WorkspaceWebSnapshotStore(paths, files), workspace, chat, settings, () => "fixture-key", frame => {
                                            if (chat.Id == target.Id && cold && frame.Kind == "ToolWaiting") requested.Set();
                                            if (chat.Id == target.Id && scenario == "wait" && frame.Kind == "ToolWaiting")
                                            { requested.Set(); AssertTrue(release.Task.Wait(5000), "control precedes the pending-wait claim"); }
                                        },
                                        agentCommunication: communication, agentInbox: inbox, resourceAuthority: resources, resourceMutations: resources);
                                    using (var input = new ConversationRunInputChannel(inbox, chat, (incoming, token) => {
                                        PersistPeerInput(store, chat, incoming, chat.LastRun.RunId);
                                        return Task.FromResult(AgentMessage.User(ConversationInputMessage.ModelText(chat.Messages.Single(item => item.Id == incoming.Id))));
                                    }))
                                        ConversationRunCoordinator.RunAsync(new AgentRunRequest(id, id, message.Content, new AgentRunLimits(8, 8)),
                                            ports, ports.Tools, ports, runCancellation?.Token ?? CancellationToken.None, input,
                                            suspendOnCancellation: () => scenario == "restart" || cold).GetAwaiter().GetResult();
                                }
                            }
                            finally { inbox.End(chat, true, false); }
                        }
                    };
                    try
                    {
                        peer = Task.Run(() => run(target, cancellation));
                        AssertTrue(requested.Wait(5000), "target enters a model request");
                        if (pausedCancel)
                        {
                            tool = AgentCommunicationToolCatalog.Pause; run(source, null); release.TrySetResult(true);
                            AssertTrue(SpinWait.SpinUntil(() => journal.QueueControl(target.Id)?.Fact.Action == QueueControlAction.AgentPauseAcknowledged, 5000), "peer pause reaches a safe cancellation boundary");
                            sourceRound = 0; tool = AgentCommunicationToolCatalog.Cancel;
                        }
                        if (cold)
                        {
                            cancellation.Cancel(); AssertTrue(peer.Wait(5000), "shutdown retains the original saved wait");
                            target = store.Load(target.Id); journal.Rebuild(); inbox = new ConversationInboxService(journal);
                            AssertTrue(ConversationInboxService.HasPendingWait(target), "cold cancellation starts from a saved wait");
                        }
                        if (cancel) inbox.Submit(target, InboxInput("cancel-retains-input", "Keep this queued", InputDelivery.Queue));
                        var beforeCancel = journal.CurrentRun(target.Id).Fact;
                        if (scenario == "cancel-cold-busy") heldLease = leases.Reserve(target.Id);
                        if (unknown)
                        {
                            RuntimeThrows<RunStoreException>(() => run(source, null)); journal.Fault = null;
                            source = RecoverJournalRun(store, store.Load(source.Id), office);
                        }
                        else run(source, null);
                        if (cancel && !unknown)
                        {
                            heldLease?.Dispose(); heldLease = null;
                            release.TrySetResult(true);
                            AssertTrue(peer.Wait(5000), "live cancellation settles without a second model call");
                            var saved = store.Load(target.Id);
                            var busy = scenario == "cancel-cold-busy";
                            AssertEqual(busy ? RunLifecycle.Waiting : RunLifecycle.Cancelled, saved.LastRun.KernelState.Summary.Lifecycle, "only the exact owner may terminate the target");
                            AssertEqual(beforeCancel.RunId, saved.LastRun.RunId, "cancel preserves the target run");
                            AssertEqual(beforeCancel.ToolStepsUsed, saved.LastRun.KernelState.Summary.ToolStepsUsed, "cancellation adds no tool charge");
                            AssertEqual(1, targetRound, "cancel never resumes the model");
                            var result = journal.ReadWindow(source.Id, long.MaxValue, 8).Single(item => item.Fact.Result.ToolName == AgentCommunicationToolCatalog.Cancel).Fact.Result;
                            AssertEqual(busy ? ToolExecutionOutcome.Error : scenario == "cancel-cold-ack" ? ToolExecutionOutcome.Unknown : ToolExecutionOutcome.Ok,
                                result.ExecutionProgress.Outcome, "source outcome reflects request or terminal proof, never a lost receipt");
                            if (cold && !busy && !pausedCancel) AssertEqual(1, saved.Messages.Count(item => item.ProtocolMessage && item.ToolName == AgentCommunicationToolCatalog.Wait && item.Role != "assistant"), "cold cancel closes the original call once");
                            var cancelEvents = (IEventStore)journal;
                            var cancelSourceEvents = cancelEvents.Read(ConversationMetadata.From(source), SessionEventReadMode.RequireComplete);
                            AssertEqual(busy ? 0 : pausedCancel ? 2 : 1, cancelSourceEvents.Count(item => item.Type == "agent.control"), "one cancel request or no dispatch on a competing lease");
                            journal.Rebuild(); inbox = new ConversationInboxService(journal);
                            var snapshot = inbox.Snapshot(store.Load(target.Id));
                            AssertTrue(snapshot.Paused && snapshot.Items.Count == 1, "restart keeps queued input paused without replaying cancel");
                            AssertEqual(0, catalogReads, "native cancel addresses its indexed peer");
                            if (!office)
                            {
                                var backup = Path.Combine(paths.Root, "cancel-backup"); JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                                var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "cancel-restore"), _ => StorageProtector.None);
                                var copy = new ConversationJournal(restored, scope);
                                AssertEqual(saved.LastRun.RunId, ((IConversationStore)copy).Load(target.Id).LastRun.RunId, "restore retains exact cancellation target");
                                AssertEqual(cancelSourceEvents.Count, ((IEventStore)copy).Read(ConversationMetadata.From(((IConversationStore)copy).Load(source.Id)), SessionEventReadMode.RequireComplete).Count, "restore retains native cancellation without dispatch");
                            }
                            return;
                        }
                        var receipt = journal.QueueControl(target.Id);
                        AssertEqual(cancel ? QueueControlAction.AgentCancelRequested : QueueControlAction.AgentPauseRequested, receipt.Fact.Action, "request does not claim a terminal or safe boundary");
                        AssertEqual(source.Id, receipt.Fact.Origin.Sender, "receipt names its accepted sender");
                        AssertEqual(unknown ? 0 : 1, notifications, "only proven read-back publishes the control");
                        AssertTrue(!peer.IsCompleted && targetRound == 1, "request does not abort the current model/wait step");
                        AssertEqual(unknown ? ToolExecutionOutcome.Unknown : ToolExecutionOutcome.Ok,
                            journal.ReadWindow(source.Id, long.MaxValue, 8).Single().Fact.Result.ExecutionProgress.Outcome, "source terminal reflects exact control proof");
                        release.TrySetResult(true);
                        if (unknown)
                        {
                            RuntimeThrows<RunStoreException>(() => peer.GetAwaiter().GetResult());
                            target = RecoverJournalRun(store, store.Load(target.Id), office);
                            journal.Rebuild(); inbox = new ConversationInboxService(journal);
                            AssertTrue(inbox.Snapshot(store.Load(target.Id)).Paused, "unconfirmed live request cannot grant authority after restart");
                            AssertEqual(1, targetRound, "unknown control is never replayed into another model call");
                            return;
                        }
                        AssertTrue(SpinWait.SpinUntil(() => {
                            var control = journal.QueueControl(target.Id); var current = journal.CurrentRun(target.Id);
                            return control.Fact.Action == QueueControlAction.AgentPauseAcknowledged &&
                                current.Fact.Lifecycle == RunLifecycle.Waiting && current.Fact.InvocationId != control.Fact.InvocationId;
                        }, 5000), "kernel acknowledges a safe boundary and claims the same live pause");
                        var paused = journal.CurrentRun(target.Id).Fact;
                        AssertEqual(1, targetRound, "a paused run makes no background model calls");
                        if (scenario == "manual")
                        {
                            var old = communication.PauseForUser(target.Id, "manual-old");
                            manualResume = communication.PauseForUser(target.Id, "manual-new");
                            AssertTrue(old != null && manualResume != null, "manual control binds the existing native live owner");
                            old();
                            AssertEqual("manual-new", inbox.RunPauseId(target), "late manual release cannot clear its replacement");
                            rejected = true;
                        }
                        else if (scenario == "stop" || scenario == "restart")
                        {
                            if (scenario == "stop") inbox.Pause(target, "User stop");
                            cancellation.Cancel(); AssertTrue(peer.Wait(5000), "stopped target settles");
                            target = store.Load(target.Id);
                            journal.Rebuild(); inbox = new ConversationInboxService(journal);
                            AssertTrue(inbox.Snapshot(target).Paused && inbox.RunPauseId(target) == null, "cold queue discards live peer authority");
                            rejected = true;
                        }
                        else
                        {
                            // Real peer delivery while paused must preserve authority and FIFO.
                            sourceRound = 0; tool = AgentCommunicationToolCatalog.Send; run(source, null);
                            AssertEqual(QueueControlAction.AgentPauseAcknowledged, journal.QueueControl(target.Id).Fact.Action, "peer input cannot clear a pause");
                            AssertEqual(1, inbox.ReadUndelivered(target).Count, "queued peer input remains pending");
                        }
                        sourceRound = 0; tool = AgentCommunicationToolCatalog.Resume; run(source, null);
                        if (manualResume != null) manualResume();
                        if (!rejected || manualResume != null)
                        {
                            AssertTrue(peer.Wait(5000), "same live target finishes after peer release");
                            AssertEqual(paused.RunId, target.LastRun.RunId, "release preserves the target run");
                            AssertEqual(RunLifecycle.Completed, target.LastRun.KernelState.Summary.Lifecycle, "kernel completes the released target");
                            AssertEqual(2, targetRound, "one model continuation follows release");
                            if (manualResume == null) AssertTrue(target.Messages.Any(item => item.Content == "queued native peer input"), "resumed target consumes its original input once");
                            AssertEqual(paused.Counts.ReadOk, target.LastRun.KernelState.Summary.ToolCounts.ReadOk, "continuation preserves completed wait counts");
                        }
                        else AssertEqual(1, targetRound, "peer resume cannot release Stop or restart");
                        AssertEqual(scenario == "manual" ? 2 : scenario == "wait" ? 1 : 0, catalogReads, "controls stay addressed; existing wait/user discovery owns its catalog reads");
                        var events = (IEventStore)journal;
                        var sourceEvents = events.Read(ConversationMetadata.From(source), SessionEventReadMode.RequireComplete);
                        AssertEqual(rejected ? 1 : 2, sourceEvents.Count(item => item.Type == "agent.control"), "one source notice per accepted control");
                        var cursor = new SessionEventPosition(source.Id, receipt.Position.Sequence, receipt.Position.Hash);
                        var page = events.OpenReader(ConversationMetadata.From(source)).Read(cursor, null, 100, CancellationToken.None);
                        AssertTrue(page.HighWater.Sequence == source.Revision, "native control is an exact feed anchor");
                        journal.Rebuild();
                        AssertEqual(receipt.Position.Hash, events.Read(ConversationMetadata.From(store.Load(source.Id)), SessionEventReadMode.RequireComplete)
                            .Single(item => item.EventId == receipt.Operation).Hash, "rebuild preserves the original control proof");
                        if (!office)
                        {
                            var backup = Path.Combine(paths.Root, "control-backup"); JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                            var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "control-restore"), _ => StorageProtector.None);
                            var copy = new ConversationJournal(restored, scope);
                            AssertEqual(sourceEvents.Count, ((IEventStore)copy).Read(ConversationMetadata.From(((IConversationStore)copy).Load(source.Id)),
                                SessionEventReadMode.RequireComplete).Count, "restore preserves controls without signalling an owner");
                        }
                    }
                    finally
                    {
                        journal.Fault = null; journal.Journal.Fault = null; release.TrySetResult(true);
                        heldLease?.Dispose();
                        if (peer?.IsCompleted == false) cancellation.Cancel();
                        try { peer?.Wait(5000); } catch (AggregateException) { }
                        try { listener.Stop(); } catch (ObjectDisposedException) { }
                        AssertTrue(provider.Wait(5000), "native control provider stops"); requested.Dispose();
                    }
                }
            });
        }
    }
}
