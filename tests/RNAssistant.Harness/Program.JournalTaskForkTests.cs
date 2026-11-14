using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RNAssistant.Core.Agent;
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
        private static void VerifyJournalTaskForkRuntime(AppDataPaths paths, ConversationJournal journal,
            ResourceJournal resources, ChatSession source, bool office)
        {
            IConversationStore store = journal; var blobs = new ChatBlobStore(paths);
            var sourceHead = resources.GetHead(TaskListPublication.Scope(source), TaskListPublication.Identity(source));
            var closed = ForkSharedJournal(paths, journal, resources, source, source.Messages.Count);
            AssertTrue(closed.ActiveTaskListArtifactId == null, "a closed prefix never reactivates an older task list");
            AssertEqual(4, closed.Artifacts.Count, "real typed results retain every closed historical revision");
            var selected = source.Artifacts.Single(item => item.Kind == ChatArtifactKinds.TaskList && item.Revision == 3);
            var index = source.Messages.FindLastIndex(message => message.ResourceRefs.Any(reference => {
                string owner, id; int revision;
                return ChatResourceUri.TryParseArtifactRevision(reference, out owner, out id, out revision) && id == selected.Id;
            }));
            AssertTrue(index >= 0, "real kernel result references the blocked revision");
            var child = ForkSharedJournal(paths, journal, resources, source, index + 1);
            journal.Rebuild(); child = store.Load(child.Id);
            AssertEqual(selected.Id, child.ActiveTaskListArtifactId, "fork selects the exact historical blocked revision");
            AssertEqual("blocked", TaskListService.Active(child).Status, "blocked task state remains editable");
            AssertEqual(3, child.Artifacts.Count, "fork retains prefix snapshots and their parent lineage only");
            AssertEqual(1, child.Artifacts.Count(item => item.InlineText != null), "only selected Task bytes are hydrated");
            TaskListPublication.RequireCurrent(child, resources.Capture(TaskListPublication.Scope(child)), resources, blobs);
            AssertEqual(0, journal.ReadWindow(child.Id, long.MaxValue, 32).Count, "inherited task results are not child executions");
            var reads = journal.ChatBodyReads;
            var page = store.ReadMessageWindow(child.Id, child.Messages.Count, 1);
            AssertEqual(2L, journal.ChatBodyReads - reads, "page reads text/manifest without Task bodies");
            AssertTrue(page.Artifacts.Any(item => item.Id == selected.Id) && page.Artifacts.All(item => item.InlineText == null), "local presentation links address inherited cold metadata");

            Func<TaskListMutation> edit = () => new TaskListService().UpdateStatuses(child, null,
                new List<TaskListStatusUpdate> { new TaskListStatusUpdate { Index = 1, Status = "in_progress", Note = "Child only" } }, () => { });
            if (office)
            {
                var executor = new OfficeToolExecutor(FakeOfficeAdapter.ForHost(child.Host), new VbaJournalStore(paths), new SkillStore(paths),
                    new ToolStore(paths), () => new AppSettings(), paths: paths, resourceAuthorityStore: resources,
                    resourceMutationJournal: resources, persistResourceFacts: store.Commit);
                AssertTrue(executor.MutateLocalResources(child, TaskListToolCatalog.SetToolId,
                    new Dictionary<string, object>(), edit).Success, "Office edits the fork through its actual publication observer");
            }
            else
            {
                var observer = new WorkspaceTaskListObserver(paths, store, blobs, child, resources, resources);
                var context = new ToolExecutionContext(new ToolCall("child-task", TaskListToolCatalog.SetToolId, "{}"),
                    new ToolPolicySnapshot(TaskListToolCatalog.SetToolId, "fixture", true), "run", "turn", "step", DateTime.UtcNow, true, 1);
                var attempt = observer.Prepare(context, new Dictionary<string, object>());
                observer.MarkDispatchMayHaveOccurred(attempt); AssertTrue(edit().Success, "Workspace edits its own fork state");
                observer.Complete(attempt, new ToolExecutionRecord(context, ToolExecutionOutcome.Ok, DateTime.UtcNow,
                    evidence: new ToolExecutionEvidence(ToolDispatchEvidence.MayHaveDispatched, ToolEffectEvidence.VerifiedChange)));
            }
            child = store.Load(child.Id);
            AssertEqual("Child only", TaskListService.Active(child).Steps.Single().Note, "child mutation survives durable read-back");
            AssertEqual(selected.Id, child.Artifacts.Single(item => item.Id == child.ActiveTaskListArtifactId).ParentArtifactId,
                "a new child revision keeps the inherited exact parent");
            AssertEqual(sourceHead.AuthorityGeneration, resources.GetHead(TaskListPublication.Scope(source), TaskListPublication.Identity(source)).AuthorityGeneration,
                "fork/edit never rewinds or republishes source authority");
            AssertEqual(4, store.Load(source.Id).Artifacts.Count, "source snapshots remain unchanged");
        }

        private static void JournalTaskForkRecovery()
        {
            foreach (var fault in new[] { "before-append", "chat-ack", "resource-ack", "readback-mismatch" })
                WithJournalInput(false, (paths, journal, source, scope) => {
                    IConversationStore store = journal; var resources = new ResourceJournal(paths); var blobs = new ChatBlobStore(paths);
                    var task = NewJournalTask(source).Artifact;
                    var message = new ChatMessage { Role = "user", Content = "Retain this task", ResourceRefs = new List<ResourceRef> { ChatResourceUri.CreateArtifactRevision(source, task) } };
                    source.Messages.Add(message); store.Commit(source, ConversationCommit.UserInput(message));
                    var child = PrepareTextFork(store, source, 1);
                    ChatCloneService.PrepareForkResources(source, child, store.LoadArtifactBody,
                        new ResourceForkService(new ResourceAuthorityService(resources, resources, resources, blobs), blobs));
                    var observer = new WorkspaceTaskListObserver(paths, store, blobs, child, resources, resources);
                    journal.Journal.Fault = point => {
                        if (point == (fault == "before-append" ? "before_append" : "after_flush") && fault != "resource-ack")
                        { journal.Journal.Fault = null; throw new IOException(fault); }
                    };
                    if (fault == "resource-ack") journal.Fault = point => {
                        if (point == "before_index") resources.Journal.Fault = stage => {
                            if (stage == "after_flush") { resources.Journal.Fault = null; throw new IOException(fault); }
                        };
                    };
                    RuntimeThrows<IOException>(() => observer.CommitFork());
                    journal.Journal.Fault = null; journal.Fault = null; resources.Journal.Fault = null;
                    var saved = store.Load(child.Id);
                    AssertEqual(fault != "before-append", saved != null, "fork acceptance is all or nothing");
                    if (fault == "readback-mismatch") { saved.ActiveTaskListArtifactId = null; store.Commit(saved, new ConversationCommit()); }
                    store.Delete(source.Host, source.DocumentKey, source.Id); journal.Rebuild();
                    observer = new WorkspaceTaskListObserver(paths, store, blobs, saved ?? child, resources, resources);
                    observer.ReconcileInterrupted();
                    var head = resources.GetHead(TaskListPublication.Scope(child), TaskListPublication.Identity(child));
                    var known = fault == "chat-ack" || fault == "resource-ack";
                    AssertEqual(known ? HeadKnowledge.Known : HeadKnowledge.Unknown, head.Knowledge,
                        "only exact durable fork read-back can publish Known; missing/different state remains Unknown");
                    var writes = journal.Journal.WrittenBytes; var generation = head.AuthorityGeneration;
                    observer.ReconcileInterrupted();
                    AssertEqual(writes, journal.Journal.WrittenBytes, "recovery never replays fork creation or Task mutation");
                    AssertEqual(generation, resources.GetHead(TaskListPublication.Scope(child), TaskListPublication.Identity(child)).AuthorityGeneration,
                        "lost acknowledgement recovery is idempotent");
                    AssertEqual(0, resources.Unresolved().Count, "the exact pending publication is resolved");
                    if (!known) return;
                    saved = store.Load(child.Id);
                    AssertEqual(task.ContentSha256, saved.Artifacts.Single().ContentSha256, "source deletion retains original CAS ownership");
                    TaskListPublication.RequireCurrent(saved, resources.Capture(TaskListPublication.Scope(saved)), resources, blobs);
                    var backup = Path.Combine(paths.Root, "task-fork-backup"); JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                    var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "task-fork-restore"), _ => StorageProtector.None);
                    var restoredResources = new ResourceJournal(restored); var restoredChild = ((IConversationStore)new ConversationJournal(restored, scope)).Load(child.Id);
                    TaskListPublication.RequireCurrent(restoredChild, restoredResources.Capture(TaskListPublication.Scope(restoredChild)), restoredResources, new ChatBlobStore(restored));
                });
        }
    }
}
