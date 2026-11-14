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
using RNAssistant.Runtime;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static ChatArtifact PublishWorkspaceTask(AppDataPaths paths, IConversationStore store, ChatSession chat,
            IResourceAuthorityStore authority = null, IResourceMutationJournal mutations = null)
        {
            var observer = new WorkspaceTaskListObserver(paths, store, new ChatBlobStore(paths), chat, authority, mutations);
            var context = new ToolExecutionContext(new ToolCall(Guid.NewGuid().ToString("N"), TaskListToolCatalog.SetToolId, "{}"),
                new ToolPolicySnapshot(TaskListToolCatalog.SetToolId, "fixture", true), "run", "turn", "step", DateTime.UtcNow, true, 1);
            var attempt = observer.Prepare(context, new Dictionary<string, object>());
            observer.MarkDispatchMayHaveOccurred(attempt);
            var task = NewJournalTask(chat).Artifact;
            observer.Complete(attempt, new ToolExecutionRecord(context, ToolExecutionOutcome.Ok, DateTime.UtcNow,
                evidence: new ToolExecutionEvidence(ToolDispatchEvidence.MayHaveDispatched, ToolEffectEvidence.VerifiedChange)));
            return task;
        }

        private static void JournalTaskHistoryReset()
        {
            foreach (var edit in new[] { false, true })
            foreach (var fault in new[] { "none", "before-append", "chat-ack", "resource-ack", "mismatch" })
                WithJournalInput(false, (paths, journal, chat, scope) => {
                    IConversationStore store = journal; var resources = new ResourceJournal(paths); var blobs = new ChatBlobStore(paths);
                    var task = PublishWorkspaceTask(paths, store, chat, resources, resources);
                    var target = new ChatMessage { Role = "user", Content = "Reset this history", ResourceRefs =
                        new List<ResourceRef> { ChatResourceUri.CreateArtifactRevision(chat, task) } };
                    chat.Messages.Add(target); store.Commit(chat, ConversationCommit.UserInput(target));
                    var taskScope = TaskListPublication.Scope(chat); var identity = TaskListPublication.Identity(chat);
                    AssertEqual(HeadKnowledge.Known, resources.GetHead(taskScope, identity).Knowledge, "fixture has published selected Tasks");
                    Action rewrite;
                    if (edit)
                    {
                        string[] before;
                        var source = WorkspaceConversationService.PrepareWorkspaceMessageEdit(chat,
                            new WorkspaceSubmitInputRequest { EditMessageId = target.Id, ExpectedRevision = chat.Revision }, out before);
                        var replacement = new ChatMessage { Role = "user", Content = "Edited", ResourceRefs = source.ResourceRefs };
                        chat.Messages.Add(replacement);
                        var commit = ConversationCommit.RewriteHistory(before, chat.Messages);
                        rewrite = () => new WorkspaceTaskListObserver(paths, store, blobs, chat, resources, resources)
                            .CommitHistoryReset(commit, source.Id, replacement.Id);
                    }
                    else rewrite = () => WorkspaceConversationService.DeleteHistoryMessage(paths, store, chat, target.Id.ToUpperInvariant(), resources, resources);

                    if (fault != "none" && fault != "resource-ack") journal.Journal.Fault = point => {
                        if (point == (fault == "before-append" ? "before_append" : "after_flush"))
                        { journal.Journal.Fault = null; throw new IOException(fault); }
                    };
                    if (fault == "resource-ack") journal.Fault = point => {
                        if (point == "before_index") resources.Journal.Fault = stage => {
                            if (stage == "after_flush") { resources.Journal.Fault = null; throw new IOException(fault); }
                        };
                    };
                    if (fault == "none") rewrite(); else RuntimeThrows<IOException>(rewrite);
                    journal.Journal.Fault = null; journal.Fault = null; resources.Journal.Fault = null;
                    chat = store.Load(chat.Id);
                    if (fault == "mismatch")
                    {
                        if (!chat.Artifacts.Any(item => item.Id == task.Id)) chat.Artifacts.Add(task.CopyMetadata());
                        chat.ActiveTaskListArtifactId = task.Id; store.Commit(chat, new ConversationCommit());
                    }
                    // Recovery must tolerate unrelated new input without retrying the rewrite.
                    var independent = new ChatMessage { Role = "user", Content = "Independent input" };
                    chat.Messages.Add(independent); store.Commit(chat, ConversationCommit.UserInput(independent));
                    journal.Rebuild(); chat = store.Load(chat.Id);
                    var writes = journal.Journal.WrittenBytes;
                    var observer = new WorkspaceTaskListObserver(paths, store, blobs, chat, resources, resources);
                    observer.ReconcileInterrupted();
                    var verified = fault != "before-append" && fault != "mismatch";
                    var head = resources.GetHead(taskScope, identity);
                    AssertEqual(verified ? HeadKnowledge.Unavailable : HeadKnowledge.Unknown, head.Knowledge,
                        "only durable removal/replacement with cleared selection proves the Task reset: " + fault);
                    AssertEqual(writes, journal.Journal.WrittenBytes, "recovery never replays or edits history");
                    observer.ReconcileInterrupted();
                    AssertEqual(head.AuthorityGeneration, resources.GetHead(taskScope, identity).AuthorityGeneration, "recovery is idempotent");
                    AssertEqual(0, resources.Unresolved().Count, "history publication is terminal");
                    AssertTrue(chat.Messages.Any(item => item.Id == independent.Id), "independent input survives recovery");
                    if (!verified) return;
                    TaskListPublication.RequireCurrent(chat, resources.Capture(taskScope), resources, blobs);
                    if (edit && fault == "chat-ack")
                    {
                        var backup = Path.Combine(paths.Root, "history-task-backup");
                        JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                        var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "history-task-restore"), _ => StorageProtector.None);
                        var restoredResources = new ResourceJournal(restored);
                        var restoredChat = ((IConversationStore)new ConversationJournal(restored, scope)).Load(chat.Id);
                        TaskListPublication.RequireCurrent(restoredChat, restoredResources.Capture(taskScope), restoredResources, new ChatBlobStore(restored));
                        AssertEqual(TaskListPublication.ReadBody(task, blobs), TaskListPublication.ReadBody(task, new ChatBlobStore(restored)),
                            "history reset preserves original Task CAS bytes for restore");
                    }
                    PublishWorkspaceTask(paths, store, chat, resources, resources);
                    AssertTrue(new WorkspaceTaskListObserver(paths, store, blobs, store.Load(chat.Id), resources, resources).Inspect().Available,
                        "a new explicit Task mutation works after history reset");
                });
        }
    }
}
