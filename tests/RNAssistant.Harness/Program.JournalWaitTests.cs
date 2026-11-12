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
        private static void JournalWaitParity(string scenario)
        {
            foreach (var office in new[] { true, false })
            {
                var settings = new AppSettings { Model = "journal-wait", MaxCollaborationAgents = 4,
                    StreamResponses = false, SmartChatTitles = false, AutoCompressContext = false,
                    ContextWindowOverrideTokens = 65536, RequestTimeoutSeconds = 5, MaxAgentFormatRetries = 0 };
                WithTempPaths(paths => {
                    var adapter = FakeOfficeAdapter.ForHost("Excel");
                    var workspace = office ? null : new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "project"));
                    var scope = office ? "document:" + DocumentAuthorityId.Create().Id : "workspace:" + workspace.WorkspaceId;
                    var journal = new ConversationJournal(paths, scope); IConversationStore store = journal;
                    var chat = office ? new ChatSessionService(adapter, store).CreatePersistentChat("Saved wait") :
                        new WorkspaceStore(paths).CreateSession(store, workspace, "Saved wait");
                    var payloads = new ChatBlobStore(paths); var resources = new ResourceJournal(paths);
                    var files = new WorkspaceFileService(paths, resources, resources, resources);
                    var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths), new ToolStore(paths),
                        () => settings, paths: paths, resourceAuthorityStore: resources, resourceMutationJournal: resources,
                        resourceOrigin: (session, context) => journal.MutationOrigin(session, context));
                    var communication = new AgentCommunicationService(() => 4);
                    var inbox = new ConversationInboxService(journal);
                    communication.Register(new AgentConversationScope { Key = scope, Contains = session => session.Id == chat.Id,
                        ReadCatalog = () => new[] { new AgentConversationEndpoint {
                            Address = AgentCommunicationService.Address(chat), Inbox = inbox, Load = () => store.Load(chat.Id) } } });
                    executor.AgentCommunication = communication; executor.AgentInbox = inbox;
                    var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
                    settings.BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
                    var requests = 0; var suspending = true;
                    using (var listener = new HttpListener())
                    using (var shutdown = new CancellationTokenSource())
                    {
                        listener.Prefixes.Add(settings.BaseUrl + "/"); listener.Start();
                        var provider = Task.Run(async () => {
                            try
                            {
                                while (true)
                                {
                                    var request = await listener.GetContextAsync().ConfigureAwait(false);
                                    using (var reader = new StreamReader(request.Request.InputStream)) await reader.ReadToEndAsync().ConfigureAwait(false);
                                    AssertTrue(++requests <= 2, "wait continuation never repeats the accepted tool call");
                                    await SteeringReply(request, requests == 1
                                        ? ModelProtocolWire.WriteAcceptedCall("Observe incoming input", null, new ConversationToolCall {
                                            Name = "agents.wait", Arguments = new JObject { ["timeout_seconds"] = 1 } })
                                        : ModelProtocolWire.WriteAcceptedDecision("finish", "The observation ended", "Wait finished.",
                                            outcome: ConversationOutcome.UnableToComplete, results: new ConversationResultClaim[0])).ConfigureAwait(false);
                                }
                            }
                            catch (HttpListenerException) { }
                            catch (ObjectDisposedException) { }
                        });
                        try
                        {
                            var client = new LlmClient(() => "fixture-key");
                            Func<ConversationRunService> officeService = () => new ConversationRunService(adapter, executor, store, journal,
                                (configuration, messages, options, stream, token) => client.CompleteAsync(configuration, messages, options, stream, token),
                                null, inbox: inbox, suspendOnCancellation: () => suspending);
                            Func<ChatSession, WorkspaceConversationService.WorkspacePorts> ports = session =>
                                new WorkspaceConversationService.WorkspacePorts(paths, store, journal,
                                    new DocumentArtifactStore(resources, resources, payloads), files, payloads,
                                    new WorkspaceWebSnapshotStore(paths, files), workspace, session, settings, () => "fixture-key",
                                    frame => { if (frame.Kind == "ToolWaiting") shutdown.Cancel(); },
                                    agentCommunication: communication, agentInbox: inbox, resourceAuthority: resources, resourceMutations: resources);
                            inbox.Bind(chat);
                            if (office)
                                officeService().ExecuteAsync(ChatModes.Agent, "Wait for input", chat, chat.Context, settings,
                                    executor.GetControllerTools().ToArray(), null,
                                    (phase, message, activity) => { if (phase == "waiting_agents") shutdown.Cancel(); }, null, null, shutdown.Token).GetAwaiter().GetResult();
                            else
                            {
                                var id = Guid.NewGuid().ToString("N");
                                chat.LastRun = new ChatRunRecord { RunId = id, TurnId = id, StartedUtc = DateTime.UtcNow,
                                    ConversationPolicy = ConversationRunPolicy.For(ChatModes.Agent) };
                                var user = new ChatMessage { Role = "user", RunId = id, Content = "Wait for input" };
                                chat.Messages.Add(user); store.Commit(chat, ConversationCommit.UserInput(user));
                                var runtime = ports(chat);
                                ConversationRunCoordinator.RunAsync(new AgentRunRequest(id, id, user.Content, new AgentRunLimits(5, 5)),
                                    runtime, runtime.Tools, runtime, shutdown.Token, suspendOnCancellation: () => true).GetAwaiter().GetResult();
                            }
                            suspending = false;
                            journal.Rebuild(); chat = store.Load(chat.Id);
                            var pending = chat.LastRun.KernelState.Summary.PendingWait;
                            AssertTrue(pending != null, "shutdown retains the exact wait");
                            var runId = chat.LastRun.RunId; var deadline = pending.Condition.DeadlineUtc;
                            AssertEqual(0, journal.ReadWindow(chat.Id, long.MaxValue, 8).Count, "waiting is not a tool terminal");
                            AssertEqual(1, requests, "no model request while suspended");
                            AssertEqual(chat.Revision, RecoverJournalRun(store, chat, office).Revision, "restart recovery leaves a saved wait pending");
                            if (scenario == "timeout" && !office)
                            {
                                var backup = Path.Combine(paths.Root, "wait-backup");
                                JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                                var copiedPaths = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "wait-restore"), _ => StorageProtector.None);
                                IConversationStore copied = new ConversationJournal(copiedPaths, scope);
                                AssertEqual(deadline, copied.Load(chat.Id).LastRun.KernelState.Summary.PendingWait.Condition.DeadlineUtc,
                                    "restore preserves the original deadline without starting work");
                            }
                            inbox = new ConversationInboxService(journal); executor.AgentInbox = inbox;
                            AssertTrue(inbox.Snapshot(chat).Paused, "cold wait remains paused until explicit resume");
                            if (scenario == "control-ack")
                            {
                                journal.Journal.Fault = stage => { if (stage == "after_flush") throw new IOException("lost queue resume acknowledgement"); };
                                RuntimeThrows<IOException>(() => inbox.Resume(chat)); journal.Journal.Fault = null;
                                journal.Rebuild(); chat = store.Load(chat.Id); inbox = new ConversationInboxService(journal); executor.AgentInbox = inbox;
                                AssertTrue(inbox.Snapshot(chat).Paused, "cold restart never auto-resumes after a lost control acknowledgement");
                                AssertTrue(!journal.QueueControl(chat.Id).Fact.Paused, "accepted resume remains in the canonical control index");
                                AssertEqual(1, requests, "lost control acknowledgement makes no model request");
                            }
                            inbox.Resume(chat); inbox.Bind(chat);
                            var stale = store.Load(chat.Id);
                            Action<ChatSession, CancellationToken> resume = (session, token) => {
                                if (office) officeService().ResumeWaitAsync(session,
                                    new ConversationRunInput(settings, session.Context, executor.GetControllerTools().ToArray()), null, null, token).GetAwaiter().GetResult();
                                else
                                {
                                    var runtime = ports(session);
                                    var continuation = AgentRunContinuation.RestoreWait(session.LastRun.KernelState, 0,
                                        WorkspaceConversationService.RestoreAcceptedHistory(session, payloads, true));
                                    ConversationRunCoordinator.ContinueAsync(continuation, runtime, runtime.Tools, runtime, token).GetAwaiter().GetResult();
                                }
                            };
                            if (scenario == "changed")
                            {
                                var changed = JObject.FromObject(chat.LastRun.KernelState);
                                changed["Summary"]["PendingWait"]["Condition"]["DeadlineUtc"] = deadline.AddSeconds(1);
                                chat.LastRun.KernelState = changed.ToObject<AgentRunState>();
                                RuntimeThrows<RunStoreException>(() => resume(chat, CancellationToken.None));
                                chat = store.Load(chat.Id);
                                new ConversationInputJournal(journal).Append(chat, new ConversationInputEvent { Paused = true, PauseReason = "User stop" });
                                RuntimeThrows<RunStoreException>(() => resume(chat, CancellationToken.None));
                                AssertEqual(deadline, store.Load(chat.Id).LastRun.KernelState.Summary.PendingWait.Condition.DeadlineUtc, "pause and failed claim retain the condition");
                                AssertEqual(1, requests, "changed deadline and paused queue cannot reach the model");
                                return;
                            }
                            var invocation = journal.CurrentRun(chat.Id).Fact.InvocationId;
                            if (scenario == "claim-ack")
                            {
                                journal.Journal.Fault = stage => { if (stage == "after_flush") throw new IOException("lost wait claim acknowledgement"); };
                                RuntimeThrows<RunStoreException>(() => resume(chat, CancellationToken.None)); journal.Journal.Fault = null;
                                AssertEqual(0, journal.ReadWindow(chat.Id, long.MaxValue, 8).Count, "lost claim leaves no terminal");
                                AssertEqual(1, requests, "uncertain claim does not reach another model request");
                                journal.Rebuild(); chat = store.Load(chat.Id);
                                inbox.Bind(chat);
                            }
                            resume(chat, scenario == "cancel" ? new CancellationToken(true) : CancellationToken.None);
                            RuntimeThrows<RunStoreException>(() => resume(stale, CancellationToken.None));
                            var finished = store.Load(chat.Id);
                            var terminal = journal.ReadWindow(chat.Id, long.MaxValue, 8).Single();
                            AssertEqual(runId, finished.LastRun.RunId, "wait continues its original run");
                            AssertTrue(invocation != journal.CurrentRun(chat.Id).Fact.InvocationId, "explicit continuation owns a new invocation");
                            AssertEqual(pending.Context.Call.Id, terminal.Fact.Result.ToolCallId, "exact original call resolves once");
                            AssertEqual(scenario == "cancel" ? ToolExecutionOutcome.NotDispatched : ToolExecutionOutcome.Ok,
                                terminal.Fact.Result.ExecutionProgress.Outcome, "cancel and timeout retain distinct outcomes");
                            AssertEqual(scenario != "cancel", terminal.Fact.Result.ExecutionProgress.WaitTimedOut, "timeout proof is retained");
                            AssertEqual(1, finished.LastRun.KernelState.Summary.ToolStepsUsed, "wait charges its budget once");
                            journal.Rebuild();
                            AssertEqual(terminal.Position.Hash, journal.ReadWindow(chat.Id, long.MaxValue, 8).Single().Position.Hash, "rebuild cannot duplicate the terminal");
                        }
                        finally { journal.Journal.Fault = null; listener.Stop(); AssertTrue(provider.Wait(5000), "wait provider stops"); }
                    }
                });
            }
        }
    }
}
