using System;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RNAssistant.Application.Conversations;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Runtime;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static AgentConversationScope StoredAgentScope(ChatStore store, ConversationInboxService inbox,
            ChatSession source, Action<ChatSession> start = null)
        {
            Func<ChatSession, AgentConversationEndpoint> endpoint = session => new AgentConversationEndpoint {
                Address = AgentCommunicationService.Address(session), Inbox = inbox,
                Load = () => store.Load(session.Id), Validate = current => current.DocumentKey,
                Start = start ?? (_ => { }) };
            return new AgentConversationScope {
                Key = AgentScopeKey(source),
                Contains = session => AgentScopeKey(session) == AgentScopeKey(source),
                ReadCatalog = () => store.ListHeaders(false).Where(header => header.Host == source.Host && header.DocumentKey == source.DocumentKey)
                    .Select(header => endpoint(store.Load(header.Id))).ToArray(),
                Prepare = (parent, title) => store.CreateTransient(parent.Host, parent.DocumentKey, parent.DocumentTitle, title),
                Commit = child => store.Commit(child, new ConversationCommit()), Endpoint = endpoint
            };
        }

        private static void AgentSpawnDurableIdentityAndInheritance()
        {
            WithTempPaths(paths => {
                var store = new ChatStore(paths); var inbox = new ConversationInboxService(new ChatEventStoreAdapter(store));
                var service = new AgentCommunicationService(); var parent = AgentChat(store, "Parent");
                parent.Mode = ChatModes.Plan; parent.MutationPolicy = MutationPolicy.Deny;
                parent.Model = "inherited-model"; parent.ReasoningEnabled = true; store.Save(parent);
                var started = 0; var dispatched = 0;
                service.Register(StoredAgentScope(store, inbox, parent, _ => started++));
                var result = service.Spawn(inbox, parent, 20, "run", "spawn-one", "Verifier", null, () => dispatched++);
                AssertTrue(result.Created && result.InitialMessage == "not_requested", "creation is distinct from input and execution");
                AssertEqual(0, started, "idle creation never starts a run");
                var child = store.List().Single(chat => chat.Id != parent.Id);
                AssertEqual(result.AgentId, AgentChatAddress.IdFor(child.Id), "receipt identifies the durable ordinary chat");
                AssertTrue(child.UsesCurrentProtocol && child.Messages.Count == 0 && child.LastRun == null, "no parent history or independent runtime");
                AssertTrue(child.Mode == ChatModes.Plan && child.MutationPolicy == MutationPolicy.Deny &&
                    child.Model == parent.Model && child.ReasoningEnabled, "model and restrictions are inherited");
                AssertEqual(parent.Id, child.AgentCreation.Creator.ChatId, "creation provenance survives reload");
                var again = service.Spawn(inbox, parent, 20, "run", "spawn-one", "Verifier", null, () => dispatched++);
                AssertEqual(result.AgentId, again.AgentId, "same operation returns same participant");
                AssertEqual(1, dispatched, "duplicate creation does not dispatch");
                AssertEqual(2, service.List(parent).Count, "new participant is discovered without opening a view");
                var conflict = false;
                try { service.Spawn(inbox, parent, 20, "run", "spawn-one", "Other", null, () => { }); }
                catch (AgentCommunicationException error) { conflict = error.Code == "agent_operation_conflict"; }
                AssertTrue(conflict && store.ListHeaders(false).Count == 2, "changed duplicate is rejected");
            });
        }

        private static void AgentSpawnInitialDeliveryAndScope()
        {
            WithTempPaths(paths => {
                var store = new ChatStore(paths); var inbox = new ConversationInboxService(new ChatEventStoreAdapter(store));
                var service = new AgentCommunicationService(); var parent = AgentChat(store, "Parent");
                var peer = AgentChat(store, "Peer");
                service.Register(StoredAgentScope(store, inbox, parent));
                service.Send(inbox, parent, 3, "run", "recruit-peer", AgentChatAddress.IdFor(peer.Id), "Question", () => { });
                var failingScope = StoredAgentScope(store, inbox, parent);
                var commit = failingScope.Commit;
                failingScope.Commit = child => { commit(child); child.MutationPolicy = MutationPolicy.Ask; store.Save(child); };
                parent = store.Load(parent.Id); parent.MutationPolicy = MutationPolicy.Deny; store.Save(parent);
                service.Register(failingScope);
                var result = service.Spawn(inbox, parent, 3, "run", "created-but-not-sent", "New peer", "Work", () => { });
                AssertTrue(result.Created && result.InitialMessage == "rejected" && result.Error == "agent_chat_unavailable",
                    "initial input failure preserves the successful creation receipt");
                AssertEqual(3, store.ListHeaders(false).Count, "one participant was saved despite input refusal");
                var foreign = store.CreateTransient("Excel", "foreign-document", "Foreign", "Foreign");
                store.Commit(foreign, new ConversationCommit());
                service.Register(StoredAgentScope(store, inbox, foreign));
                AssertEqual(3, service.List(parent).Count, "discovery stays in the source scope");
                var denied = false;
                try { service.Send(inbox, peer, 20, "run", "foreign-send", AgentChatAddress.IdFor(foreign.Id), "Work", () => { }); }
                catch (AgentCommunicationException error) { denied = error.Code == "agent_chat_not_found"; }
                AssertTrue(denied && inbox.Snapshot(foreign).Items.Count == 0, "knowing an address does not grant cross-scope delivery");
            });
        }

        private static void AgentSpawnBudgetRecoveryAndUncertainty()
        {
            WithTempPaths(paths => {
                var store = new ChatStore(paths); var inbox = new ConversationInboxService(new ChatEventStoreAdapter(store));
                var service = new AgentCommunicationService(() => 4); var parent = AgentChat(store, "Parent");
                service.Register(StoredAgentScope(store, inbox, parent));
                var first = service.Spawn(inbox, parent, 20, "run", "first", "Child", "Join", () => { });
                AssertEqual("queued", first.InitialMessage, "first input is durably queued");
                var child = store.List().Single(chat => chat.Id != parent.Id);
                var input = inbox.TakeNext(child);
                var message = new ChatMessage { Id = input.Id, Role = "user", Content = input.Text, AgentCommunication = input.AgentCommunication };
                child.Messages.Add(message); store.Commit(child, ConversationCommit.UserInput(message)); inbox.Applied(child, input.Id);
                for (var i = 1; i < 3; i++)
                    service.Spawn(inbox, child, 20, "child-run", "nested-" + i, "Nested " + i, null, () => { });
                inbox = new ConversationInboxService(new ChatEventStoreAdapter(store));
                service = new AgentCommunicationService(() => 4); parent = store.Load(parent.Id);
                service.Register(StoredAgentScope(store, inbox, parent));
                var limited = false;
                try { service.Spawn(inbox, parent, 20, "later-run", "excess", "Excess", null, () => { }); }
                catch (AgentCommunicationException error) { limited = error.Code == "agent_participant_limit"; }
                AssertTrue(limited && store.ListHeaders(false).Count == 4, "nested creation shares the root limit across restart");
            });
            WithTempPaths(paths => {
                var store = new ChatStore(paths); var inbox = new ConversationInboxService(new ChatEventStoreAdapter(store));
                var service = new AgentCommunicationService(() => 4); var parent = AgentChat(store, "Parent");
                var scope = StoredAgentScope(store, inbox, parent); scope.Commit = _ => { throw new IOException("Injected creation failure"); };
                service.Register(scope);
                try { service.Spawn(inbox, parent, 20, "run", "uncertain", "Child", null, () => { }); }
                catch (IOException) { }
                service.Register(StoredAgentScope(store, inbox, parent));
                var refused = false;
                try { service.Spawn(inbox, parent, 20, "run", "uncertain", "Child", null, () => { throw new Exception("Replayed creation"); }); }
                catch (AgentCommunicationException error) { refused = error.Code == "agent_spawn_unknown"; }
                AssertTrue(refused && store.ListHeaders(false).Count == 1, "unconfirmed creation is not replayed");
            });
        }

        private static void AgentSpawnWorkspaceWire()
        {
            WithPluginPaths(paths => {
                var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
                using (var listener = new HttpListener())
                using (var runtime = new WorkspaceConversationService(paths))
                {
                    var workspaces = new WorkspaceStore(paths);
                    // Reuse the production project connection path without opening any Chat View.
                    using (var projects = new RNAssistant.Application.PluginApplicationService(paths))
                    {
                        var id = projects.CreateProjectWorkspace(new RNAssistant.Application.Contracts.ProjectConnectionRequest {
                            RootPath = Path.Combine(paths.Root, "spawn-wire") }).SelectedWorkspaceId;
                        var workspace = workspaces.OpenConnected(id);
                        var root = runtime.CreateSession(workspace, "Parent"); var store = new ChatStore(paths);
                        var settings = new AppSettings { BaseUrl = "http://127.0.0.1:" + port, Model = "spawn-model",
                            AgentResponseMode = "json_object", StreamResponses = false, SmartChatTitles = false,
                            AutoCompressContext = false, MaxAgentIterations = 4, AutoConfirmToolActions = true };
                        listener.Prefixes.Add(settings.BaseUrl + "/"); listener.Start();
                        var children = 0; var parentCalls = 0;
                        var server = Task.Run(async () => {
                            for (var i = 0; i < 3; i++)
                            {
                                var request = await listener.GetContextAsync().ConfigureAwait(false);
                                string body; using (var reader = new StreamReader(request.Request.InputStream)) body = await reader.ReadToEndAsync().ConfigureAwait(false);
                                var chat = store.List().FirstOrDefault(item => item.AgentCreation != null);
                                var childRequest = body.Contains("SPAWN_CHILD_START") && !body.Contains("SPAWN_PARENT_START");
                                string response;
                                if (childRequest)
                                {
                                    children++; AssertContains(body, "not the human user", "spawn input retains peer provenance");
                                    response = ModelProtocolWire.WriteAcceptedDecision(ConversationResponse.FinishAction, "Reviewed", "Review complete",
                                        results: new ConversationResultClaim[0], outcome: ConversationOutcome.Completed);
                                }
                                else if (++parentCalls == 1)
                                    response = new ConversationResponse(ConversationResponse.ExecuteAction, "Invite a peer", null,
                                        calls: new[] { new ConversationToolCall { Name = AgentCommunicationToolCatalog.Spawn,
                                            Arguments = new JObject { ["title"] = "Peer", ["initial_message"] = "SPAWN_CHILD_START" } } }).ToJson();
                                else
                                    response = ScriptedVerifiedFinish(store.Load(root.Id), "Participant created", "Peer invited", "Created and queued only", AgentCommunicationToolCatalog.Spawn);
                                await SteeringReply(request, response).ConfigureAwait(false);
                            }
                        });
                        try
                        {
                            var run = runtime.SubmitInputAsync(workspace, new WorkspaceSubmitInputRequest {
                                SessionId = root.Id, RequestId = "spawn-human", Message = "SPAWN_PARENT_START", AutoConfirmToolActions = false }, settings, () => "");
                            AssertTrue(run.Wait(15000) && runtime.RunsDrained.Wait(10000) && server.Wait(5000),
                                "parent and child run through the common production kernel without views");
                            AssertEqual(1, children, "one child execution");
                            var child = store.List().Single(item => item.AgentCreation != null);
                            AssertTrue(!child.LastRun.ConversationPolicy.AutoConfirmsToolActions, "peer input cannot inherit broader global auto approval");
                            AssertEqual(1, child.Messages.Count(item => item.Content == "SPAWN_CHILD_START"), "one durable initial input");
                            AssertTrue(child.LastRun.KernelState.Summary.Lifecycle == RNAssistant.Core.Agent.RunLifecycle.Completed,
                                "ordinary child Run finishes independently");
                        }
                        finally { listener.Stop(); runtime.CloseAsync().Wait(5000); try { server.Wait(1000); } catch (AggregateException) { } }
                    }
                }
            });
        }

        private static void AgentPeerPermissionCeiling()
        {
            WithTempPaths(paths => {
                var store = new ChatStore(paths); var inbox = new ConversationInboxService(new ChatEventStoreAdapter(store));
                var service = new AgentCommunicationService(); var sender = AgentChat(store, "Sender"); var receiver = AgentChat(store, "Receiver");
                RegisterAgent(service, AgentEndpoint(sender, inbox)); RegisterAgent(service, AgentEndpoint(receiver, inbox));
                sender.Mode = ChatModes.Plan;
                var refused = false;
                try { service.Send(inbox, sender, 20, "sender-run", "plan-delegate", AgentChatAddress.IdFor(receiver.Id), "Work", () => { }); }
                catch (AgentCommunicationException error) { refused = error.Code == "agent_chat_unavailable"; }
                AssertTrue(refused && inbox.Snapshot(receiver).Items.Count == 0, "Plan cannot delegate to a more permissive chat");
                sender.Mode = ChatModes.Agent;
                receiver.LastRun = new ChatRunRecord { RunId = "receiver-run", TurnId = "receiver-turn",
                    ConversationPolicy = ConversationRunPolicy.ForSession(receiver, true) };
                inbox.Bind(receiver);
                service.Send(inbox, sender, 20, "sender-run", "lower-approval", AgentChatAddress.IdFor(receiver.Id), "Work", () => { });
                AssertEqual(0, inbox.TakeRunInput(receiver).Count, "restricted input is not included in a run with automatic approval");
                AssertEqual(AgentWaitStatus.InputPending, WaitForAgentTest(service, inbox, receiver, 20, "receiver-run", 1, CancellationToken.None)
                    .GetAwaiter().GetResult().Status, "wait yields to queued input needing another permission policy");
                var input = inbox.Get(receiver).Inputs.Single();
                var policy = ConversationRunPolicy.ForSession(receiver, true).ForPeerInput(input.AgentCommunication);
                AssertTrue(!policy.AutoConfirmsToolActions, "queued input caps global automatic approval");
                receiver.LastRun.ConversationPolicy = policy;
                AssertEqual(1, inbox.TakeRunInput(receiver).Count, "a compatible run can include the input");
            });
        }

        private static void AgentSpawnUnknownInitialReceipt()
        {
            WithTempPaths(paths => {
                var store = new ChatStore(paths); var events = new ChatEventStoreAdapter(store);
                var inbox = new ConversationInboxService(events); var failing = new InboxFailingEvents(events) { Fail = true };
                var targetInbox = new ConversationInboxService(failing);
                var source = AgentChat(store, "Source"); var service = new AgentCommunicationService();
                var scope = StoredAgentScope(store, inbox, source); var catalog = scope.ReadCatalog;
                scope.ReadCatalog = () => catalog().Select(endpoint => {
                    if (endpoint.Address.ChatId != source.Id) endpoint.Inbox = targetInbox;
                    return endpoint;
                }).ToArray();
                service.Register(scope);
                var handler = new AgentCommunicationToolHandler(service, inbox, source, 20);
                Func<ToolHandlerContext> context = () => new ToolHandlerContext(new ToolRuntimeFixture().Context(toolId: AgentCommunicationToolCatalog.Spawn),
                    new System.Collections.Generic.Dictionary<string, object> { ["title"] = "Child", ["initial_message"] = "Work" });
                var first = handler.ExecuteAsync(context(), CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(ToolEffectEvidence.Unknown, first.Effect, "created child with uncertain delivery has an unknown overall effect");
                failing.Fail = false;
                var repeatedContext = context();
                var second = handler.ExecuteAsync(repeatedContext, CancellationToken.None).GetAwaiter().GetResult();
                AssertTrue(!repeatedContext.MayHaveDispatched && second.Effect == ToolEffectEvidence.VerifiedNoChange,
                    "historical uncertainty does not invent a new dispatch or replay the first message");
                AssertEqual("unknown", (string)JObject.Parse(second.Result.DataJson)["InitialMessage"], "receipt preserves uncertainty separately from current invocation");
                AssertEqual(2, store.ListHeaders(false).Count, "uncertain initial message never creates a replacement chat");
            });
        }

        private static void AgentSpawnConcurrentBudget()
        {
            WithTempPaths(paths => {
                var store = new ChatStore(paths); var inbox = new ConversationInboxService(new ChatEventStoreAdapter(store));
                var source = AgentChat(store, "Source");
                var peers = Enumerable.Range(0, 5).Select(i => AgentChat(store, "Existing " + i)).ToArray();
                var services = new[] { new AgentCommunicationService(() => 5), new AgentCommunicationService(() => 5) };
                foreach (var service in services) service.Register(StoredAgentScope(store, inbox, source));
                var admitted = 0; var spawned = 0; var limited = 0;
                Task.WaitAll(Enumerable.Range(0, 10).Select(index => Task.Run(() => {
                    try
                    {
                        if (index % 2 == 0)
                            services[0].Send(inbox, source, 20, "run", "parallel-" + index, AgentChatAddress.IdFor(peers[index / 2].Id), "Join", () => { });
                        else
                        {
                            services[1].Spawn(inbox, source, 20, "run", "parallel-" + index, "Child " + index, null, () => { });
                            Interlocked.Increment(ref spawned);
                        }
                        Interlocked.Increment(ref admitted);
                    }
                    catch (AgentCommunicationException error) when (error.Code == "agent_participant_limit") { Interlocked.Increment(ref limited); }
                })).ToArray());
                AssertTrue(admitted == 4 && limited == 6 && store.ListHeaders(false).Count == 6 + spawned,
                    "existing and spawned peers compete for the same four places beside the root");
                AssertEqual(4, inbox.AgentEvents(source).Count(e => e.State == AgentDeliveryState.ParticipantReserved), "no oversubscription across bound services");
            });
        }
    }
}
