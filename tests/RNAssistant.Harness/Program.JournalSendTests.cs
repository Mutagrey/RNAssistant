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
        private static void JournalSendParity(bool loseAcknowledgement, bool loseReadBack = false, bool spawn = false, bool idle = false, bool assignments = false)
        {
            foreach (var office in new[] { true, false }) WithJournalInput(office, (paths, journal, source, scopeKey) => {
                IConversationStore store = journal;
                var workspace = office ? null : new WorkspaceStore(paths).OpenConnected(source.WorkspaceId);
                var target = spawn ? null : store.CreateTransient(source.Host, source.DocumentKey, source.DocumentTitle, "Recipient");
                if (target != null) store.Commit(target, new ConversationCommit());
                store.SaveActiveSessionId(source.Host, source.DocumentKey, source.Id);
                var sourceAnchor = new SessionEventPosition(source.Id, source.Revision, source.StorageHeadHash);
                if (idle) { source.Mode = ChatModes.Plan; source.MutationPolicy = MutationPolicy.Deny; source.ReasoningEnabled = true; }
                var inbox = new ConversationInboxService(journal);
                var targetBox = target == null ? null : inbox.Get(target);
                if (!spawn && !loseAcknowledgement && !loseReadBack) inbox.Pause(target, "User stop");
                var notification = targetBox?.InputChanged.Task;
                var starts = 0; var validations = 0; var catalogReads = 0; var idleNotices = 0;
                var settings = new AppSettings { Model = "journal-send", MaxCollaborationAgents = 2,
                    StreamResponses = false, SmartChatTitles = false, AutoCompressContext = false,
                    ContextWindowOverrideTokens = 65536, RequestTimeoutSeconds = 5, MaxAgentFormatRetries = 0 };
                var communication = new AgentCommunicationService(() => 2);
                Func<AgentChatAddress, AgentConversationEndpoint> endpoint = address => new AgentConversationEndpoint {
                    Address = address, Inbox = inbox, Load = () => store.Load(address.ChatId),
                    Validate = chat => {
                        if (++validations == 1 && loseReadBack)
                            journal.Fault = point => { if (point == (spawn ? "before_spawn_readback" : "before_peer_readback")) throw new IOException("peer receipt unavailable"); };
                        if (validations == 1 && loseAcknowledgement)
                            journal.Journal.Fault = point => { if (point == "after_flush") {
                                journal.Journal.Fault = null; throw new IOException("lost peer acceptance acknowledgement"); } };
                        return chat.WorkspaceId ?? chat.DocumentKey;
                    }, Changed = chat => {
                        idleNotices++;
                        if (idle) throw new InvalidOperationException("fixture: detached view");
                    }, Start = chat => {
                        AssertEqual(store.ReadMetadata(chat.Id).Revision, chat.Revision, "live recipient cursor advances before scheduling");
                        if (spawn && chat.AgentCreation != null) { target = chat; AssertTrue(!inbox.Snapshot(chat).Paused, "proven fresh child can start without a recovery pause"); }
                        starts++;
                    } };
                communication.Register(new AgentConversationScope { Key = scopeKey,
                    Contains = chat => chat != null && chat.DocumentKey == source.DocumentKey && chat.Host == source.Host,
                    Endpoint = endpoint,
                    Prepare = (parent, title) => {
                        var child = store.CreateTransient(parent.Host, parent.DocumentKey, parent.DocumentTitle, title);
                        child.DocumentAuthorityId = parent.DocumentAuthorityId; child.DocumentPath = parent.DocumentPath;
                        return child;
                    }, Commit = child => { throw new Exception("Native spawn must not use a second Store commit."); },
                    ReadCatalog = () => {
                        catalogReads++;
                        return new[] { endpoint(AgentCommunicationService.Address(source)), endpoint(AgentCommunicationService.Address(target)) };
                    } });
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var resources = new ResourceJournal(paths); var payloads = new ChatBlobStore(paths);
                var files = new WorkspaceFileService(paths, resources, resources, resources);
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths), new ToolStore(paths),
                    () => settings, paths: paths, resourceAuthorityStore: resources, resourceMutationJournal: resources,
                    resourceOrigin: (session, context) => journal.MutationOrigin(session, context));
                executor.AgentCommunication = communication; executor.AgentInbox = inbox;
                var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
                settings.BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
                string requestId = null; var round = 0;
                using (var listener = new HttpListener())
                {
                    listener.Prefixes.Add(settings.BaseUrl + "/"); listener.Start();
                    var provider = Task.Run(async () => {
                        try
                        {
                            while (true)
                            {
                                var request = await listener.GetContextAsync().ConfigureAwait(false);
                                string wire; using (var reader = new StreamReader(request.Request.InputStream)) wire = await reader.ReadToEndAsync().ConfigureAwait(false);
                                if (++round > (assignments ? 3 : 2))
                                    foreach (var message in JObject.Parse(wire)["messages"])
                                    {
                                        var content = (string)message["content"] ?? "";
                                        var feedback = content.IndexOf("completionFeedback:", StringComparison.Ordinal);
                                        if (feedback >= 0) Console.WriteLine(content.Substring(feedback, Math.Min(1400, content.Length - feedback)));
                                    }
                                AssertTrue(round <= (assignments ? 3 : 2), "send or resolve is never retried by the model loop");
                                if (assignments && round == 3) AssertContains(wire, requestId == null ? "Open" : "ResolutionReceived", "native assignment page reaches the real model wire");
                                if (requestId != null) AssertContains(wire, "Inspect this exact assignment", "recipient model receives peer provenance and original text");
                                var response = round == 1 ? ModelProtocolWire.WriteAcceptedCall("Contact the exact peer.", null,
                                    new ConversationToolCall { Name = requestId == null ? (spawn ? AgentCommunicationToolCatalog.Spawn : AgentCommunicationToolCatalog.Send) : AgentCommunicationToolCatalog.Resolve,
                                        Arguments = requestId == null ? spawn ? idle ? new JObject { ["title"] = "Native child" } :
                                            new JObject { ["title"] = "Native child", ["initial_message"] = "Inspect this exact assignment",
                                                ["assignment"] = true, ["expect_reply"] = true } : new JObject { ["agent_id"] = AgentChatAddress.IdFor(target.Id),
                                            ["text"] = "Inspect this exact assignment", ["assignment"] = true, ["expect_reply"] = true }
                                            : new JObject { ["request_id"] = requestId, ["outcome"] = "completed", ["text"] = "Exact native completion" } })
                                    : assignments && round == 2 ? ModelProtocolWire.WriteAcceptedCall("Read this chat's assignments.", null,
                                        new ConversationToolCall { Name = AgentCommunicationToolCatalog.List,
                                            Arguments = new JObject { ["assignments"] = requestId == null ? "outgoing" : "incoming" } })
                                    : ModelProtocolWire.WriteAcceptedDecision("finish", "The message attempt ended.", "Delivery observed.",
                                        outcome: ConversationOutcome.Completed, results: new[] {
                                            new ConversationResultClaim("Peer intake only; no external completion is claimed.", new[] { "E1" },
                                                ConversationAssessment.Satisfied) });
                                await SteeringReply(request, response).ConfigureAwait(false);
                            }
                        }
                        catch (HttpListenerException) { }
                        catch (ObjectDisposedException) { }
                        catch (Exception error) { Console.WriteLine(error); listener.Abort(); throw; }
                    });
                    try
                    {
                        Action<ChatSession, ConversationInput> run = (chat, input) => {
                            var id = Guid.NewGuid().ToString("N");
                            chat.LastRun = new ChatRunRecord { RunId = id, TurnId = id, StartedUtc = DateTime.UtcNow,
                                ConversationPolicy = ConversationRunPolicy.ForSession(chat),
                                ModelConfiguration = spawn ? new ModelRunMetadata { Model = settings.Model, ReasoningEnabled = chat.ReasoningEnabled } : null };
                            var user = input == null ? new ChatMessage { Role = "user", Content = "Ask the peer to inspect the assignment.", RunId = id }
                                : new ChatMessage { Id = input.Id, Role = "user", Content = input.Text, RunId = id, AgentCommunication = input.AgentCommunication };
                            chat.Messages.Add(user); store.Commit(chat, ConversationCommit.UserInput(user));
                            if (input != null) inbox.Applied(chat, input.Id);
                            inbox.Bind(chat);
                            if (office)
                            {
                                var client = new LlmClient(() => "fixture-key");
                                var service = new ConversationRunService(adapter, executor, store, journal,
                                    (config, messages, options, stream, token) => client.CompleteAsync(config, messages, options, stream, token), null, inbox: inbox);
                                service.ExecuteAsync(chat.Mode, user.Content, chat, chat.Context, settings,
                                    executor.GetControllerTools().ToArray(), null, null, null, null, CancellationToken.None,
                                    appendUserMessage: false).GetAwaiter().GetResult();
                            }
                            else
                            {
                                var ports = new WorkspaceConversationService.WorkspacePorts(paths, store, journal,
                                    new DocumentArtifactStore(resources, resources, payloads), files, payloads,
                                    new WorkspaceWebSnapshotStore(paths, files), workspace, chat, settings, () => "fixture-key", null,
                                    agentCommunication: communication, agentInbox: inbox, resourceAuthority: resources, resourceMutations: resources);
                                ConversationRunCoordinator.RunAsync(new AgentRunRequest(id, id, user.Content, new AgentRunLimits(5, 5)),
                                    ports, ports.Tools, ports, CancellationToken.None).GetAwaiter().GetResult();
                            }
                            inbox.End(chat, true, false);
                        };
                        if (loseReadBack)
                        {
                            RuntimeThrows<RunStoreException>(() => run(source, null)); journal.Fault = null;
                            source = RecoverJournalRun(store, store.Load(source.Id), office);
                            // The interrupted fixture's bound owner is gone after
                            // recovery; do not reuse its stale live Inbox projection.
                            inbox = new ConversationInboxService(journal); executor.AgentInbox = inbox;
                        }
                        else run(source, null);
                        if (!spawn) AssertEqual(!loseReadBack, notification.IsCompleted, "only proven intake wakes the existing live Inbox signal");
                        AssertEqual(loseReadBack || idle ? 0 : 1, starts, "idle or unconfirmed creation is not scheduled");
                        if (!spawn && !loseAcknowledgement && !loseReadBack)
                            AssertTrue(targetBox.Paused && !inbox.TryStartWorker(targetBox.Session), "peer intake cannot release a user-stopped queue");
                        AssertEqual(0, catalogReads, "known-address delivery never enumerates the participant catalog");
                        var send = journal.ReadWindow(source.Id, long.MaxValue, 8).Single(item => item.Fact.Result.ToolName != AgentCommunicationToolCatalog.List);
                        AssertEqual(loseReadBack ? ToolExecutionOutcome.Unknown : ToolExecutionOutcome.Ok,
                            send.Fact.Result.ExecutionProgress.Outcome, "only exact canonical read-back proves intake");
                        if (spawn)
                        {
                            var headers = store.ListHeaders(false);
                            AssertEqual(2, headers.Count, "spawn creates exactly one Store-visible child");
                            AssertEqual(source.Id, headers.Single(header => header.Id != source.Id).CreatorChatId,
                                "native catalog header retains the creator without loading child history");
                            target = store.Load(headers.Single(header => header.Id != source.Id).Id);
                            var receipt = journal.SpawnReceipt(target.AgentCreation.OperationId);
                            AssertEqual(send.Fact.Result.ToolCallId, receipt.Fact.CallId, "spawn terminal retains exact accepted call");
                            AssertEqual(source.Id, store.LoadActiveSessionId(source.Host, source.DocumentKey), "spawn never selects its child");
                            AssertEqual(0, target.Messages.Count, "spawn does not copy the parent's history");
                            AssertTrue(target.LastRun == null && target.TitleAssignedByAgent, "fresh child has no automatic run");
                            AssertEqual(source.LastRun.ModelConfiguration.Model, target.Model, "child inherits the effective model");
                            AssertEqual(source.Mode, target.Mode, "child inherits the accepted mode");
                            AssertEqual(source.MutationPolicy, target.MutationPolicy, "child cannot broaden mutation permissions");
                            AssertEqual(source.ReasoningEnabled, target.ReasoningEnabled, "child inherits reasoning preference");
                            AssertTrue(!receipt.Fact.Child.Policy.AutoConfirmToolActions, "child metadata never grants automatic approval");
                            var events = (IEventStore)journal;
                            var sourceEvents = events.Read(ConversationMetadata.From(source), SessionEventReadMode.RequireComplete);
                            AssertEqual(1, sourceEvents.Count(item => item.EventId == receipt.Operation), "spawn appears once in source history");
                            var feed = events.OpenReader(ConversationMetadata.From(source)).Read(sourceAnchor, null, 100, CancellationToken.None);
                            AssertEqual(1, feed.Events.Count(item => item.EventId == receipt.Operation && item.Type == "agent.spawned"), "source feed projects native spawn once");
                            AssertEqual(receipt.Position.Hash, target.StorageHeadHash, "child chat and initial input share one frame");
                            AssertEqual(1, events.Read(ConversationMetadata.From(target), SessionEventReadMode.RequireComplete).Count, "child begins with one canonical frame");
                            if (idle)
                            {
                                AssertEqual(1, idleNotices, "idle creation notifies once and a detached view cannot erase the receipt");
                                AssertEqual(0, inbox.ReadUndelivered(target).Count, "idle child has no input");
                                store.SetTitle(target, "Renamed child");
                                journal.Rebuild(); target = store.Load(target.Id);
                                AssertEqual("Renamed child", target.Title, "normal Store operations retain native child provenance");
                                AssertEqual(receipt.Operation, target.AgentCreation.OperationId, "rebuild retains creation identity");
                                return;
                            }
                        }
                        var accepted = new ConversationInboxService(journal).ReadUndelivered(store.Load(target.Id)).Single();
                        requestId = accepted.AgentCommunication.MessageId;
                        var open = communication.Assignments(inbox, source, 2, "outgoing", null).Items.Single();
                        AssertEqual(requestId, open.RequestId, "assignment projection reads the same atomic request");
                        AssertEqual(AgentAssignmentState.Open, open.State, "canonical intake remains distinct from an unknown source terminal");
                        var delivery = journal.AgentMessage(requestId);
                        AssertEqual(send.Fact.Result.ToolCallId, delivery.Fact.CallId, "tool terminal refers to the exact native delivery call");
                        AssertEqual(source.LastRun.RunId, delivery.Fact.RunId, "acceptance belongs to the actual sender run");
                        journal.Rebuild();
                        source = store.Load(source.Id); target = store.Load(target.Id);
                        AssertEqual(send.Position.Hash, journal.ReadWindow(source.Id, long.MaxValue, 8).Single(item => item.Fact.Result.ToolName != AgentCommunicationToolCatalog.List).Position.Hash, "rebuild preserves even an unknown terminal");
                        // Restart does not schedule retained input; explicit resume uses
                        // the same Inbox and real Office/Workspace run consumers.
                        inbox = new ConversationInboxService(journal); executor.AgentInbox = inbox;
                        AssertTrue(inbox.Snapshot(target).Paused && !inbox.TryStartWorker(target), "cold native intake does not restart itself");
                        AssertEqual(loseReadBack ? 0 : 1, starts, "rebuild never redispatches a send");
                        inbox.Resume(target); var incoming = inbox.TakeNext(target);
                        round = 0; run(target, incoming);
                        AssertEqual(loseReadBack ? 1 : 2, starts, "resolve schedules one new correlated input");
                        AssertEqual(0, catalogReads, "resolve also uses exact indexed addressing");
                        var resolved = journal.ReadWindow(target.Id, long.MaxValue, 8).Single(item => item.Fact.Result.ToolName != AgentCommunicationToolCatalog.List);
                        AssertEqual(ToolExecutionOutcome.Ok, resolved.Fact.Result.ExecutionProgress.Outcome, "real resolve has its native receipt");
                        source = store.Load(source.Id);
                        var completion = inbox.ReadUndelivered(source).Single();
                        AssertEqual(requestId, completion.AgentCommunication.ReplyTo, "resolution retains request identity across restart");
                        AssertEqual(AgentAssignmentOutcome.Completed, completion.AgentCommunication.Resolution.Value, "completion remains the peer's report");
                        AssertEqual(ConversationInputStatus.Applied, journal.InputState(requestId).Status, "the original assignment is applied once");
                        var page = communication.Assignments(inbox, source, 2, "outgoing", null).Items.Single();
                        AssertTrue(page.State == AgentAssignmentState.ResolutionReceived && page.InputStatus == ConversationInputStatus.Pending &&
                            page.ReportedOutcome == AgentAssignmentOutcome.Completed && page.Summary == "Inspect this exact assignment",
                            "projection exposes report receipt without consuming or exposing its pending body");
                        AssertEqual(delivery.Position.Hash, journal.AgentMessage(requestId).Position.Hash, "reply never rewrites original acceptance");
                        if (!office)
                        {
                            var backup = Path.Combine(paths.Root, "send-backup"); JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                            var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "send-restore"), _ => StorageProtector.None);
                            var copy = new ConversationJournal(restored, scopeKey);
                            AssertEqual(send.Position.Hash, copy.ReadWindow(source.Id, long.MaxValue, 8).Single(item => item.Fact.Result.ToolName != AgentCommunicationToolCatalog.List).Position.Hash, "restore preserves the original sender outcome without dispatch");
                            AssertEqual(AgentAssignmentOutcome.Completed, new ConversationInboxService(copy)
                                .ReadUndelivered(((IConversationStore)copy).Load(source.Id)).Single().AgentCommunication.Resolution.Value,
                                "restore retains native completion input");
                            if (spawn) AssertEqual(target.AgentCreation.OperationId, ((IConversationStore)copy).Load(target.Id).AgentCreation.OperationId,
                                "restore retains native child metadata and context CAS");
                        }
                    }
                    finally { journal.Journal.Fault = null; journal.Fault = null; try { listener.Stop(); } catch (ObjectDisposedException) { }
                        AssertTrue(provider.Wait(5000), "native send provider stops"); }
                }
            });
        }
    }
}
