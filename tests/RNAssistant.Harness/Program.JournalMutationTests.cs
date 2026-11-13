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
        private static void JournalMutationHostParity()
        { foreach (var office in new[] { true, false }) JournalMutationTurn(office, null); }

        private static void JournalMutationUnknown()
        { JournalMutationTurn(false, "unknown"); JournalMutationTurn(false, "resource-ack"); }

        private static void JournalMutationTerminalReceipt()
        { JournalMutationTurn(false, "terminal-ack"); }

        private static void JournalPlanRuntime()
        { JournalMutationTurn(true, "plan"); JournalMutationTurn(false, "plan"); }

        private static void JournalConfirmationParity()
        { foreach (var office in new[] { true, false }) JournalMutationTurn(office, "confirm"); }

        private static void JournalConfirmationClaimLoss()
        { foreach (var office in new[] { true, false }) JournalMutationTurn(office, "confirm-claim-ack"); }

        private static void JournalConfirmationDenial()
        { JournalMutationTurn(false, "confirm-deny"); }

        private static void JournalConfirmationPreparation()
        { foreach (var office in new[] { true, false }) JournalMutationTurn(office, "confirm-preparation"); }

        private static void JournalRecoveryWrite()
        { JournalMutationTurn(false, "recovery-open"); JournalMutationTurn(false, "recovery-ack"); }

        private static void JournalRecoveryBeforeKernel()
        {
            foreach (var office in new[] { true, false }) WithTempPaths(paths => {
                var journal = new ConversationJournal(paths, "document:before-kernel"); IConversationStore store = journal;
                var chat = store.CreateTransient("Word", "doc", "Document", "Accepted input");
                var user = new ChatMessage { Role = "user", Content = "Start", RunId = "accepted" };
                chat.LastRun = new ChatRunRecord { RunId = "accepted", TurnId = "accepted", Status = "running" };
                chat.Messages.Add(user); store.Commit(chat, ConversationCommit.UserInput(user));
                var recovered = RecoverJournalRun(store, store.Load(chat.Id), office);
                AssertEqual("interrupted", recovered.LastRun.Status, "accepted input closes before kernel start");
                AssertTrue(recovered.LastRun.KernelState == null && journal.CurrentRun(chat.Id) == null,
                    "recovery cannot invent a kernel execution");
                AssertEqual(2, recovered.Messages.Count, "input and diagnostic survive");
                AssertEqual(0, journal.ReadWindow(chat.Id, long.MaxValue, 8).Count, "no tool evidence before kernel start");
                AssertEqual(recovered.Revision, RecoverJournalRun(store, recovered, office).Revision, "pre-kernel recovery is idempotent");
                journal.Rebuild();
                AssertEqual(recovered.Revision, store.Load(chat.Id).Revision, "rebuild retains pre-kernel interruption");
            });
        }

        private static ChatSession RecoverJournalRun(IConversationStore store, ChatSession session, bool office)
        {
            if (office) return new ChatSessionService(new FakeOfficeAdapter(), store)
                .ReloadAndReconcileInterruptedRun(session.Host, session.DocumentKey, session.Id);
            return ConversationRecoveryService.ReconcileFenced(session, store.Load(session.Id),
                current => WorkspaceConversationService.InterruptAbandonedRun(current, store));
        }

        private static void JournalMutationTurn(bool office, string fault, bool checkHistory = false)
        {
            WithTempPaths(paths => {
                var workspace = office ? null : new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "project"));
                var scope = office ? "document:" + DocumentAuthorityId.Create().Id : "workspace:" + workspace.WorkspaceId;
                var journal = new ConversationJournal(paths, scope); IConversationStore store = journal;
                var resources = new ResourceJournal(paths); var payloads = new ChatBlobStore(paths);
                var files = new WorkspaceFileService(paths, resources, resources, resources);
                var adapter = new FakeOfficeAdapter();
                var confirmation = fault?.StartsWith("confirm", StringComparison.Ordinal) == true;
                var claimStopped = fault == "confirm-claim-ack" || fault == "confirm-preparation";
                var settings = new AppSettings { Model = "journal-mutation", StreamResponses = false, SmartChatTitles = false,
                    AutoCompressContext = false, AutoConfirmToolActions = !confirmation, ContextWindowOverrideTokens = 65536,
                    RequestTimeoutSeconds = 5, MaxAgentFormatRetries = 0 };
                var executor = office ? new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths),
                    new ToolStore(paths), () => settings, paths: paths, resourceAuthorityStore: resources,
                    resourceMutationJournal: resources, resourceOrigin: (session, context) => journal.MutationOrigin(session, context)) : null;
                var chat = office ? new ChatSessionService(adapter, store).CreatePersistentChat("Journal mutation") :
                    new WorkspaceStore(paths).CreateSession(store, workspace, "Journal mutation");
                var target = office ? "VBA module: JournalModule" : "created.txt";
                var content = office ? "Sub Main()\nDebug.Print 17\nEnd Sub" : "Written exactly once.";
                var uncertain = fault == "unknown" || fault == "resource-ack";
                var plan = fault == "plan";
                var recover = fault?.StartsWith("recovery-", StringComparison.Ordinal) == true;
                if (plan)
                {
                    target = "plan: Journal Plan";
                    content = "---\ntitle: \"Journal Plan\"\nstatus: draft\n---\nPublished through native journal.";
                }
                var dispatched = 0;
                files.FaultPoint = point => {
                    if (point != "after-dispatch") return;
                    dispatched++;
                    if (recover) journal.Journal.Fault = stage => {
                        if (stage == "before_append") throw new IOException("process stopped before terminal commit");
                    };
                    if (fault == "unknown") throw new IOException("lost read-back");
                    if (fault == "terminal-ack") journal.Journal.Fault = stage => {
                        if (stage != "after_flush") return;
                        journal.Journal.Fault = null; throw new IOException("lost terminal acknowledgement");
                    };
                    if (fault == "resource-ack")
                    {
                        var flushes = 0;
                        resources.Journal.Fault = stage => {
                            if (stage != "after_flush" || ++flushes != 3) return;
                            resources.Journal.Fault = null; throw new IOException("lost authority acknowledgement");
                        };
                    }
                };
                var port = new TcpListener(IPAddress.Loopback, 0); port.Start();
                settings.BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
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
                                using (var reader = new StreamReader(request.Request.InputStream)) await reader.ReadToEndAsync().ConfigureAwait(false);
                                var number = ++calls;
                                AssertTrue(number <= (uncertain ? 4 : 2), "finish correction remains bounded without repeating the mutation");
                                var response = number == 1
                                    ? ModelProtocolWire.WriteAcceptedCall("Create the requested source.", null,
                                        new ConversationToolCall { Name = "core.write", Arguments = new JObject { ["target"] = target, ["content"] = content } })
                                    : uncertain
                                        ? ModelProtocolWire.WriteAcceptedDecision("finish", "The write outcome is uncertain.", "Inspect before retrying.",
                                            outcome: ConversationOutcome.UnableToComplete, results: new[] {
                                                new ConversationResultClaim("Write not proven.", new[] { "E1" }, ConversationAssessment.Unverified) })
                                        : ScriptedVerifiedFinish(store.Load(chat.Id), "Write verified.", "Written.", "Source created.", "core.write");
                                await SteeringReply(request, response).ConfigureAwait(false);
                            }
                        }
                        catch (HttpListenerException) { }
                        catch (ObjectDisposedException) { }
                        catch (Exception error) { Console.WriteLine("Mutation provider: " + error); listener.Abort(); throw; }
                    });
                    try
                    {
                        Func<ChatSession, WorkspaceConversationService.WorkspacePorts> workspacePorts = session =>
                            new WorkspaceConversationService.WorkspacePorts(paths, store, journal,
                                new DocumentArtifactStore(resources, resources, payloads), files, payloads,
                                new WorkspaceWebSnapshotStore(paths, files), workspace, session, settings, () => "fixture-key", null,
                                mutationOrigin: context => journal.MutationOrigin(session, context), resourceAuthority: resources, resourceMutations: resources);
                        var client = new LlmClient(() => "fixture-key");
                        var service = office ? new ConversationRunService(adapter, executor, store, journal,
                            (configuration, messages, options, stream, token) => client.CompleteAsync(configuration, messages, options, stream, token)) : null;
                        Action run = () => {
                            if (office)
                            {
                                service.ExecuteAsync(ChatModes.Agent, "Create the source.", chat, chat.Context, settings,
                                    executor.GetControllerTools().ToArray(), null, (session, command, preparation) => "journal-approval").GetAwaiter().GetResult();
                            }
                            else
                            {
                                var id = Guid.NewGuid().ToString("N");
                                chat.LastRun = new ChatRunRecord { RunId = id, TurnId = id, StartedUtc = DateTime.UtcNow,
                                    ConversationPolicy = ConversationRunPolicy.For(ChatModes.Agent, autoConfirmToolActions: !confirmation) };
                                var user = new ChatMessage { Role = "user", Content = "Create the source.", RunId = id };
                                chat.Messages.Add(user); store.Commit(chat, ConversationCommit.UserInput(user));
                                var ports = workspacePorts(chat);
                                ConversationRunCoordinator.RunAsync(new AgentRunRequest(id, id, user.Content, new AgentRunLimits(5, 5)),
                                    ports, ports.Tools, ports, CancellationToken.None).GetAwaiter().GetResult();
                            }
                        };
                        if (fault == "terminal-ack" || recover) RuntimeThrows<RunStoreException>(run); else run();
                        if (confirmation)
                        {
                            journal.Rebuild(); chat = store.Load(chat.Id);
                            var pending = chat.LastRun.KernelState.Summary.PendingConfirmation;
                            AssertEqual(RunLifecycle.AwaitingConfirmation, chat.LastRun.KernelState.Summary.Lifecycle, "approval survives restart and rebuild");
                            AssertTrue(pending != null && pending.PreparedStateJson != null, "exact preparation remains durable");
                            AssertEqual(0, journal.ReadWindow(chat.Id, long.MaxValue, 8).Count, "pending cannot issue terminal evidence");
                            AssertEqual(0, resources.Unresolved().Count, "waiting does not prepare a source mutation");
                            AssertEqual(1, calls, "waiting does not request another model response");
                            var priorInvocation = journal.CurrentRun(chat.Id).Fact.InvocationId;
                            var stale = store.Load(chat.Id);
                            if (fault == "confirm-preparation")
                            {
                                var changed = JObject.FromObject(chat.LastRun.KernelState);
                                changed["Summary"]["PendingConfirmation"]["PreparedStateJson"] = pending.PreparedStateJson + " ";
                                chat.LastRun.KernelState = changed.ToObject<AgentRunState>();
                            }
                            Action<ChatSession> resume = session => {
                                var approval = session.LastRun.KernelState.Summary.PendingConfirmation;
                                if (office) service.ConfirmAsync(approval.PendingId, PendingCommand(session), session,
                                    new ConversationRunInput(settings, session.Context, executor.GetControllerTools().ToArray()), null).GetAwaiter().GetResult();
                                else
                                {
                                    var continuation = AgentRunContinuation.Restore(session.LastRun.KernelState, 0,
                                        WorkspaceConversationService.RestoreAcceptedHistory(session, payloads, true));
                                    var ports = workspacePorts(session);
                                    ConversationRunCoordinator.ResumeAsync(session.LastRun.RunId, approval.PendingId, continuation,
                                        ports, ports.Tools, ports, CancellationToken.None, rejectPending: fault == "confirm-deny").GetAwaiter().GetResult();
                                }
                            };
                            if (fault == "confirm-claim-ack")
                                journal.Journal.Fault = stage => { if (stage == "after_flush") { journal.Journal.Fault = null; throw new IOException("lost confirmation claim acknowledgement"); } };
                            if (claimStopped) RuntimeThrows<RunStoreException>(() => resume(chat)); else resume(chat);
                            RuntimeThrows<RunStoreException>(() => resume(stale));
                            AssertTrue(priorInvocation != journal.CurrentRun(chat.Id).Fact.InvocationId, "approval claims a new invocation in the same run");
                            if (claimStopped)
                            {
                                journal.Rebuild(); var claimed = store.Load(chat.Id);
                                AssertTrue(claimed.LastRun.KernelState.Summary.PendingConfirmation == null, "lost claim acknowledgement cannot recreate an approval");
                                AssertEqual(0, journal.ReadWindow(chat.Id, long.MaxValue, 8).Count, "claim loss precedes dispatch and terminal");
                                AssertEqual(0, resources.Unresolved().Count, "claim loss does not prepare a source write");
                                AssertEqual(1, calls, "claim loss makes no model request");
                            }
                        }
                    }
                    catch (Exception error) { Console.WriteLine((office ? "Office " : "Workspace ") + fault + ": " + error); throw; }
                    finally { try { listener.Stop(); } catch (ObjectDisposedException) { } AssertTrue(provider.Wait(10000), "mutation provider stops"); }
                }
                resources.Journal.Fault = null; journal.Journal.Fault = null; files.FaultPoint = null;
                if (claimStopped)
                {
                    AssertEqual(0, dispatched, "no invocation after uncertain claim acknowledgement");
                    if (!office) AssertTrue(!File.Exists(Path.Combine(workspace.RootPath, target)), "no file after lost claim acknowledgement");
                    var claimed = RecoverJournalRun(store, store.Load(chat.Id), office);
                    AssertEqual(RunLifecycle.Failed, claimed.LastRun.KernelState.Summary.Lifecycle, "abandoned claim closes without execution");
                    AssertEqual(0, claimed.LastRun.KernelState.Summary.ToolCounts.WriteUnknown, "no entered call means no invented effect");
                    AssertEqual(0, journal.ReadWindow(chat.Id, long.MaxValue, 8).Count, "claim recovery invents no terminal");
                    AssertEqual(claimed.Revision, RecoverJournalRun(store, claimed, office).Revision, "claim recovery is idempotent");
                    journal.Rebuild();
                    AssertEqual(claimed.Revision, store.Load(chat.Id).Revision, "rebuild retains recovered claim");
                    return;
                }
                var restored = store.Load(chat.Id);
                if (recover)
                {
                    AssertTrue(restored.LastRun.KernelState.InFlightTool != null, "lost terminal retains entered call");
                    AssertEqual(0, journal.ReadWindow(chat.Id, long.MaxValue, 8).Count, "no terminal before recovery");
                    if (fault == "recovery-ack")
                    {
                        journal.Journal.Fault = stage => { if (stage == "after_flush") throw new IOException("lost recovery acknowledgement"); };
                        RuntimeThrows<IOException>(() => RecoverJournalRun(store, restored, office));
                        journal.Journal.Fault = null;
                    }
                    restored = RecoverJournalRun(store, store.Load(chat.Id), office);
                    AssertEqual(RunLifecycle.Failed, restored.LastRun.KernelState.Summary.Lifecycle, "recovery closes abandoned run");
                    AssertEqual(1, restored.LastRun.KernelState.Summary.ToolCounts.WriteUnknown, "possible write is counted once as unknown");
                    AssertEqual(1, dispatched, "recovery cannot repeat the write");
                    AssertEqual(1, calls, "recovery cannot request a model response");
                    AssertEqual(content, File.ReadAllText(Path.Combine(workspace.RootPath, target)), "written source survives recovery");
                    var recoveredTerminal = journal.ReadWindow(chat.Id, long.MaxValue, 8).Single();
                    AssertEqual(ToolExecutionOutcome.Unknown, recoveredTerminal.Fact.Result.ExecutionProgress.Outcome, "late resource proof cannot promote interrupted execution");
                    AssertTrue(journal.RestoreContext(chat.Id, restored.LastRun.RunId, 0, 8192).WorkingSet.Requirements.UnknownEffects, "unknown remains in addressed context");
                    AssertEqual(restored.Revision, RecoverJournalRun(store, restored, office).Revision, "repeated recovery does not append");
                    journal.Rebuild();
                    AssertEqual(recoveredTerminal.Position.Hash, journal.ReadWindow(chat.Id, long.MaxValue, 8).Single().Position.Hash, "rebuild preserves the exact unknown receipt");
                    var backup = Path.Combine(paths.Root, "recovery-backup");
                    JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                    var restoredPaths = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "recovery-restore"), _ => StorageProtector.None);
                    IConversationStore copied = new ConversationJournal(restoredPaths, scope);
                    AssertEqual(restored.Revision, RecoverJournalRun(copied, copied.Load(chat.Id), false).Revision, "restore never replays recovered work");
                    return;
                }
                if (fault == "terminal-ack")
                {
                    var savedHash = journal.ReadWindow(chat.Id, long.MaxValue, 8).Single().Position.Hash;
                    restored = RecoverJournalRun(store, restored, office);
                    AssertEqual(RunLifecycle.Failed, restored.LastRun.KernelState.Summary.Lifecycle, "unfinished run closes after known terminal");
                    AssertEqual(savedHash, journal.ReadWindow(chat.Id, long.MaxValue, 8).Single().Position.Hash, "recovery never replaces a known terminal");
                    AssertEqual(0, restored.LastRun.KernelState.Summary.ToolCounts.WriteUnknown, "known effect stays known");
                }
                var call = restored.Messages.Single(item => item.AcceptedCallOrigin != null);
                var origin = new ResourceMutationOrigin(scope, chat.Id, call.RunId, call.ToolCallId, call.AcceptedCallOrigin.StepId);
                var terminal = journal.ReadWindow(chat.Id, long.MaxValue, 8).Single();
                if (fault == "confirm-deny")
                {
                    AssertEqual(ToolExecutionOutcome.NotDispatched, terminal.Fact.Result.ExecutionProgress.Outcome, "denial closes the accepted call without invocation");
                    AssertTrue(terminal.Fact.ResourceOutcome == null && !File.Exists(Path.Combine(workspace.RootPath, target)), "denial creates neither resource effect nor a file");
                    var alias = terminal.Fact.Result.EvidenceReference.Alias;
                    var admission = ConversationFinishEvidenceAdmission.Evaluate(journal, restored, call.RunId, restored.LastRun.TurnId,
                        new[] { call.ToolCallId }, new[] { new ConversationResultClaim("Written.", new[] { alias }, ConversationAssessment.Satisfied) }, payloads, new[] { alias });
                    AssertTrue(!admission.Accepted, "a denial receipt cannot prove a successful write");
                    AssertEqual(RunLifecycle.Cancelled, restored.LastRun.KernelState.Summary.Lifecycle, "denial is durable cancellation");
                    journal.Rebuild(); AssertEqual(terminal.Position.Hash, journal.ReadWindow(chat.Id, long.MaxValue, 8).Single().Position.Hash, "denial rebuild retains one terminal");
                    return;
                }
                AssertEqual(uncertain ? ToolExecutionOutcome.Unknown : ToolExecutionOutcome.Ok, terminal.Fact.Result.ExecutionProgress.Outcome,
                    "the kernel terminal is never promoted by a late receipt");
                AssertEqual(1, uncertain ? restored.LastRun.KernelState.Summary.ToolCounts.WriteUnknown : restored.LastRun.KernelState.Summary.ToolCounts.WriteOk,
                    "exactly one mutation is counted");
                if (uncertain) AssertEqual("repeated_finish_no_progress", restored.LastRun.KernelState.Summary.Reason,
                    "unverified model prose cannot bypass the existing post-unknown inspection requirement");
                if (plan)
                {
                    var artifact = new DocumentArtifactStore(resources, resources, payloads)
                        .ReadPlanSelection(restored, restored.ActivePlanDocumentArtifactId);
                    AssertEqual("Published through native journal.", artifact.InlineText, "real kernel publishes exact Plan bytes");
                    AssertEqual(1, restored.Artifacts.Count, "journal retains one exact Plan selection");
                    AssertTrue(restored.Artifacts.Single().InlineText == null, "journal does not hydrate Plan bodies");
                    if (!office) AssertEqual(artifact.InlineText,
                        new WorkspacePlanService(paths, store, payloads, workspace, restored,
                            new DocumentArtifactStore(resources, resources, payloads), resources, resources)
                            .Read("plan: Journal Plan", 1, representation: "text").Result.Text, "real Workspace Plan consumer");
                }
                else if (office) AssertContains(adapter.GetVbaModuleCode("JournalModule"), "Print 17", "Office source was changed by the actual owner");
                else { AssertEqual(content, File.ReadAllText(Path.Combine(workspace.RootPath, target)), "actual file write survives"); AssertEqual(1, dispatched, "exactly one file invocation"); }
                var outcome = resources.CallOutcome(origin);
                if (fault == "unknown")
                {
                    AssertTrue(outcome == null && resources.Unresolved().Count == 1, "possible dispatch remains addressable without invented read-back");
                    AssertTrue(journal.RestoreContext(chat.Id, call.RunId, 0, 8192).WorkingSet.Requirements.UnknownEffects, "unknown write remains a required context obligation");
                }
                else
                {
                    AssertTrue(outcome?.Fact.Call != null, "resource terminal carries immutable accepted-call and runtime-entry proof");
                    AssertEqual(outcome.CommitId, terminal.Fact.Result.AuthorityCommitId, "conversation terminal links the exact resource outcome");
                    new ConversationJournal(paths, scope).VerifyMutation(outcome.Fact.Call);
                    var bytes = resources.Journal.WrittenBytes;
                    RuntimeThrows<InvalidOperationException>(() => resources.Prepare(outcome.Fact.Scope, "foreign", outcome.Fact.Attempt.Target,
                        origin: new ResourceMutationOrigin(scope, "other-chat", call.RunId, call.ToolCallId, origin.StepId)));
                    RuntimeThrows<InvalidOperationException>(() => resources.Prepare(outcome.Fact.Scope, "repeat", outcome.Fact.Attempt.Target, origin: origin));
                    AssertEqual(bytes, resources.Journal.WrittenBytes, "foreign call is rejected before resource append");
                }
                journal.Rebuild();
                AssertEqual(terminal.Position.Hash, journal.ReadWindow(chat.Id, long.MaxValue, 8).Single().Position.Hash, "rebuild preserves the immutable terminal");
                if (fault == "unknown")
                {
                    RuntimeThrows<WorkspaceFileException>(() => files.ReplaceText(workspace, target, null, "unsafe repeat", origin: origin));
                    files.CreateText(workspace, "independent.txt", "Independent manual write.");
                    files.ReconcileUncertain(workspace, target);
                    var proof = resources.CallOutcome(origin);
                    journal.LinkResourceOutcome(chat.Id, call.RunId, call.ToolCallId, proof);
                    journal.Rebuild();
                    AssertEqual(proof.Position.Hash, journal.LinkedResourceOutcome(chat.Id, call.RunId, call.ToolCallId).Position.Hash,
                        "late inspection links its exact proof without changing the original unknown terminal");
                    AssertEqual(terminal.Position.Hash, journal.ReadWindow(chat.Id, long.MaxValue, 8).Single().Position.Hash,
                        "late evidence preserves the original occurrence");
                    AssertEqual(content, File.ReadAllText(Path.Combine(workspace.RootPath, target)), "uncertain target is not replayed while independent work remains available");
                }
                else if (!office)
                {
                    var backup = Path.Combine(paths.Root, "backup"); var maintenance = JournalCasService(paths, StorageProtector.None);
                    var scopeLease = Directory.EnumerateFiles(paths.ResourceAuthorityDirectory, "mutation-*.lck").First();
                    File.WriteAllText(scopeLease, "Unexpected data must not be silently excluded.");
                    RuntimeThrows<InvalidOperationException>(() => maintenance.BackupJournals(backup));
                    File.WriteAllText(scopeLease, string.Empty); maintenance.BackupJournals(backup);
                    var recovered = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "restore"), _ => StorageProtector.None);
                    var recoveredResources = new ResourceJournal(recovered);
                    AssertEqual(outcome.Position.Hash, recoveredResources.CallOutcome(origin).Position.Hash, "restore preserves cross-journal proof without invocation");
                }
                AssertEqual(fault == "terminal-ack" ? 1 : uncertain ? 4 : 2, calls, "only bounded finish correction, never mutation replay");
                if (checkHistory) JournalHistoryGuardAfterMutation(paths, journal, store.Load(chat.Id), resources, workspace, files, settings, target, content);
            });
        }
    }
}
