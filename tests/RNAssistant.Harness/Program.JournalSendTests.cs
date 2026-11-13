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
        private static void JournalSendParity(bool loseAcknowledgement, bool loseReadBack = false)
        {
            foreach (var office in new[] { true, false }) WithJournalInput(office, (paths, journal, source, scopeKey) => {
                IConversationStore store = journal;
                var workspace = office ? null : new WorkspaceStore(paths).OpenConnected(source.WorkspaceId);
                var target = store.CreateTransient(source.Host, source.DocumentKey, source.DocumentTitle, "Recipient");
                store.Commit(target, new ConversationCommit());
                var inbox = new ConversationInboxService(journal);
                var targetBox = inbox.Get(target);
                if (!loseAcknowledgement && !loseReadBack) inbox.Pause(target, "User stop");
                var notification = targetBox.InputChanged.Task;
                var starts = 0; var validations = 0; var catalogReads = 0;
                var settings = new AppSettings { Model = "journal-send", MaxCollaborationAgents = 2,
                    StreamResponses = false, SmartChatTitles = false, AutoCompressContext = false,
                    ContextWindowOverrideTokens = 65536, RequestTimeoutSeconds = 5, MaxAgentFormatRetries = 0 };
                var communication = new AgentCommunicationService(() => 2);
                Func<AgentChatAddress, AgentConversationEndpoint> endpoint = address => new AgentConversationEndpoint {
                    Address = address, Inbox = inbox, Load = () => store.Load(address.ChatId),
                    Validate = chat => {
                        if (++validations == 1 && loseReadBack)
                            journal.Fault = point => { if (point == "before_peer_readback") throw new IOException("peer receipt unavailable"); };
                        if (validations == 1 && loseAcknowledgement)
                            journal.Journal.Fault = point => { if (point == "after_flush") {
                                journal.Journal.Fault = null; throw new IOException("lost peer acceptance acknowledgement"); } };
                        return chat.WorkspaceId ?? chat.DocumentKey;
                    }, Start = chat => {
                        AssertEqual(store.ReadMetadata(chat.Id).Revision, chat.Revision, "live recipient cursor advances before scheduling");
                        starts++;
                    } };
                communication.Register(new AgentConversationScope { Key = scopeKey,
                    Contains = chat => chat != null && chat.DocumentKey == source.DocumentKey && chat.Host == source.Host,
                    Endpoint = endpoint, ReadCatalog = () => {
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
                                if (++round > 2)
                                    foreach (var message in JObject.Parse(wire)["messages"])
                                    {
                                        var content = (string)message["content"] ?? "";
                                        var feedback = content.IndexOf("completionFeedback:", StringComparison.Ordinal);
                                        if (feedback >= 0) Console.WriteLine(content.Substring(feedback, Math.Min(1400, content.Length - feedback)));
                                    }
                                AssertTrue(round <= 2, "send or resolve is never retried by the model loop");
                                if (requestId != null) AssertContains(wire, "Inspect this exact assignment", "recipient model receives peer provenance and original text");
                                var response = round == 1 ? ModelProtocolWire.WriteAcceptedCall("Contact the exact peer.", null,
                                    new ConversationToolCall { Name = requestId == null ? AgentCommunicationToolCatalog.Send : AgentCommunicationToolCatalog.Resolve,
                                        Arguments = requestId == null ? new JObject { ["agent_id"] = AgentChatAddress.IdFor(target.Id),
                                            ["text"] = "Inspect this exact assignment", ["assignment"] = true, ["expect_reply"] = true }
                                            : new JObject { ["request_id"] = requestId, ["outcome"] = "completed", ["text"] = "Exact native completion" } })
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
                                ConversationPolicy = ConversationRunPolicy.For(ChatModes.Agent) };
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
                                service.ExecuteAsync(ChatModes.Agent, user.Content, chat, chat.Context, settings,
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
                        }
                        else run(source, null);
                        AssertEqual(!loseReadBack, notification.IsCompleted, "only proven intake wakes the existing live Inbox signal");
                        AssertEqual(loseReadBack ? 0 : 1, starts, "unconfirmed intake is not scheduled");
                        if (!loseAcknowledgement && !loseReadBack)
                            AssertTrue(targetBox.Paused && !inbox.TryStartWorker(targetBox.Session), "peer intake cannot release a user-stopped queue");
                        AssertEqual(0, catalogReads, "known-address delivery never enumerates the participant catalog");
                        var send = journal.ReadWindow(source.Id, long.MaxValue, 8).Single();
                        AssertEqual(loseReadBack ? ToolExecutionOutcome.Unknown : ToolExecutionOutcome.Ok,
                            send.Fact.Result.ExecutionProgress.Outcome, "only exact canonical read-back proves intake");
                        var accepted = new ConversationInboxService(journal).ReadUndelivered(store.Load(target.Id)).Single();
                        requestId = accepted.AgentCommunication.MessageId;
                        var delivery = journal.AgentMessage(requestId);
                        AssertEqual(send.Fact.Result.ToolCallId, delivery.Fact.CallId, "tool terminal refers to the exact native delivery call");
                        AssertEqual(source.LastRun.RunId, delivery.Fact.RunId, "acceptance belongs to the actual sender run");
                        journal.Rebuild();
                        source = store.Load(source.Id); target = store.Load(target.Id);
                        AssertEqual(send.Position.Hash, journal.ReadWindow(source.Id, long.MaxValue, 8).Single().Position.Hash, "rebuild preserves even an unknown terminal");
                        // Restart does not schedule retained input; explicit resume uses
                        // the same Inbox and real Office/Workspace run consumers.
                        inbox = new ConversationInboxService(journal); executor.AgentInbox = inbox;
                        AssertTrue(inbox.Snapshot(target).Paused && !inbox.TryStartWorker(target), "cold native intake does not restart itself");
                        AssertEqual(loseReadBack ? 0 : 1, starts, "rebuild never redispatches a send");
                        inbox.Resume(target); var incoming = inbox.TakeNext(target);
                        round = 0; run(target, incoming);
                        AssertEqual(loseReadBack ? 1 : 2, starts, "resolve schedules one new correlated input");
                        AssertEqual(0, catalogReads, "resolve also uses exact indexed addressing");
                        var resolved = journal.ReadWindow(target.Id, long.MaxValue, 8).Single();
                        AssertEqual(ToolExecutionOutcome.Ok, resolved.Fact.Result.ExecutionProgress.Outcome, "real resolve has its native receipt");
                        source = store.Load(source.Id);
                        var completion = inbox.ReadUndelivered(source).Single();
                        AssertEqual(requestId, completion.AgentCommunication.ReplyTo, "resolution retains request identity across restart");
                        AssertEqual(AgentAssignmentOutcome.Completed, completion.AgentCommunication.Resolution.Value, "completion remains the peer's report");
                        AssertEqual(ConversationInputStatus.Applied, journal.InputState(requestId).Status, "the original assignment is applied once");
                        AssertEqual(delivery.Position.Hash, journal.AgentMessage(requestId).Position.Hash, "reply never rewrites original acceptance");
                        if (!office)
                        {
                            var backup = Path.Combine(paths.Root, "send-backup"); JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                            var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "send-restore"), _ => StorageProtector.None);
                            var copy = new ConversationJournal(restored, scopeKey);
                            AssertEqual(send.Position.Hash, copy.ReadWindow(source.Id, long.MaxValue, 8).Single().Position.Hash, "restore preserves the original sender outcome without dispatch");
                            AssertEqual(AgentAssignmentOutcome.Completed, new ConversationInboxService(copy)
                                .ReadUndelivered(((IConversationStore)copy).Load(source.Id)).Single().AgentCommunication.Resolution.Value,
                                "restore retains native completion input");
                        }
                    }
                    finally { journal.Journal.Fault = null; journal.Fault = null; try { listener.Stop(); } catch (ObjectDisposedException) { }
                        AssertTrue(provider.Wait(5000), "native send provider stops"); }
                }
            });
        }
    }
}
