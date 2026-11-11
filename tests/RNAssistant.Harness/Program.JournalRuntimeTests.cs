using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;
using RNAssistant.Runtime;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void JournalRuntimeHostParity()
        {
            foreach (var office in new[] { true, false }) WithTempExecutor((executor, adapter) => {
                var paths = FixturePaths.Value;
                var workspace = office ? null : new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "project"));
                var scope = office ? "document:" + DocumentAuthorityId.Create().Id : "workspace:" + workspace.WorkspaceId;
                var journal = new ConversationJournal(paths, scope); IConversationStore store = journal;
                var chat = office ? new ChatSessionService(adapter, store).CreatePersistentChat("Journal runtime") :
                    new WorkspaceStore(paths).CreateSession(store, workspace, "Journal runtime");
                var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
                var settings = new AppSettings { BaseUrl = "http://127.0.0.1:" + port, Model = "scripted-journal",
                    StreamResponses = false, SmartChatTitles = false, AutoCompressContext = false,
                    ContextWindowOverrideTokens = 65536, RequestTimeoutSeconds = 5, MaxAgentFormatRetries = 0 };
                var calls = 0;
                using (var listener = new HttpListener())
                {
                    listener.Prefixes.Add(settings.BaseUrl + "/"); listener.Start();
                    var provider = Task.Run(async () => {
                        try
                        {
                            while (calls < 3)
                            {
                                var request = await listener.GetContextAsync().ConfigureAwait(false);
                                string wire; using (var reader = new StreamReader(request.Request.InputStream)) wire = await reader.ReadToEndAsync().ConfigureAwait(false);
                                var number = ++calls;
                                if (number == 3) AssertContains(wire, "Journal answer", "the next turn receives retained accepted history");
                                var response = number == 1
                                    ? ModelProtocolWire.WriteAcceptedDecision("reflection", "Check the requested answer.", "", "Answer directly.")
                                    : ModelProtocolWire.WriteAcceptedDecision("finish", "The text answer is ready.", "Journal answer",
                                        outcome: ConversationOutcome.Completed, results: new ConversationResultClaim[0]);
                                await SteeringReply(request, response).ConfigureAwait(false);
                            }
                        }
                        catch (HttpListenerException) { }
                        catch (ObjectDisposedException) { }
                    });
                    try
                    {
                        for (var turn = 0; turn < 2; turn++)
                        {
                            var text = turn == 0 ? "Give a text answer." : "Use the previous answer.";
                            if (office)
                            {
                                var client = new LlmClient(() => "fixture-key");
                                var traces = new ModelTracePersistenceService(journal);
                                var service = new ConversationRunService(adapter, executor, store, journal,
                                    (configuration, messages, options, stream, token) => {
                                        traces.Configure(options, configuration.DebugModelTraffic);
                                        return client.CompleteAsync(configuration, messages, options, stream, token);
                                    });
                                service.ExecuteAsync(ChatModes.Agent, text, chat, chat.Context, settings,
                                    executor.GetControllerTools().ToArray(), null).GetAwaiter().GetResult();
                            }
                            else
                            {
                                var runId = Guid.NewGuid().ToString("N");
                                chat.LastRun = new ChatRunRecord { RunId = runId, TurnId = runId, StartedUtc = DateTime.UtcNow,
                                    ConversationPolicy = ConversationRunPolicy.For(ChatModes.Agent) };
                                var user = new ChatMessage { Role = "user", Content = text, RunId = runId };
                                chat.Messages.Add(user); store.Commit(chat, ConversationCommit.UserInput(user));
                                var payloads = new ChatBlobStore(paths); var authority = new ResourceAuthorityStore(paths);
                                var ports = new WorkspaceConversationService.WorkspacePorts(paths, store, journal,
                                    new DocumentArtifactStore(authority, authority, payloads), new WorkspaceFileService(paths), payloads,
                                    new WorkspaceWebSnapshotStore(paths, new WorkspaceFileService(paths)), workspace, chat, settings, () => "fixture-key", null);
                                ConversationRunCoordinator.RunAsync(new AgentRunRequest(runId, runId, text, new AgentRunLimits(5, 5)),
                                    ports, ports.Tools, ports, CancellationToken.None).GetAwaiter().GetResult();
                            }
                            AssertEqual(RunLifecycle.Completed, chat.LastRun.KernelState.Summary.Lifecycle,
                                (office ? "Office" : "Workspace") + " finishes through the real kernel and journal");
                            var retained = store.Load(chat.Id);
                            AssertEqual(chat.Revision, retained.Revision, "restart preserves the exact accepted cursor");
                            AssertTrue(retained.Messages.Any(message => message.ProtocolMessage && message.Content.Contains("Journal answer")), "accepted answer is durable");
                            AssertEqual(RunLifecycle.Completed, retained.LastRun.KernelState.Summary.Lifecycle, "canonical checkpoint survives restart");
                            var manifests = ((IEventStore)journal).Read(ConversationMetadata.From(retained), SessionEventReadMode.Validated)
                                .Where(item => item.Type == SessionEventDescriptors.For(SessionEventKind.ModelRequestPrepared).Type).ToArray();
                            AssertEqual(turn == 0 ? 2 : 3, manifests.Length, "each actual HTTP request has a durable manifest");
                            AssertTrue(manifests.All(item => item.Payload == null && item.Data["Manifest"] != null), "raw traffic remains outside durable history");
                            chat = retained; journal = new ConversationJournal(paths, scope); store = journal;
                        }
                    }
                    catch (Exception error) { Console.WriteLine((office ? "Office: " : "Workspace: ") + error); throw; }
                    finally { listener.Stop(); AssertTrue(provider.Wait(10000), "scripted provider stops"); }
                }
                AssertEqual(3, calls, "there is no format retry or repeated request");
                var bytes = StorageJournal.Inventory(paths).SelectMany(stream => stream.Segments).ToDictionary(path => path, File.ReadAllBytes);
                journal.Rebuild();
                AssertEqual(RunLifecycle.Completed, store.Load(chat.Id).LastRun.KernelState.Summary.Lifecycle, "combined run/message reducer rebuilds");
                AssertTrue(bytes.All(pair => pair.Value.SequenceEqual(File.ReadAllBytes(pair.Key))), "rebuild preserves canonical bytes");
                AssertTrue(!Directory.Exists(paths.ChatDirectory) || !Directory.EnumerateFiles(paths.ChatDirectory, "*", SearchOption.AllDirectories).Any(),
                    "real adapters wrote no old chat stream");
            });
        }

        private static void JournalRuntimeAtomicReceipt()
        {
            WithTempPaths(paths => {
                var journal = new ConversationJournal(paths, "document:atomic-runtime"); IConversationStore store = journal;
                var chat = store.CreateTransient("Word", "doc", "Document", "Atomic runtime"); store.Commit(chat, new ConversationCommit());
                var initial = CheckpointState("run", RunLifecycle.Running, 0, 0);
                chat.LastRun = new ChatRunRecord { RunId = initial.Summary.RunId, TurnId = initial.Summary.TurnId,
                    StartedUtc = DateTime.UtcNow, KernelState = initial, ConversationPolicy = ConversationRunPolicy.For(ChatModes.Agent) };
                new ConversationRunCommit(chat, new AgentRunEvent(AgentRunEventKind.Started, initial.Summary, limits: initial.Limits),
                    new ChatMessage[0]).Persist(store.CommitRun, chat);
                var summary = new RunSummary(initial.Summary.RunId, initial.Summary.TurnId, RunLifecycle.Running,
                    new ToolCounts(), 1, 0, "", null, null);
                chat.LastRun.KernelState = new AgentRunState(summary, initial.Limits);
                ConversationRunProjection.Apply(chat.LastRun);
                var message = new ChatMessage { Role = "assistant", RunId = summary.RunId, ProtocolMessage = true,
                    ResponseProtocolVersion = ConversationResponse.ProtocolVersion, ResponseModelAttemptId = "atomic-attempt",
                    Content = ModelProtocolWire.WriteAcceptedDecision("finish", "Answer ready.", "Atomic answer", outcome: ConversationOutcome.Completed,
                        results: new ConversationResultClaim[0]) };
                chat.Messages.Add(message);
                var fact = new AgentRunEvent(AgentRunEventKind.ResponseAccepted, summary,
                    response: new AgentResponse("finish", "Answer ready.", "Atomic answer", results: new ConversationResultClaim[0], outcome: ConversationOutcome.Completed));
                var command = new ConversationRunCommit(chat, fact, new[] { message });
                journal.Journal.Fault = point => { if (point == "after_flush") throw new IOException("lost acknowledgement"); };
                RuntimeThrows<IOException>(() => command.Persist(store.CommitRun, chat)); journal.Journal.Fault = null;
                AssertTrue(command.Position != null, "accepted own receipt survives a lost acknowledgement");
                var revision = chat.Revision; store.CommitRun(command);
                AssertEqual(revision, store.ReadMetadata(chat.Id).Revision, "retry does not append another kernel fact");
                AssertEqual(command.Position.Hash, journal.CurrentRun(chat.Id).Position.Hash, "Store and run share the same canonical frame");
                var restored = store.Load(chat.Id);
                AssertEqual(RunLifecycle.Running, restored.LastRun.KernelState.Summary.Lifecycle, "model finish is not a completed runtime; restart does not replay it");
                AssertEqual(message.Content, restored.Messages.Single().Content, "answer and checkpoint are accepted exactly once in one frame");
                journal.Fault = point => { if (point == "before_index") throw new IOException("index unavailable"); };
                store.SetTitle(chat, "Accepted while index lagged"); journal.Fault = null;
                AssertEqual(chat.Revision, store.ReadMetadata(chat.Id).Revision, "accepted metadata catches up after index failure");
                var stale = new ConversationRunCommit(restored, new AgentRunEvent(AgentRunEventKind.ModelStepStarted, summary), new ChatMessage[0]);
                RuntimeThrows<ChatConcurrencyException>(() => stale.Persist(store.CommitRun, restored));
                var backup = Path.Combine(paths.Root, "journal-backup"); JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                var target = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "journal-restore"), _ => StorageProtector.None);
                IConversationStore recovered = new ConversationJournal(target, "document:atomic-runtime");
                AssertEqual(restored.LastRun.RunId, recovered.Load(chat.Id).LastRun.RunId, "restore retains accepted state without running it");
                var before = journal.Journal.WrittenBytes;
                var unsafeMessage = new ChatMessage { Role = "assistant", Content = "tool", ToolCallId = "write" };
                RuntimeThrows<NotSupportedException>(() => store.Commit(chat, new ConversationCommit(new[] { unsafeMessage })));
                AssertEqual(before, journal.Journal.WrittenBytes, "unconnected tool writes fail before canonical append");
            });
        }
    }
}
