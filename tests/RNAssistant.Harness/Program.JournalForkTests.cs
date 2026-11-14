using System;
using System.IO;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Runtime;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static ChatSession PrepareTextFork(IConversationStore store, ChatSession source, int count)
        {
            var fork = store.CreateTransient(source.Host, source.DocumentKey, source.DocumentTitle, "Fork");
            fork.ParentSessionId = source.Id; fork.ParentSessionRevision = source.Revision;
            fork.ForkedThroughMessageId = count == 0 ? null : source.Messages[count - 1].Id;
            fork.Context = ChatCloneService.CloneContext(source.Context);
            fork.Messages = ChatCloneService.CloneMessages(source.Messages.Take(count));
            return fork;
        }

        private static void JournalForkConsumers()
        {
            foreach (var office in new[] { true, false }) WithTempPaths(paths => {
                var workspace = office ? null : new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "project"));
                var journal = new ConversationJournal(paths, office ? "document:fork" : "workspace:" + workspace.WorkspaceId);
                IConversationStore store = journal;
                var resources = new ResourceJournal(paths); var adapter = new FakeOfficeAdapter();
                var executor = office ? new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths), new ToolStore(paths),
                    () => new AppSettings(), paths: paths, resourceAuthorityStore: resources, resourceMutationJournal: resources,
                    persistResourceFacts: store.Commit) : null;
                var source = office ? new ChatSessionService(adapter, store).CreatePersistentChat("Fork source") :
                    new WorkspaceStore(paths).CreateSession(store, workspace, "Fork source");
                for (var start = 0; start < 160; start += 80)
                {
                    var messages = Enumerable.Range(start, 80).Select(i => new ChatMessage { Role = "user", Content = "Question " + i }).ToArray();
                    source.Messages.AddRange(messages); store.Commit(source, new ConversationCommit(messages));
                }
                ChatSession fork;
                var before = journal.Journal.WrittenBytes;
                if (office)
                {
                    fork = PrepareTextFork(store, source, 140);
                    var plan = ChatCloneService.PrepareForkResources(source, fork, store.LoadArtifactBody,
                        new ResourceForkService(executor.ResourceAuthority, executor.Payloads));
                    executor.MutateChatResources(fork, new ChatResourceMutationIntent(ChatResourceMutationKind.Fork,
                        fork.ForkedThroughMessageId, 139, source: source, fork: plan), () => fork);
                    AssertEqual(0, resources.Unresolved().Count, "Office fork completes its existing resource publication barrier");
                }
                else fork = WorkspaceConversationService.ForkHistorySession(paths, store, workspace, source,
                    source.Messages[139].Id, new AttachmentStore(paths), resources, resources);
                AssertTrue(journal.Journal.WrittenBytes - before < 16384, "large prefix stays outside the bounded journal frame");
                var reads = journal.ChatBodyReads;
                AssertEqual("Question 139", store.ReadMessageWindow(fork.Id, 140, 1).PageMessages.Single().Content, "page follows the frozen source locator");
                AssertEqual(1L, journal.ChatBodyReads - reads, "page does not load the fork manifest or older bodies");
                var loaded = store.Load(fork.Id);
                AssertEqual(140, loaded.Messages.Count, "fork is not limited by the message delta bound");
                AssertEqual(source.Revision, loaded.ParentSessionRevision, "source revision retained");
                AssertTrue(loaded.LastRun == null, "fork has no active source run");
                var grandchild = PrepareTextFork(store, loaded, 140); store.Commit(grandchild, new ConversationCommit());
                WorkspaceConversationService.DeleteHistoryMessage(store, source, source.Messages[0].Id);
                store.Delete(source.Host, source.DocumentKey, source.Id);
                store.Delete(fork.Host, fork.DocumentKey, fork.Id);
                journal.Rebuild(); loaded = store.Load(grandchild.Id);
                AssertEqual("Question 0", loaded.Messages[0].Content, "source rewrite and both ancestor deletions do not rewrite the child");
                AssertEqual(140, loaded.Messages.Count, "multi-generation membership rebuilds");
                var priorIds = loaded.Messages.Select(item => item.Id).ToArray();
                loaded.Messages[0].Content = "Child edit";
                store.Commit(loaded, ConversationCommit.RewriteHistory(priorIds, loaded.Messages));
                var next = new ChatMessage { Role = "user", Content = "Continue" }; loaded.Messages.Add(next);
                store.Commit(loaded, ConversationCommit.UserInput(next));
                AssertEqual("Child edit", store.Load(loaded.Id).Messages[0].Content, "child history can be edited independently");
                AssertEqual(141, store.ReadHeader(loaded.Id).MessageCount, "child accepts new work");
            });
        }

        private static void JournalForkReceipts()
        {
            foreach (var fault in new[] { "before_append", "after_flush", "before_index", "after_fork_index" }) WithTempPaths(paths => {
                var journal = new ConversationJournal(paths, "document:fork-fault"); IConversationStore store = journal;
                var source = store.CreateTransient("Word", "doc", "Document", "Source");
                var message = new ChatMessage { Role = "user", Content = "Exact prefix" }; source.Messages.Add(message);
                store.Commit(source, ConversationCommit.UserInput(message));
                var stale = PrepareTextFork(store, source, 1);
                store.SetTitle(source, "Changed source");
                RuntimeThrows<ChatConcurrencyException>(() => store.Commit(stale, new ConversationCommit()));
                AssertTrue(store.Load(stale.Id) == null, "stale fork leaves no child");
                var fork = PrepareTextFork(store, source, 1); var commit = new ConversationCommit(); var once = true;
                Action<string> fail = point => { if (once && point == fault) { once = false; throw new IOException("fork fault"); } };
                journal.Journal.Fault = fail; journal.Fault = fail;
                try { store.Commit(fork, commit); } catch (IOException) { }
                journal.Journal.Fault = null; journal.Fault = null;
                AssertEqual(fault != "before_append", store.Load(fork.Id) != null, "fork accepts all membership or nothing");
                store.Commit(fork, commit); var revision = fork.Revision;
                store.Delete(source.Host, source.DocumentKey, source.Id);
                store.Commit(fork, commit);
                AssertEqual(revision, fork.Revision, "same-command retry after ancestor deletion does not append");
                journal.Rebuild();
                AssertEqual(message.Content, store.Load(fork.Id).Messages.Single().Content, "receipt and inherited source survive rebuild");
                var forged = PrepareTextFork(store, store.Load(fork.Id), 1); forged.Messages[0].Content = "Forged";
                RuntimeThrows<InvalidOperationException>(() => store.Commit(forged, new ConversationCommit()));
                AssertTrue(store.Load(forged.Id) == null, "changed source evidence is rejected without a child");
            });
        }

        private static void JournalForkUnknown() { JournalMutationTurn(false, "unknown", checkFork: true); }

        private static void VerifyJournalForkUnknown(AppDataPaths paths, ConversationJournal journal, ChatSession source,
            ResourceJournal resources, WorkspaceDescriptor workspace, WorkspaceFileService files, AppSettings settings, string target, string content)
        {
            IConversationStore store = journal;
            var original = journal.ReadWindow(source.Id, long.MaxValue, 8).Single();
            AppendJournalContextHistory(store, source); var beforeCompaction = source.Messages.Count;
            CompactJournalContext(paths, resources, source, settings, "Summary does not resolve Unknown");
            store.Commit(source, new ConversationCommit(source.Messages.Skip(beforeCompaction))); source = store.Load(source.Id);
            var all = WorkspaceConversationService.ForkHistorySession(paths, store, workspace, source, source.Messages.Last().Id, new AttachmentStore(paths), resources, resources);
            AssertEqual(source.ActiveContextCheckpointId, store.Load(all.Id).ActiveContextCheckpointId, "fork inherits the selected local summary");
            AssertTrue(journal.RestoreContext(all.Id, "new-run", 0, 8192).WorkingSet.Requirements.UnknownEffects,
                "an inherited checkpoint cannot discharge an uncertain source effect");
            AssertEqual(source.Messages.Count, store.Load(all.Id).Messages.Count, "accepted calls and results remain visible with original provenance");
            AssertEqual(0, journal.ReadWindow(all.Id, long.MaxValue, 8).Count, "inherited results are not child executions");
            AssertEqual(source.ToolCallReferenceSequence, store.Load(all.Id).ToolCallReferenceSequence, "future tool aliases cannot collide with inherited ones");
            var first = source.Messages.First(item => item.Role == "user");
            var accepted = source.Messages.Single(item => item.AcceptedCallOrigin != null);
            var cut = WorkspaceConversationService.ForkHistorySession(paths, store, workspace, source, accepted.Id, new AttachmentStore(paths), resources, resources);
            cut = store.Load(cut.Id);
            AssertTrue(cut.Messages.Last().ExcludeFromModelContext, "incomplete inherited exchange is excluded without rewriting source evidence");
            WorkspaceConversationService.DeleteHistoryMessage(store, cut, first.Id);
            AssertTrue(store.Load(cut.Id).Messages.Last().ExcludeFromModelContext, "child history rewrite preserves derived exclusion");
            var prefix = WorkspaceConversationService.ForkHistorySession(paths, store, workspace, source, first.Id, new AttachmentStore(paths), resources, resources);
            var child = WorkspaceConversationService.ForkHistorySession(paths, store, workspace, store.Load(prefix.Id), first.Id, new AttachmentStore(paths), resources, resources);
            store.Delete(source.Host, source.DocumentKey, source.Id); store.Delete(prefix.Host, prefix.DocumentKey, prefix.Id);
            journal.Rebuild(); child = store.Load(child.Id);
            AssertTrue(!child.Messages.Any(item => item.AcceptedCallOrigin != null), "Unknown lies outside the copied visible prefix");
            AssertEqual(original.Position.Hash, journal.ReadContextOperation(child.Id, original.OperationId).Result.Position.Hash,
                "frozen inherited obligation retains the original execution owner after ancestor deletion");
            JournalHistoryGuardAfterMutation(paths, journal, child, resources, workspace, files, settings, target, content, original);
        }
    }
}
