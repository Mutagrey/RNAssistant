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
        private static void JournalHistoryConsumers()
        {
            WithTempPaths(paths => {
                var journal = new ConversationJournal(paths, "document:history"); IConversationStore store = journal;
                var adapter = new FakeOfficeAdapter(); var resources = new ResourceJournal(paths);
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths), new ToolStore(paths),
                    () => new AppSettings(), paths: paths, resourceAuthorityStore: resources, resourceMutationJournal: resources,
                    persistResourceFacts: store.Commit);
                var session = new ChatSessionService(adapter, store).CreatePersistentChat("History");
                for (var start = 0; start < 160; start += 80)
                {
                    var messages = Enumerable.Range(start, 80).Select(i => new ChatMessage { Role = "user", Content = "Question " + i }).ToArray();
                    session.Messages.AddRange(messages); store.Commit(session, new ConversationCommit(messages));
                }
                var old = store.Load(session.Id); var target = session.Messages[130];
                var editor = new ChatHistoryEditService(_ => { }, (chat, reason) => { });
                executor.MutateChatResources(session, new ChatResourceMutationIntent(ChatResourceMutationKind.Edit,
                    target.Id, 130, "Edited"), () => editor.RewriteUserMessage(session, session.Id, target.Id, 130, "Edited"));
                AssertEqual(131, store.ListHeaders(false).Single().MessageCount, "large rewrite is not limited by the 128-message frame bound");
                var reads = journal.ChatBodyReads;
                AssertEqual("Edited", store.ReadMessageWindow(session.Id, 131, 1).PageMessages.Single().Content, "edited user occurrence");
                AssertEqual(1L, journal.ChatBodyReads - reads, "a page does not hydrate the history manifest");
                RuntimeThrows<ChatConcurrencyException>(() => store.Commit(old, ConversationCommit.RewriteHistory(old.Messages.Select(item => item.Id), old.Messages)));
                WorkspaceConversationService.DeleteHistoryMessage(paths, store, session, session.Messages[2].Id, resources, resources);
                AssertEqual(130, store.Load(session.Id).Messages.Count, "Workspace deletion uses the same port and reindexes pages");
                var resurrection = old.Messages.Last();
                var beforeClear = ConversationEventFeed.Cursor(session);
                executor.MutateChatResources(session, new ChatResourceMutationIntent(ChatResourceMutationKind.Clear), () => {
                    editor.Clear(session, session.Context); return true;
                });
                AssertEqual(0, store.Load(session.Id).Messages.Count, "Office clear persists through the actual resource observer");
                AssertEqual(0, resources.Unresolved().Count, "UI history authority completes with read-back");
                journal.Rebuild();
                AssertEqual(0, store.Load(session.Id).Messages.Count, "rebuild retains clear");
                var notices = new ConversationEventFeed(journal).Read(ConversationMetadata.From(session), beforeClear);
                AssertTrue(notices.Events.SelectMany(item => item.Operations).Contains(SessionOperationTypes.MessageRemove), "clear invalidates the visible message page");
                // DocumentAuthorityRegistry is a separate existing P6/P7 backup owner.
                AssertTrue(File.Exists(Path.Combine(paths.ResourceAuthorityDirectory, "documents.json")), "Office binding retains its identity registry");
                RuntimeThrows<InvalidOperationException>(() => JournalCasService(paths, StorageProtector.None).BackupJournals(Path.Combine(paths.Root, "backup")));
                session.Messages.Add(resurrection);
                RuntimeThrows<InvalidOperationException>(() => store.Commit(session, ConversationCommit.UserInput(resurrection)));
                session.Messages.Clear(); var next = new ChatMessage { Role = "user", Content = "Continue" };
                session.Messages.Add(next); store.Commit(session, ConversationCommit.UserInput(next));
                AssertEqual(next.Id, store.Load(session.Id).Messages.Single().Id, "new work remains available");
            });
        }

        private static void JournalHistoryReceipts()
        {
            foreach (var fault in new[] { "before_append", "after_flush", "before_index", "after_history_index" }) WithTempPaths(paths => {
                var journal = new ConversationJournal(paths, "document:history-receipt"); IConversationStore store = journal;
                var chat = store.CreateTransient("Word", "doc", "Document", "History");
                var first = new ChatMessage { Role = "user", Content = "First" }; var second = new ChatMessage { Role = "user", Content = "Second" };
                chat.Messages.AddRange(new[] { first, second }); store.Commit(chat, new ConversationCommit(chat.Messages));
                var original = chat.Revision; var previous = chat.Messages.Select(item => item.Id).ToArray();
                chat.Messages.Remove(second); first.Content = "Edited";
                var command = ConversationCommit.RewriteHistory(previous, chat.Messages);
                Action<string> fail = point => { if (point == fault) throw new IOException("history fault"); };
                if (fault == "before_index" || fault == "after_history_index") journal.Fault = fail; else journal.Journal.Fault = fail;
                if (fault == "before_append" || fault == "after_flush") RuntimeThrows<IOException>(() => store.Commit(chat, command));
                else store.Commit(chat, command);
                journal.Fault = null; journal.Journal.Fault = null;
                if (fault == "before_append") AssertEqual(original, store.Load(chat.Id).Revision, "unaccepted rewrite leaves old history");
                else AssertEqual("Edited", store.Load(chat.Id).Messages.Single().Content, "accepted rewrite survives index/ack loss");
                store.Commit(chat, command); var bytes = journal.Journal.WrittenBytes; store.Commit(chat, command);
                AssertEqual(bytes, journal.Journal.WrittenBytes, "retry does not duplicate membership or bodies");
                first.Content = "Conflict"; RuntimeThrows<InvalidOperationException>(() => store.Commit(chat, command));
                journal.Rebuild(); AssertEqual("Edited", store.Load(chat.Id).Messages.Single().Content, "rebuild restores exact accepted text");
            });
        }

        private static void JournalHistoryUnknownConsumer() { JournalMutationTurn(false, "unknown", checkHistory: true); }

        private static async Task JournalHistoryGuardPolicy()
        {
            foreach (var explicitApproval in new[] { false, true })
            {
                var fixture = new KernelFixture(KernelResponse(KernelCall("external")), KernelResponse());
                var policy = new ToolPolicySnapshot("external", "policy", new ToolPolicy(ToolEffect.External,
                    ToolVerification.None, explicitApproval, false, new[] { "agent" }));
                fixture.Tools.Policies["external"] = policy;
                var old = new ToolExecutionContext(new ToolCall("old", "external", "{}"), policy,
                    "old-run", "old-turn", "old-step", DateTime.UtcNow, false, 1);
                var progress = ToolExecutionProgress.Capture(KernelRecord(old, ToolExecutionOutcome.Unknown));
                fixture.Store.ReplayGuard = call => progress;
                fixture.Tools.OnExecute = (context, token) => Task.FromResult(KernelRecord(context,
                    context.IsConfirmed ? ToolExecutionOutcome.Ok : ToolExecutionOutcome.AwaitingConfirmation));
                var result = await fixture.RunAsync();
                if (!explicitApproval) AssertEqual(0, fixture.Tools.Calls.Count, "automatic opaque replay remains blocked");
                else
                {
                    AssertEqual(RunLifecycle.AwaitingConfirmation, result.Summary.Lifecycle, "fresh opaque approval is still available");
                    await fixture.Kernel.ResumeAsync(result.Summary.RunId, result.Summary.PendingConfirmation.PendingId,
                        result.Continuation, CancellationToken.None);
                    AssertTrue(fixture.Tools.Calls.Last().IsConfirmed, "only fresh explicit confirmation dispatches the opaque repeat");
                }
            }
        }

        private static void JournalHistoryGuardAfterMutation(AppDataPaths paths, ConversationJournal journal, ChatSession chat,
            ResourceJournal resources, WorkspaceDescriptor workspace, WorkspaceFileService files, AppSettings settings,
            string target, string content, ConversationResultReceipt inherited = null)
        {
            IConversationStore store = journal; var payloads = new ChatBlobStore(paths);
            var source = chat.Messages.SingleOrDefault(item => item.AcceptedCallOrigin != null);
            var original = inherited ?? journal.ReadWindow(chat.Id, long.MaxValue, 8).Single();
            var terminalHash = original.Position.Hash;
            if (source != null) WorkspaceConversationService.DeleteHistoryMessage(paths, store, chat, source.Id, resources, resources);
            else if (inherited != null) WorkspaceConversationService.DeleteHistoryMessage(paths, store, chat, chat.Messages[0].Id, resources, resources);
            AssertTrue(!store.Load(chat.Id).Messages.Any(item => item.AcceptedCallOrigin != null), "visible call/result pair was deleted");
            journal.Rebuild(); chat = store.Load(chat.Id);
            var repeated = new ToolCall("new-call", "core.write", new JObject { ["content"] = content, ["target"] = target }.ToString());
            var metadata = ConversationMetadata.From(chat);
            AssertEqual(ToolExecutionOutcome.Unknown, ((IEventStore)journal).ReadReplayGuard(metadata, repeated).Outcome,
                "argument property order and new call ID cannot evade the retained guard");
            var backup = Path.Combine(paths.Root, "history-backup"); JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
            var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "history-restore"), _ => StorageProtector.None);
            var restoredJournal = new ConversationJournal(restored, "workspace:" + workspace.WorkspaceId);
            AssertEqual(terminalHash, restoredJournal.ReadContextOperation(chat.Id, original.OperationId).Result.Position.Hash, "hidden Unknown remains canonical after restore");
            AssertEqual(ToolExecutionOutcome.Unknown, ((IEventStore)restoredJournal).ReadReplayGuard(metadata, repeated).Outcome, "restored guard survives a fresh index");

            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
            settings.BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            files.FaultPoint = null; var calls = 0;
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
                            var number = ++calls; AssertTrue(number <= 3, "guarded call and independent read remain bounded");
                            var response = number == 1 ? ModelProtocolWire.WriteAcceptedCall("Repeat the operation.", null,
                                new ConversationToolCall { Name = "core.write", Arguments = JObject.Parse(repeated.ArgumentsJson) }) :
                                number == 2 ? ModelProtocolWire.WriteAcceptedCall("Inspect actual state.", null,
                                    new ConversationToolCall { Name = "core.read", Arguments = new JObject { ["target"] = target } }) :
                                ScriptedVerifiedFinish(store.Load(chat.Id), "The write was not repeated; source was read.", "Inspected.", "Source inspected.", "core.read");
                            await SteeringReply(request, response).ConfigureAwait(false);
                        }
                    }
                    catch (HttpListenerException) { }
                    catch (ObjectDisposedException) { }
                    catch (Exception error) { Console.WriteLine("History provider: " + error); listener.Abort(); throw; }
                });
                try
                {
                    var run = Guid.NewGuid().ToString("N");
                    chat.LastRun = new ChatRunRecord { RunId = run, TurnId = run, StartedUtc = DateTime.UtcNow,
                        ConversationPolicy = ConversationRunPolicy.For(ChatModes.Agent, autoConfirmToolActions: true) };
                    var user = new ChatMessage { Role = "user", Content = "Continue after deletion", RunId = run };
                    chat.Messages.Add(user); store.Commit(chat, ConversationCommit.UserInput(user));
                    var ports = new WorkspaceConversationService.WorkspacePorts(paths, store, journal,
                        new DocumentArtifactStore(resources, resources, payloads), files, payloads,
                        new WorkspaceWebSnapshotStore(paths, files), workspace, chat, settings, () => "fixture-key", null,
                        mutationOrigin: context => journal.MutationOrigin(chat, context), resourceAuthority: resources, resourceMutations: resources);
                    ConversationRunCoordinator.RunAsync(new AgentRunRequest(run, run, user.Content, new AgentRunLimits(4, 4)),
                        ports, ports.Tools, ports, CancellationToken.None).GetAwaiter().GetResult();
                    AssertEqual(RunLifecycle.Completed, chat.LastRun.KernelState.Summary.Lifecycle, "ordinary read and finish remain available");
                }
                finally { try { listener.Stop(); } catch (ObjectDisposedException) { } provider.GetAwaiter().GetResult(); }
            }
            var results = journal.ReadWindow(chat.Id, long.MaxValue, 8);
            AssertEqual(inherited == null ? 3 : 2, results.Count, "only this chat's own executions become result facts");
            AssertEqual(ToolExecutionOutcome.NotDispatched, results[results.Count - 2].Fact.Result.ExecutionProgress.Outcome, "kernel blocks before runtime preparation/dispatch");
            AssertContains(ToolResultPersistence.ReadContent(results[results.Count - 2].Fact.Result, payloads), "previous identical call", "action-specific guard feedback");
            AssertEqual(ToolExecutionOutcome.Ok, results.Last().Fact.Result.ExecutionProgress.Outcome, "independent read succeeds");
            AssertEqual(terminalHash, journal.ReadContextOperation(chat.Id, original.OperationId).Result.Position.Hash, "new work never upgrades the original Unknown");
            AssertEqual(content, File.ReadAllText(Path.Combine(workspace.RootPath, target)), "no repeated write");
        }
    }
}
