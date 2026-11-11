using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
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

        private static void JournalMutationUnsupportedOwner()
        { JournalMutationTurn(true, "unsupported-owner"); JournalMutationTurn(false, "unsupported-owner"); }

        private static void JournalMutationTurn(bool office, string fault)
        {
            WithTempPaths(paths => {
                var workspace = office ? null : new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "project"));
                var scope = office ? "document:" + DocumentAuthorityId.Create().Id : "workspace:" + workspace.WorkspaceId;
                var journal = new ConversationJournal(paths, scope); IConversationStore store = journal;
                var resources = new ResourceJournal(paths); var payloads = new ChatBlobStore(paths);
                var files = new WorkspaceFileService(paths, resources, resources, resources);
                var adapter = new FakeOfficeAdapter();
                var settings = new AppSettings { Model = "journal-mutation", StreamResponses = false, SmartChatTitles = false,
                    AutoCompressContext = false, AutoConfirmToolActions = true, ContextWindowOverrideTokens = 65536,
                    RequestTimeoutSeconds = 5, MaxAgentFormatRetries = 0 };
                var executor = office ? new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths),
                    new ToolStore(paths), () => settings, paths: paths, resourceAuthorityStore: resources,
                    resourceMutationJournal: resources, resourceOrigin: (session, context) => journal.MutationOrigin(session, context)) : null;
                var chat = office ? new ChatSessionService(adapter, store).CreatePersistentChat("Journal mutation") :
                    new WorkspaceStore(paths).CreateSession(store, workspace, "Journal mutation");
                var target = office ? "VBA module: JournalModule" : "created.txt";
                var content = office ? "Sub Main()\nDebug.Print 17\nEnd Sub" : "Written exactly once.";
                var uncertain = fault == "unknown" || fault == "resource-ack";
                var unsupported = fault == "unsupported-owner";
                if (unsupported)
                {
                    target = "plan: Journal Plan";
                    content = "---\ntitle: \"Journal Plan\"\nstatus: draft\n---\nNot yet connected.";
                }
                var dispatched = 0;
                files.FaultPoint = point => {
                    if (point != "after-dispatch") return;
                    dispatched++;
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
                                    : uncertain || unsupported
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
                        Action run = () => {
                            if (office)
                            {
                                var client = new LlmClient(() => "fixture-key");
                                var service = new ConversationRunService(adapter, executor, store, journal,
                                    (configuration, messages, options, stream, token) => client.CompleteAsync(configuration, messages, options, stream, token));
                                service.ExecuteAsync(ChatModes.Agent, "Create the source.", chat, chat.Context, settings,
                                    executor.GetControllerTools().ToArray(), null).GetAwaiter().GetResult();
                            }
                            else
                            {
                                var id = Guid.NewGuid().ToString("N");
                                chat.LastRun = new ChatRunRecord { RunId = id, TurnId = id, StartedUtc = DateTime.UtcNow,
                                    ConversationPolicy = ConversationRunPolicy.For(ChatModes.Agent, autoConfirmToolActions: true) };
                                var user = new ChatMessage { Role = "user", Content = "Create the source.", RunId = id };
                                chat.Messages.Add(user); store.Commit(chat, ConversationCommit.UserInput(user));
                                var ports = new WorkspaceConversationService.WorkspacePorts(paths, store, journal,
                                    new DocumentArtifactStore(resources, resources, payloads), files, payloads,
                                    new WorkspaceWebSnapshotStore(paths, files), workspace, chat, settings, () => "fixture-key", null,
                                    mutationOrigin: context => journal.MutationOrigin(chat, context), resourceAuthority: resources, resourceMutations: resources);
                                ConversationRunCoordinator.RunAsync(new AgentRunRequest(id, id, user.Content, new AgentRunLimits(5, 5)),
                                    ports, ports.Tools, ports, CancellationToken.None).GetAwaiter().GetResult();
                            }
                        };
                        if (fault == "terminal-ack") RuntimeThrows<RunStoreException>(run); else run();
                    }
                    catch (Exception error) { Console.WriteLine((office ? "Office " : "Workspace ") + fault + ": " + error); throw; }
                    finally { try { listener.Stop(); } catch (ObjectDisposedException) { } AssertTrue(provider.Wait(10000), "mutation provider stops"); }
                }
                resources.Journal.Fault = null; journal.Journal.Fault = null; files.FaultPoint = null;
                var restored = store.Load(chat.Id);
                var call = restored.Messages.Single(item => item.AcceptedCallOrigin != null);
                var origin = new ResourceMutationOrigin(scope, chat.Id, call.RunId, call.ToolCallId, call.AcceptedCallOrigin.StepId);
                var terminal = journal.ReadWindow(chat.Id, long.MaxValue, 8).Single();
                if (unsupported)
                {
                    AssertEqual(ToolExecutionOutcome.Error, terminal.Fact.Result.ExecutionProgress.Outcome, "unconnected owner rejects before dispatch");
                    AssertContains(ToolResultPersistence.ReadContent(terminal.Fact.Result, payloads), "Journal mutation owner is not connected", "explicit owner boundary");
                    AssertEqual(0, resources.Unresolved().Count, "unconnected owner cannot prepare an attempt");
                    AssertEqual(0, restored.Artifacts.Count, "unconnected owner does not create an artifact");
                    AssertEqual(0, dispatched, "unconnected owner does not write a file");
                    AssertEqual(2, calls, "refusal is reported without retry");
                    return;
                }
                AssertEqual(uncertain ? ToolExecutionOutcome.Unknown : ToolExecutionOutcome.Ok, terminal.Fact.Result.ExecutionProgress.Outcome,
                    "the kernel terminal is never promoted by a late receipt");
                AssertEqual(1, uncertain ? restored.LastRun.KernelState.Summary.ToolCounts.WriteUnknown : restored.LastRun.KernelState.Summary.ToolCounts.WriteOk,
                    "exactly one mutation is counted");
                if (uncertain) AssertEqual("repeated_finish_no_progress", restored.LastRun.KernelState.Summary.Reason,
                    "unverified model prose cannot bypass the existing post-unknown inspection requirement");
                if (office) AssertContains(adapter.GetVbaModuleCode("JournalModule"), "Print 17", "Office source was changed by the actual owner");
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
            });
        }
    }
}
