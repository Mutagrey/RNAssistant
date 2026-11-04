using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RNAssistant.Application.Contracts;
using RNAssistant.Application.Conversations;
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
        private static void AgentRecoverySelection()
        {
            WithTempExecutor((executor, adapter) => {
                var paths = FixturePaths.Value; var store = new ChatStore(paths);
                var service = new AgentCommunicationService();
                var oldInbox = new ConversationInboxService(EventStore(store));
                executor.AgentCommunication = service; executor.AgentInbox = oldInbox;
                var sessions = new List<ChatSession>();
                var modelCalls = 0;
                for (var index = 0; index < 6; index++)
                {
                    var current = AgentChat(store, "Waiting " + index);
                    var runtime = new ConversationRunService(adapter, executor, ConversationStore(store), EventStore(store),
                        (config, messages, options, stream, token) => {
                            modelCalls++;
                            return Task.FromResult(new LlmCompletionResult { Content = ModelProtocolWire.WriteAcceptedCall("Wait", null,
                                new ConversationToolCall { Name = "agents.wait", Arguments = new JObject { ["timeout_seconds"] = 1 } }) });
                        }, null, saved: saved => { if (ConversationInboxService.HasPendingWait(saved)) throw new IOException("Process lost"); }, inbox: oldInbox);
                    oldInbox.Bind(current);
                    try
                    {
                        runtime.ExecuteAsync(ChatModes.Agent, "Wait for peers", current, NewContext(adapter),
                            new AppSettings { AgentResponseMode = "json_object", StreamResponses = false, SmartChatTitles = false,
                                AutoCompressContext = false, MaxAgentIterations = 4 }, executor.GetControllerTools().ToList(),
                            null, null, null, BuiltInSkillProvider.GetSkills(adapter), CancellationToken.None).GetAwaiter().GetResult();
                        throw new InvalidOperationException("Expected interruption");
                    }
                    catch (RunStoreException) { }
                    sessions.Add(store.Load(current.Id));
                }
                var inbox = new ConversationInboxService(EventStore(store));
                var leases = ConversationLeaseService.ForOffice(paths);
                var starts = new List<string>();
                var catalog = sessions.Select(session => new AgentConversationEndpoint {
                    Address = AgentCommunicationService.Address(session), Inbox = inbox, Load = () => store.Load(session.Id),
                    Validate = current => current.DocumentKey, Reserve = () => leases.Reserve(session.Id),
                    ValidateResume = current => { if (current.Id == sessions[3].Id) throw new InvalidOperationException("Document binding changed"); },
                    Start = current => {
                        AssertTrue(sessions.Take(2).All(item => inbox.HasRunnable(store.Load(item.Id))), "all selected waits admitted before the first start");
                        starts.Add(current.Id);
                    }
                }).ToList();
                var anchor = AgentChat(store, "Ordinary entry chat");
                catalog.Add(AgentEndpoint(anchor, inbox));
                service.Register(new AgentConversationScope { Key = "office", Contains = current => current.DocumentKey == anchor.DocumentKey,
                    ReadCatalog = () => catalog });
                var page = service.ListRecoverableWaits(new AgentRecoveryQuery { Source = "office", SessionId = anchor.Id });
                AssertEqual(6, page.Participants.Count, "all saved waits available from an ordinary chat without views");
                AssertEqual(6, modelCalls, "listing never calls the model");
                AssertEqual(0, starts.Count, "inspection never resumes work");
                var targets = page.Participants.ToDictionary(item => item.SessionId, item => item.Target);
                inbox.Pause(store.Load(sessions[2].Id), "A later explicit Stop");
                var command = new AgentRecoveryCommand { Source = "office", SessionId = anchor.Id,
                    Targets = sessions.Take(5).Select(item => targets[item.Id]).Concat(new[] {
                        new AgentWaitResumeTarget { SessionId = "foreign-chat", RunId = "foreign-run", Revision = 1 } }).ToList() };
                AgentRecoveryResponse response;
                using (leases.Reserve(sessions[4].Id)) response = service.ResumeSelectedWaits(command);
                AssertTrue(response.Receipts.Select(item => item.Status).SequenceEqual(new[] { AgentWaitResumeStatus.Requested,
                    AgentWaitResumeStatus.Requested, AgentWaitResumeStatus.Changed, AgentWaitResumeStatus.Unavailable,
                    AgentWaitResumeStatus.Unavailable, AgentWaitResumeStatus.Unavailable }), "independent truthful receipts for selection, Stop, binding, lease and scope");
                AssertEqual(2, starts.Count, "unselected and rejected chats never start");
                AssertTrue(inbox.Snapshot(store.Load(sessions[5].Id)).Paused, "unselected wait stays paused");
                foreach (var session in sessions)
                {
                    var saved = store.Load(session.Id);
                    AssertEqual(session.LastRun.RunId, saved.LastRun.RunId, "no new Run");
                    AssertEqual(1, saved.LastRun.KernelState.Summary.ToolStepsUsed, "no repeat charge");
                }
                var retry = service.ResumeSelectedWaits(new AgentRecoveryCommand { Source = "office", SessionId = anchor.Id,
                    Targets = sessions.Take(2).Select(item => targets[item.Id]).ToList() });
                AssertTrue(retry.Receipts.All(item => item.Status == AgentWaitResumeStatus.Changed), "retry cannot resume a later state");
                AssertEqual(2, starts.Count, "retry never dispatches twice");
                var restartedInbox = new ConversationInboxService(EventStore(store));
                AssertTrue(sessions.Take(2).All(item => restartedInbox.Snapshot(store.Load(item.Id)).Paused),
                    "process loss after admission still requires a fresh explicit resume");
                catalog.Single(item => item.Address.ChatId == sessions[5].Id).Start = current => { throw new IOException("Start acknowledgement lost"); };
                var uncertain = service.ResumeSelectedWaits(new AgentRecoveryCommand { Source = "office", SessionId = anchor.Id,
                    Targets = new List<AgentWaitResumeTarget> { targets[sessions[5].Id] } });
                AssertEqual(AgentWaitResumeStatus.Unconfirmed, uncertain.Receipts.Single().Status, "admitted but unconfirmed start is never reported as completed or rejected");
                var stopped = store.Load(sessions[0].Id);
                AssertTrue(inbox.TryStartWorker(stopped), "queued continuation claimed");
                inbox.Pause(stopped, "Stop before binding");
                try { inbox.Bind(stopped, queuedWait: true); throw new InvalidOperationException("Stop was lost"); }
                catch (OperationCanceledException) { }
                AssertTrue(!inbox.HasRunnable(stopped) && !inbox.Snapshot(stopped).ActiveRun, "Stop wins before queued wait binding");
                inbox.EndWorker(stopped);
                AssertEqual(6, modelCalls, "only existing execution owners may perform further model calls");
            });
        }

        private static void AgentRecoveryPaging()
        {
            WithPluginPaths(paths => {
                var store = new ChatStore(paths); var inbox = new ConversationInboxService(EventStore(store));
                var chats = Enumerable.Range(0, 23).Select(i => AgentChat(store, "Idle " + i)).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
                var reads = 0;
                var catalog = chats.Select(chat => new AgentConversationEndpoint { Address = AgentCommunicationService.Address(chat), Inbox = inbox,
                    Load = () => { reads++; return chat; }, Validate = current => current.DocumentKey }).ToArray();
                var service = new AgentCommunicationService();
                service.Register(new AgentConversationScope { Key = "office", Contains = current => true, ReadCatalog = () => catalog });
                var first = service.ListRecoverableWaits(new AgentRecoveryQuery { Source = "office", SessionId = chats[0].Id });
                AssertEqual(21, reads, "one anchor plus at most 20 chat reads per explicit page");
                AssertEqual(0, first.Participants.Count, "ordinary chats are not resumed");
                AssertTrue(first.NextAfterSessionId != null, "empty page still exposes further chats");
                var second = service.ListRecoverableWaits(new AgentRecoveryQuery { Source = "office", SessionId = chats[0].Id, AfterSessionId = first.NextAfterSessionId });
                AssertEqual(25, reads, "second page reads only its three remaining chats and anchor");
                AssertTrue(second.NextAfterSessionId == null, "bounded enumeration terminates");
                try
                {
                    service.ListRecoverableWaits(new AgentRecoveryQuery { Source = "workspace", WorkspaceId = "foreign", SessionId = chats[0].Id });
                    throw new Exception("Foreign scope accepted");
                }
                catch (InvalidOperationException) { }
            });
        }

        private static void AgentRecoveryWorkspaceBatch()
        {
            WithPluginPaths(paths => {
                var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
                using (var listener = new HttpListener())
                using (var projects = new RNAssistant.Application.PluginApplicationService(paths))
                {
                    var id = projects.CreateProjectWorkspace(new ProjectConnectionRequest { RootPath = Path.Combine(paths.Root, "recovery-team") }).SelectedWorkspaceId;
                    var workspace = new WorkspaceStore(paths).OpenConnected(id);
                    var settings = new AppSettings { BaseUrl = "http://127.0.0.1:" + port, Model = "wait-model", AgentResponseMode = "json_object",
                        StreamResponses = false, SmartChatTitles = false, AutoCompressContext = false, MaxAgentIterations = 4 };
                    listener.Prefixes.Add(settings.BaseUrl + "/"); listener.Start();
                    var modelCalls = 0;
                    var server = Task.Run(async () => {
                        for (var i = 0; i < 4; i++)
                        {
                            var request = await listener.GetContextAsync().ConfigureAwait(false); Interlocked.Increment(ref modelCalls);
                            await SteeringReply(request, i < 2 ? ModelProtocolWire.WriteAcceptedCall("Wait", null,
                                new ConversationToolCall { Name = "agents.wait", Arguments = new JObject { ["timeout_seconds"] = 10 } }) :
                                ModelProtocolWire.WriteAcceptedDecision(ConversationResponse.FinishAction, "Done waiting", "Continued.",
                                    results: new ConversationResultClaim[0], outcome: ConversationOutcome.UnableToComplete)).ConfigureAwait(false);
                        }
                    });
                    try
                    {
                        string[] ids;
                        using (var runtime = new WorkspaceConversationService(paths))
                        {
                            ids = new[] { runtime.CreateSession(workspace, "First").Id, runtime.CreateSession(workspace, "Second").Id };
                            var waits = 0;
                            var runs = ids.Select(chat => runtime.SubmitInputAsync(workspace, new WorkspaceSubmitInputRequest {
                                SessionId = chat, RequestId = "initial-" + chat, Message = "Wait for peers" }, settings, () => "test", frame => {
                                    if (frame.Kind == "ToolWaiting" && Interlocked.Increment(ref waits) == 2)
                                        Task.Run(async () => { await Task.Delay(50).ConfigureAwait(false); await runtime.CloseAsync().ConfigureAwait(false); });
                                })).ToArray();
                            AssertTrue(Task.WaitAll(runs, 6000), "both concurrent waits persist before shutdown");
                            AssertTrue(runs.All(run => run.Result.Summary.Lifecycle == RunLifecycle.Waiting), "shutdown retains both waits");
                        }
                        var store = new ChatStore(paths); var saved = ids.Select(store.Load).ToArray();
                        using (var runtime = new WorkspaceConversationService(paths))
                        {
                            runtime.RegisterAgentScope(workspace, settings, () => "test");
                            var page = runtime.AgentCommunication.ListRecoverableWaits(new AgentRecoveryQuery {
                                Source = "workspace", WorkspaceId = id, SessionId = ids[0] });
                            AssertEqual(2, page.Participants.Count, "both cold waits discovered without tabs");
                            AssertEqual(2, modelCalls, "cold inspection performs no model calls");
                            var result = runtime.AgentCommunication.ResumeSelectedWaits(new AgentRecoveryCommand { Source = "workspace",
                                WorkspaceId = id, SessionId = ids[0], Targets = page.Participants.Select(item => item.Target).ToList() });
                            AssertTrue(result.Receipts.All(item => item.Status == AgentWaitResumeStatus.Requested), "one explicit command admits both participants");
                            AssertTrue(SpinWait.SpinUntil(() => runtime.ActiveChatRuns().Count == 2, 2000), "both resumed waits have concurrent owners without views");
                            AssertTrue(runtime.RunsDrained.Wait(15000), "both restored waits complete concurrently");
                            foreach (var previous in saved)
                            {
                                var current = store.Load(previous.Id);
                                AssertEqual(previous.LastRun.RunId, current.LastRun.RunId, "same run after restart");
                                AssertEqual(RunLifecycle.Completed, current.LastRun.KernelState.Summary.Lifecycle, "restored participant completes");
                                AssertEqual(1, current.LastRun.KernelState.Summary.ToolStepsUsed, "one charge per wait");
                                var terminal = current.Messages.Single(item => item.Activity == null && item.ExecutionProgress != null);
                                AssertEqual(previous.LastRun.KernelState.Summary.PendingWait.Context.Call.Id, terminal.ToolCallId, "same accepted call");
                            }
                        }
                        AssertTrue(server.Wait(1000), "exactly four model requests, no replay");
                    }
                    finally { listener.Stop(); try { server.Wait(1000); } catch (AggregateException) { } }
                }
            });
        }
    }
}
