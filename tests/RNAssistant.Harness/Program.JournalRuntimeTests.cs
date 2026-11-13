using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Application.Conversations;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RNAssistant.Runtime;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void JournalRuntimeHostParity()
        { JournalRuntimeHostParity(false); }

        private static void JournalRuntimeToolParity()
        { JournalRuntimeHostParity(true); }

        private static void JournalRuntimeHostParity(bool tools, bool inputs = false, bool feed = false)
        {
            foreach (var office in new[] { true, false }) WithTempExecutor((executor, adapter) => {
                var paths = FixturePaths.Value;
                var workspace = office ? null : new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "project"));
                if (tools && workspace != null) File.WriteAllText(Path.Combine(workspace.RootPath, "source.txt"), "Journal source.");
                var toolId = "core.read";
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
                            while (true)
                            {
                                var request = await listener.GetContextAsync().ConfigureAwait(false);
                                string wire; using (var reader = new StreamReader(request.Request.InputStream)) wire = await reader.ReadToEndAsync().ConfigureAwait(false);
                                var number = ++calls;
                                AssertTrue(number <= (tools ? 4 : 3), "the scripted scenario requires no further requests");
                                if (number == (tools ? 4 : 3)) AssertContains(wire, "Journal answer", "the next turn receives retained accepted history");
                                if (inputs && office) AssertContains(wire, "Steered requirements", "Office consumes steering through its real input channel");
                                var response = number == 1
                                    ? ModelProtocolWire.WriteAcceptedDecision("reflection", "Check the requested answer.", "", "Answer directly.")
                                    : ModelProtocolWire.WriteAcceptedDecision("finish", "The text answer is ready.", "Journal answer",
                                        outcome: ConversationOutcome.Completed, results: new ConversationResultClaim[0]);
                                if (tools && number == 1)
                                    response = ModelProtocolWire.WriteAcceptedCall("Inspect the requested source.", null,
                                        new ConversationToolCall { Name = toolId, Arguments = office
                                            ? new JObject { ["target"] = "Excel range: Data!A1:B2", ["representation"] = "text" }
                                            : new JObject { ["target"] = "source.txt" } });
                                if (tools && (number == 2 || number == 3))
                                {
                                    AssertContains(wire, "E1", "the request receives the committed evidence alias");
                                    response = ScriptedVerifiedFinish(store.Load(chat.Id), "Source inspected.", "Journal answer", "Inspected the source.", toolId);
                                    if (number == 2)
                                    {
                                        var invalid = JObject.Parse(response); invalid["results"][0]["evidence"] = new JArray("E999");
                                        response = invalid.ToString(Newtonsoft.Json.Formatting.None);
                                    }
                                }
                                if (tools && number == 4)
                                    response = ModelProtocolWire.WriteAcceptedDecision("finish", "Use the retained read.", "Journal answer",
                                        outcome: ConversationOutcome.Completed, results: new[] {
                                            new ConversationResultClaim("Previously inspected source.", new[] { "E1" }, ConversationAssessment.Satisfied) });
                                await SteeringReply(request, response).ConfigureAwait(false);
                            }
                        }
                        catch (HttpListenerException) { }
                        catch (ObjectDisposedException) { }
                        catch { listener.Abort(); throw; }
                    });
                    try
                    {
                        for (var turn = 0; turn < 2; turn++)
                        {
                            var eventStart = ConversationEventFeed.Cursor(chat);
                            var text = turn == 0 ? "Give a text answer." : "Use the previous answer.";
                            var inbox = inputs ? new ConversationInboxService(journal) : null;
                            ConversationInput input = null;
                            if (inputs)
                            {
                                var runId = Guid.NewGuid().ToString("N");
                                chat.LastRun = new ChatRunRecord { RunId = runId, TurnId = runId, StartedUtc = DateTime.UtcNow,
                                    ConversationPolicy = ConversationRunPolicy.For(ChatModes.Agent) };
                                input = InboxInput("input-" + turn, text, InputDelivery.Queue);
                                input.AcceptedRunId = office ? null : runId;
                                if (!office) input.Status = ConversationInputStatus.Delivering;
                                inbox.Submit(chat, input);
                                if (office) input = inbox.TakeNext(chat);
                                var user = new ChatMessage { Id = input.Id, Role = "user", Content = text, RunId = runId };
                                chat.Messages.Add(user); store.Commit(chat, ConversationCommit.UserInput(user));
                                inbox.Applied(chat, input.Id);
                                if (office)
                                {
                                    inbox.Bind(chat);
                                    var steer = InboxInput("steer-" + turn, "Steered requirements", InputDelivery.Steer);
                                    steer.AcceptedRunId = runId;
                                    inbox.Submit(chat, steer);
                                }
                            }
                            if (office)
                            {
                                var client = new LlmClient(() => "fixture-key");
                                var traces = new ModelTracePersistenceService(journal);
                                var service = new ConversationRunService(adapter, executor, store, journal,
                                    (configuration, messages, options, stream, token) => {
                                        traces.Configure(options, configuration.DebugModelTraffic);
                                        return client.CompleteAsync(configuration, messages, options, stream, token);
                                    }, null, inbox: inbox);
                                service.ExecuteAsync(ChatModes.Agent, text, chat, chat.Context, settings,
                                    executor.GetControllerTools().ToArray(), null, null, null, null, CancellationToken.None,
                                    appendUserMessage: !inputs).GetAwaiter().GetResult();
                            }
                            else
                            {
                                var runId = inputs ? chat.LastRun.RunId : Guid.NewGuid().ToString("N");
                                if (!inputs)
                                {
                                    chat.LastRun = new ChatRunRecord { RunId = runId, TurnId = runId, StartedUtc = DateTime.UtcNow,
                                        ConversationPolicy = ConversationRunPolicy.For(ChatModes.Agent) };
                                    var user = new ChatMessage { Role = "user", Content = text, RunId = runId };
                                    chat.Messages.Add(user); store.Commit(chat, ConversationCommit.UserInput(user));
                                }
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
                            if (feed)
                            {
                                var bodies = journal.ChatBodyReads;
                                var page = new ConversationEventFeed(journal).Read(ConversationMetadata.From(retained), eventStart, limit: 100);
                                AssertTrue(!page.ResyncRequired && !page.HasMore, "real runtime publishes a bounded native event page");
                                AssertEqual(ConversationEventFeed.Cursor(retained), page.NextCursor, "feed reaches the accepted runtime checkpoint");
                                AssertTrue(page.Events.Any(item => item.Type == SessionEventTypes.LlmRequest) &&
                                    page.Events.Any(item => item.Type == SessionEventTypes.ModelResponseAccepted) &&
                                    page.Events.Any(item => item.Type == "chat.committed" && item.Operations.Contains(SessionOperationTypes.MessageUpdated)),
                                    "model and message notifications come from the actual runtime");
                                AssertEqual(bodies, journal.ChatBodyReads, "feed never hydrates model/context/message CAS");
                                AssertTrue(!Newtonsoft.Json.JsonConvert.SerializeObject(page).Contains("Journal answer"), "notices contain no answer body");
                            }
                            if (inputs)
                            {
                                AssertEqual(1, retained.Messages.Count(item => item.Id == input.Id), "one accepted input becomes one user message");
                                AssertEqual(ConversationInputStatus.Applied, new ConversationInboxService(journal)
                                    .ReadAccepted(retained, input.OperationId, input.RequestFingerprint, !office).Current.Status,
                                    "real runtime retains the canonical input receipt");
                                if (office) AssertEqual(ConversationInputStatus.Applied,
                                    new ConversationInboxService(journal).ReadAccepted(retained, "steer-" + turn, requireRunId: true).Current.Status,
                                    "steering persists one exact message and applied receipt");
                            }
                            AssertEqual(chat.Revision, retained.Revision, "restart preserves the exact accepted cursor");
                            AssertTrue(retained.Messages.Any(message => message.ProtocolMessage && message.Content.Contains("Journal answer")), "accepted answer is durable");
                            AssertEqual(RunLifecycle.Completed, retained.LastRun.KernelState.Summary.Lifecycle, "canonical checkpoint survives restart");
                            if (tools)
                            {
                                var accepted = retained.Messages.Single(item => item.AcceptedCallOrigin != null);
                                var terminal = retained.Messages.Single(item => item.Activity?.ToolCallId == accepted.ToolCallId && item.ExecutionProgress != null);
                                AssertEqual("T1", accepted.ToolCallReference.Alias, "one accepted call, including after citation correction");
                                AssertEqual("E1", terminal.EvidenceReference.Alias, "execution and presentation share one terminal reference");
                                AssertEqual(1, journal.ReadWindow(chat.Id, long.MaxValue, 8).Count, "one canonical terminal survives the next turn");
                                AssertTrue(retained.ResourceReferenceIndex.Count > 0, "read retains indexed resource aliases");
                                var bodies = journal.ChatBodyReads; var records = journal.Journal.ReadRecords; var readBytes = journal.Journal.ReadBytes;
                                AssertEqual(3, ((IEventStore)journal).ReadFinishEvidence(retained, retained.LastRun.RunId, new[] { "E1" }).Facts.Count,
                                    "current and historical E selection resolve the same call/start/terminal");
                                AssertEqual(bodies, journal.ChatBodyReads, "finish selection does not hydrate historical bodies");
                                Console.WriteLine("journal finish {0} turn={1}: records={2}, bytes={3}, history bodies=0", office ? "Office" : "Workspace",
                                    turn, journal.Journal.ReadRecords - records, journal.Journal.ReadBytes - readBytes);
                            }
                            var manifests = ((IEventStore)journal).Read(ConversationMetadata.From(retained), SessionEventReadMode.Validated)
                                .Where(item => item.Type == SessionEventDescriptors.For(SessionEventKind.ModelRequestPrepared).Type).ToArray();
                            AssertEqual((turn == 0 ? 2 : 3) + (tools ? 1 : 0), manifests.Length, "each actual HTTP request has a durable manifest");
                            AssertTrue(manifests.All(item => item.Payload == null && item.Data["Manifest"] != null), "raw traffic remains outside durable history");
                            chat = retained; journal = new ConversationJournal(paths, scope); store = journal;
                        }
                    }
                    catch (Exception error) { Console.WriteLine((office ? "Office: " : "Workspace: ") + error); throw; }
                    finally { listener.Stop(); AssertTrue(provider.Wait(10000), "scripted provider stops"); }
                }
                AssertEqual(tools ? 4 : 3, calls, "only the scripted finish correction adds a request");
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

        private static void JournalRuntimeToolReceipt() { JournalRuntimeToolReceipt(false, true); }

        private static void JournalRecoveryRead()
        { JournalRuntimeToolReceipt(true, true); JournalRuntimeToolReceipt(true, false); }

        private static void JournalRuntimeToolReceipt(bool recover, bool office)
        {
            foreach (var unknown in recover ? new[] { false } : new[] { false, true }) WithTempPaths(paths => {
                var journal = new ConversationJournal(paths, "document:tool-receipt"); IConversationStore store = journal;
                var chat = store.CreateTransient("Word", "doc", "Document", "Tool receipt"); store.Commit(chat, new ConversationCommit());
                var initial = CheckpointState("run", RunLifecycle.Running, 0, 0);
                chat.LastRun = new ChatRunRecord { RunId = "run", TurnId = initial.Summary.TurnId, StartedUtc = DateTime.UtcNow,
                    KernelState = initial, ConversationPolicy = ConversationRunPolicy.For(ChatModes.Agent) };
                ConversationRunCommit prepare(AgentRunEvent fact, ChatMessage[] appended, ChatMessage toolActivity = null)
                {
                    chat.LastRun.KernelState = AgentRunState.Apply(chat.LastRun.KernelState, fact);
                    ConversationRunProjection.Apply(chat.LastRun); chat.Messages.AddRange(appended);
                    return new ConversationRunCommit(chat, fact, appended, toolActivity);
                }
                prepare(new AgentRunEvent(AgentRunEventKind.Started, initial.Summary, limits: initial.Limits), new ChatMessage[0]).Persist(store.CommitRun, chat);
                var summary = new RunSummary("run", initial.Summary.TurnId, RunLifecycle.Running, new ToolCounts(), 1, 0, "", null, null);
                var call = new ToolCall("read", "core.read", "{\"target\":\"source.txt\"}");
                var accepted = JournalReadCall(call.Id, "run", "step");
                prepare(new AgentRunEvent(AgentRunEventKind.ResponseAccepted, summary, response:
                    new AgentResponse("execute", "Read the source.", null, new[] { call })), new[] { accepted }).Persist(store.CommitRun, chat);
                var context = new ToolExecutionContext(call, new ToolPolicySnapshot(call.Name, "read-v1",
                    new ToolPolicy(ToolEffect.Read, ToolVerification.None, false, true, new[] { ChatModes.Agent })),
                    "run", summary.TurnId, "step", DateTime.UtcNow, false, 10);
                var activity = new ChatMessage { Role = "assistant", Content = "Reading", RunId = "run", ExcludeFromModelContext = true,
                    Activity = new ChatActivity { Kind = "tool", RunId = "run", ToolCallId = call.Id, ToolId = call.Name, StepId = "step" } };
                prepare(new AgentRunEvent(AgentRunEventKind.ToolStarted, summary, toolContext: context), new[] { activity }, activity).Persist(store.CommitRun, chat);
                var interrupted = store.Load(chat.Id);
                AssertEqual(call.Id, interrupted.LastRun.KernelState.InFlightTool.Call.Id, "restart retains an entered call without replaying it");
                if (recover)
                {
                    var recovered = RecoverJournalRun(store, interrupted, office);
                    AssertEqual(RunLifecycle.Failed, recovered.LastRun.KernelState.Summary.Lifecycle, "abandoned read closes");
                    AssertEqual(1, recovered.LastRun.KernelState.Summary.ToolCounts.ReadError, "read interruption counts one error");
                    AssertEqual(0, recovered.LastRun.KernelState.Summary.ToolCounts.WriteUnknown, "read interruption invents no possible write");
                    AssertEqual(ToolExecutionOutcome.Error, journal.ReadWindow(chat.Id, long.MaxValue, 8).Single().Fact.Result.ExecutionProgress.Outcome, "read has an error terminal");
                    AssertEqual(recovered.Revision, RecoverJournalRun(store, recovered, office).Revision, "read recovery is idempotent");
                    journal.Rebuild();
                    AssertEqual(recovered.Revision, store.Load(chat.Id).Revision, "rebuild retains read recovery");
                    return;
                }
                RuntimeThrows<InvalidOperationException>(() => journal.CommitCall(chat.Id, "standalone",
                    JournalReadCall("other", "run", "other-step")));
                var execution = new ToolExecutionRecord(context, unknown ? ToolExecutionOutcome.Unknown : ToolExecutionOutcome.Ok,
                    DateTime.UtcNow, "Read finished", result: new RNAssistant.Core.Tools.Contracts.ToolResult(
                        unknown ? RNAssistant.Core.Tools.Contracts.ToolResultStatus.Unknown : RNAssistant.Core.Tools.Contracts.ToolResultStatus.Ok));
                summary = new RunSummary("run", summary.TurnId, RunLifecycle.Running, summary.ToolCounts.Add(execution), 1, 1, "", null, null);
                activity.Content = "Finished"; activity.ExecutionProgress = ToolExecutionProgress.Capture(execution);
                var completed = new AgentRunEvent(AgentRunEventKind.ToolCompleted, summary, execution: execution);
                var command = prepare(completed, new ChatMessage[0], activity);
                journal.Journal.Fault = point => { if (point == "after_flush") throw new IOException("lost terminal acknowledgement"); };
                RuntimeThrows<IOException>(() => command.Persist(store.CommitRun, chat)); journal.Journal.Fault = null;
                AssertTrue(command.Position != null, "terminal own receipt is recovered after lost acknowledgement");
                var before = journal.Journal.WrittenBytes; store.CommitRun(command);
                AssertEqual(before, journal.Journal.WrittenBytes, "retry accepts the same frame without adding another terminal");
                var retained = store.Load(chat.Id);
                AssertEqual(1, retained.LastRun.KernelState.Summary.ToolCounts.ReadOk + retained.LastRun.KernelState.Summary.ToolCounts.ReadError,
                    "terminal and counter survive together");
                AssertEqual(execution.Outcome, retained.Messages.Single(item => item.Activity != null).ExecutionProgress.Outcome,
                    "restart preserves the terminal outcome before result presentation");
                var snapshot = ((IEventStore)journal).ReadFinishEvidence(retained, "run", new[] { "E1" });
                AssertEqual(3, snapshot.Facts.Count, "addressed finish proof contains call, start and terminal");
                RuntimeThrows<InvalidOperationException>(() => prepare(new AgentRunEvent(AgentRunEventKind.ToolStarted, summary,
                    toolContext: context), new ChatMessage[0], activity).Persist(store.CommitRun, chat));
                AssertEqual(before, journal.Journal.WrittenBytes, "the same call cannot dispatch again after lost acknowledgement");
                var events = (IEventStore)journal;
                events.Append(retained, new SessionEventWrite(SessionEventDescriptors.For(SessionEventKind.ToolPackExtensionAccepted),
                    new ToolPackExtensionEventData { Admitted = true, Mode = ChatModes.Agent, Host = "Word" },
                    null, new SessionEventCorrelation("run", summary.TurnId, "step")));
                AssertEqual(0, events.ReadToolPackAdmissions(ConversationMetadata.From(retained), new[] { "other-run" }).Count,
                    "admission query is scoped to the requested runs");
                journal.Rebuild();
                AssertEqual(1, events.ReadToolPackAdmissions(ConversationMetadata.From(retained), new[] { "run" }).Count,
                    "typed admission and its addressed index rebuild together");
                AssertEqual(1, journal.ReadWindow(chat.Id, long.MaxValue, 8).Count, "rebuild retains one terminal and its context index");
                var backup = Path.Combine(paths.Root, "backup"); JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                var target = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "restore"), _ => StorageProtector.None);
                IConversationStore restored = new ConversationJournal(target, "document:tool-receipt");
                AssertEqual(execution.Outcome, restored.Load(chat.Id).Messages.Single(item => item.Activity != null).ExecutionProgress.Outcome,
                    "backup restores the same outcome without replay");
            });
        }
    }
}
