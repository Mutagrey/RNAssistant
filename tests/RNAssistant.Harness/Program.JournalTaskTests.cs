using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.ModelProtocol;
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
        private static string JournalTaskResponse(int number, ChatSession session)
        {
            if (number == 1) return ModelProtocolWire.WriteAcceptedCall("Load the task tool contract.", null,
                new ConversationToolCall { Name = "core.tools.read", Arguments = new JObject { ["id"] = TaskListToolCatalog.SetToolId } });
            number--;
            if (number == 5) return ScriptedVerifiedFinish(session, "Checklist saved.", "Done.", "Checklist outcome recorded.", Enumerable.Repeat(TaskListToolCatalog.SetToolId, 4).ToArray());
            var arguments = number == 1 ? new JObject { ["action"] = "save", ["goal"] = "Retain progress", ["steps"] = new JArray(new JObject { ["text"] = "Check source" }) } :
                number == 2 ? new JObject { ["action"] = "update_statuses", ["updates"] = new JArray(new JObject { ["index"] = 1, ["status"] = "in_progress", ["note"] = "Exact source inspected" }) } :
                new JObject { ["action"] = "close", ["outcome"] = number == 3 ? "blocked" : "completed", ["reason"] = number == 3 ? "Awaiting source" : "Reviewed" };
            return ModelProtocolWire.WriteAcceptedCall("Record current task assessment.", null,
                new ConversationToolCall { Name = TaskListToolCatalog.SetToolId, Arguments = arguments });
        }

        private static void VerifyJournalTaskRuntime(AppDataPaths paths, ConversationJournal journal, ResourceJournal resources,
            ChatSession chat, string scope, int calls, bool office)
        {
            if (calls != 6) Console.WriteLine("Task runtime " + (office ? "Office" : "Workspace") + ": " +
                chat.LastRun.KernelState.Summary.Reason + " | " + string.Join(" | ", chat.Messages.Where(item =>
                    item.ExecutionProgress != null || item.Activity != null).Select(item => item.Content + " " + item.Activity?.DataJson)));
            AssertEqual(6, calls, "schema admission, four task revisions and finish without repair or replay");
            AssertEqual(4, chat.LastRun.KernelState.Summary.ToolCounts.WriteOk, "kernel counts four verified task mutations");
            AssertTrue(chat.ActiveTaskListArtifactId == null && chat.Artifacts.Count == 4, "close retains the complete revision chain");
            AssertTrue(chat.Artifacts.All(item => item.InlineText == null), "closed historical task bodies stay cold");
            var last = chat.Artifacts.OrderBy(item => item.Revision).Last();
            var body = JsonConvert.DeserializeObject<ChatTaskList>(TaskListPublication.ReadBody(last, new ChatBlobStore(paths)));
            AssertEqual("completed", body.Status, "the outcome is retained");
            AssertEqual("Exact source inspected", body.Steps[0].Note, "progress survives blocked and close");
            var terminals = journal.ReadWindow(chat.Id, long.MaxValue, 8);
            AssertEqual(5, terminals.Count, "one terminal per accepted read or mutation");
            foreach (var call in chat.Messages.Where(item => item.AcceptedCallOrigin != null && TaskListToolCatalog.Owns(item.ToolName)))
            {
                var origin = new ResourceMutationOrigin(scope, chat.Id, call.RunId, call.ToolCallId, call.AcceptedCallOrigin.StepId);
                AssertTrue(resources.CallOutcome(origin) != null, "real consumer retains exact cross-journal proof");
            }
            var taskScope = TaskListPublication.Scope(chat);
            AssertEqual(HeadKnowledge.Unavailable, resources.GetHead(taskScope, TaskListPublication.Identity(chat)).Knowledge, "close publishes removal of active state");
            journal.Rebuild();
            AssertEqual(4, ((IConversationStore)journal).Load(chat.Id).Artifacts.Count, "rebuild restores immutable snapshots");
            if (office) return;
            var backup = Path.Combine(paths.Root, "task-runtime-backup");
            JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
            var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "task-runtime-restore"), _ => StorageProtector.None);
            var restoredJournal = new ConversationJournal(restored, scope);
            AssertEqual(4, ((IConversationStore)restoredJournal).Load(chat.Id).Artifacts.Count, "restore retains Task List consumers' state");
            AssertEqual(terminals.Last().Position.Hash, restoredJournal.ReadWindow(chat.Id, long.MaxValue, 8).Last().Position.Hash, "restore keeps exact runtime evidence");
        }

        private static TaskListMutation NewJournalTask(ChatSession chat)
        {
            var result = new TaskListService().Set(chat, "Retain progress", new List<ChatTaskStep> { new ChatTaskStep { Text = "Check source" } }, () => { });
            AssertTrue(result.Success, "shared task domain creates a snapshot"); return result;
        }

        private static void JournalTaskSnapshots()
        {
            foreach (var office in new[] { true, false }) WithJournalInput(office, (paths, journal, chat, scope) => {
                IConversationStore store = journal; var blobs = new ChatBlobStore(paths);
                var first = NewJournalTask(chat).Artifact;
                var message = new ChatMessage { Role = "user", Content = "First assessment", ResourceRefs = new List<ResourceRef> { ChatResourceUri.CreateArtifactRevision(chat, first) } };
                chat.Messages.Add(message);
                journal.Fault = point => { if (point == "before_index") throw new IOException("index unavailable"); };
                store.Commit(chat, ConversationCommit.UserInput(message)); journal.Fault = null;
                chat = store.Load(chat.Id);
                AssertEqual(first.Id, chat.ActiveTaskListArtifactId, "index recovery retains active snapshot without a tool transcript");
                var rewind = new ChatMessage { Role = "user", Content = "Continue" };
                chat.Messages.Add(rewind); store.Commit(chat, ConversationCommit.UserInput(rewind));
                var changed = new TaskListService().UpdateStatuses(chat, null, new List<TaskListStatusUpdate> {
                    new TaskListStatusUpdate { Index = 1, Status = "in_progress", Note = "Retained note" } }, () => { });
                AssertTrue(changed.Success, "active snapshot resumes after restart");
                var next = new ChatMessage { Role = "user", Content = "Updated assessment", ResourceRefs = new List<ResourceRef> { ChatResourceUri.CreateArtifactRevision(chat, changed.Artifact) } };
                chat.Messages.Add(next); var command = ConversationCommit.UserInput(next);
                journal.Journal.Fault = point => { if (point == "after_flush") throw new IOException("lost acknowledgement"); };
                RuntimeThrows<IOException>(() => store.Commit(chat, command)); journal.Journal.Fault = null;
                store.Commit(chat, command); var bytes = journal.Journal.WrittenBytes;
                store.Commit(chat, command); AssertEqual(bytes, journal.Journal.WrittenBytes, "same receipt never duplicates a revision");
                journal.Rebuild(); chat = store.Load(chat.Id);
                AssertEqual(1, chat.Artifacts.Count(item => item.InlineText != null), "only the active bounded body is hydrated");
                AssertEqual("Retained note", TaskListService.Active(chat).Steps.Single().Note, "domain resumes exact task state");
                VerifyJournalForkPreparation(paths, journal, new ResourceJournal(paths), chat, changed.Artifact.Id);
                var reads = journal.ChatBodyReads;
                var page = store.ReadMessageWindow(chat.Id, 1, 1);
                AssertEqual(1, page.Artifacts.Count, "page addresses only referenced task metadata");
                AssertTrue(page.Artifacts.Single().InlineText == null, "message page leaves historical body cold");
                AssertEqual(changed.Artifact.Id, page.ActiveTaskListArtifactId, "page exposes the current selection");
                AssertEqual(2L, journal.ChatBodyReads - reads, "only page text and working-set manifest are hydrated");
                var cold = chat.Artifacts.Single(item => item.Id == first.Id);
                cold.Title = "Rebound";
                RuntimeThrows<InvalidOperationException>(() => store.Commit(chat, new ConversationCommit()));
                chat = store.Load(chat.Id);
                var loads = 0;
                var editor = new ChatHistoryEditService(_ => { }, (current, reason) => { }, (current, id) => {
                    loads++; AssertEqual(first.Id, id, "history addresses only the selected snapshot");
                    TaskListPublication.HydrateActive(current, blobs); return true;
                });
                editor.RewriteUserMessage(chat, chat.Id, rewind.Id, 1, "Revisited assessment");
                AssertEqual(first.Id, chat.ActiveTaskListArtifactId, "history selection uses immutable metadata with cold bodies");
                AssertEqual(1, loads, "editor does not hydrate all historical Task Lists");
                AssertEqual("pending", TaskListService.Active(chat).Steps.Single().Status, "addressed selection retains original assessment");
                store.Commit(chat, ConversationCommit.RewriteHistory(new[] { message.Id, rewind.Id, next.Id }, chat.Messages));
                chat = store.Load(chat.Id);
                var usage = store.ListHeaders().Single().CasBlobCount;
                var previous = chat.Messages.Select(item => item.Id).ToArray();
                chat.Messages.Clear(); chat.Artifacts.Clear(); chat.ActiveTaskListArtifactId = null;
                store.Commit(chat, ConversationCommit.RewriteHistory(previous, chat.Messages));
                journal.Rebuild();
                AssertEqual(0, store.Load(chat.Id).Artifacts.Count, "clear removes visible membership");
                AssertTrue(store.ListHeaders().Single().CasBlobCount >= usage, "clear retains task CAS roots");
                if (!office)
                {
                    var backup = Path.Combine(paths.Root, "task-snapshot-backup");
                    JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                    var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "task-snapshot-restore"), _ => StorageProtector.None);
                    AssertEqual(TaskListPublication.ReadBody(first, blobs), TaskListPublication.ReadBody(first, new ChatBlobStore(restored)), "restore retains historical task body after clear");
                }
            });
        }

        private static void JournalTaskUnknown()
        {
            WithJournalInput(false, (paths, journal, chat, scope) => {
                IConversationStore store = journal; var resources = new ResourceJournal(paths); var blobs = new ChatBlobStore(paths);
                var consumer = new WorkspaceTaskListObserver(paths, store, blobs, chat, resources, resources);
                var context = new ToolExecutionContext(new ToolCall("task-call", TaskListToolCatalog.SetToolId, "{}"),
                    new ToolPolicySnapshot(TaskListToolCatalog.SetToolId, "fixture", true), "run", "turn", "step", DateTime.UtcNow, true, 1);
                var attempt = consumer.Prepare(context, new Dictionary<string, object>());
                consumer.MarkDispatchMayHaveOccurred(attempt);
                var created = NewJournalTask(chat).Artifact;
                journal.Journal.Fault = point => { if (point == "after_flush") throw new IOException("lost task acknowledgement"); };
                RuntimeThrows<IOException>(() => store.Commit(chat, new ConversationCommit())); journal.Journal.Fault = null;
                consumer.ReleaseUnresolved(attempt);
                journal.Rebuild(); chat = store.Load(chat.Id);
                consumer = new WorkspaceTaskListObserver(paths, store, blobs, chat, resources, resources);
                consumer.ReconcileInterrupted();
                AssertEqual(HeadKnowledge.Unknown, resources.GetHead(TaskListPublication.Scope(chat), TaskListPublication.Identity(chat)).Knowledge, "saved body does not invent terminal effect proof");
                AssertTrue(!consumer.Inspect().Available, "uncertain current list cannot authorize another save");
                var officeObserver = new ResourceMutationAuthorityObserver(new ResourceAuthorityService(resources, resources, resources, blobs),
                    resources, chat, blobs, saved => store.Commit(saved, new ConversationCommit()));
                RuntimeThrows<ToolMutationPreparationException>(() => officeObserver.Prepare(context, new Dictionary<string, object>()));
                AssertTrue(!TaskListPublication.Project(chat, resources.Capture(TaskListPublication.Scope(chat)), resources, blobs).Available,
                    "Office and Workspace use the same unavailable current-state projection");
                RuntimeThrows<ToolMutationPreparationException>(() => consumer.Prepare(context, new Dictionary<string, object>()));
                AssertEqual(created.Id, chat.Artifacts.Single().Id, "restart never repeats the mutation");
                AssertEqual("Retain progress", TaskListService.Active(chat).Goal, "exact uncertain snapshot remains readable");
                var independent = new ChatMessage { Role = "user", Content = "Independent work" };
                chat.Messages.Add(independent); store.Commit(chat, ConversationCommit.UserInput(independent));
                AssertEqual(independent.Id, store.Load(chat.Id).Messages.Last().Id, "uncertainty does not block the chat");
                var backup = Path.Combine(paths.Root, "task-unknown-backup");
                JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "task-unknown-restore"), _ => StorageProtector.None);
                AssertEqual(HeadKnowledge.Unknown, new ResourceJournal(restored).GetHead(TaskListPublication.Scope(chat), TaskListPublication.Identity(chat)).Knowledge, "restore preserves unknown without replay");
            });
        }
    }
}
