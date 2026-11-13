using System;
using System.IO;
using System.Linq;
using RNAssistant.Core.Agent;
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
        private static void JournalDocumentDeleteConsumers()
        {
            foreach (var office in new[] { true, false }) WithJournalInput(office, (paths, journal, source, scope) => {
                IConversationStore store = journal;
                var peer = store.CreateTransient(source.Host, source.DocumentKey, source.DocumentTitle, "Peer");
                store.Commit(peer, new ConversationCommit());
                ChatSession foreign = null;
                if (office)
                {
                    foreign = store.CreateTransient(source.Host, source.DocumentKey + "-other", "Other", "Untouched");
                    store.Commit(foreign, new ConversationCommit());
                }
                var resources = new ResourceJournal(paths); var groups = NativeGroups(paths, journal, source, resources);
                var group = groups.Change(source, new ChatGroupChange { Action = ChatGroupAction.Create, Name = "Surviving group" }, "create-group").Group;
                groups.Change(source, new ChatGroupChange { Action = ChatGroupAction.Add, GroupId = group.Id,
                    ExpectedRevision = group.Revision, Members = new[] { source.Id, peer.Id } }, "members");
                store.SaveActiveSessionId(source.Host, source.DocumentKey, peer.Id);
                var bodies = journal.ChatBodyReads; var bytes = journal.Journal.WrittenBytes; var resourceBytes = resources.Journal.WrittenBytes;
                AssertTrue(!store.DeleteDocument(source.Host, source.DocumentKey + "-missing"), "foreign document selection changes nothing");
                AssertTrue(store.DeleteDocument(source.Host, source.DocumentKey), "document deletion accepts the exact live set");
                AssertEqual(bodies, journal.ChatBodyReads, "document deletion hydrates no history bodies");
                AssertTrue(journal.Journal.WrittenBytes - bytes < 4096, "batch deletion fits one compact frame");
                AssertEqual(resourceBytes, resources.Journal.WrittenBytes, "group resources are not rewritten by chat deletion");
                AssertTrue(store.Load(source.Id) == null && store.Load(peer.Id) == null, "all target chats disappear together");
                AssertTrue(store.LoadActiveSessionId(source.Host, source.DocumentKey) == null, "batch clears its active selection");
                AssertTrue(!store.DeleteDocument(source.Host, source.DocumentKey), "repeated empty deletion is read-only");
                if (foreign != null) AssertEqual(foreign.StorageHeadHash, store.Load(foreign.Id).StorageHeadHash, "another address in the same journal is preserved");

                ChatSession fresh;
                if (office)
                {
                    var sessions = new ChatSessionService(FakeOfficeAdapter.ForHost(source.Host), store);
                    sessions.Reset(); fresh = sessions.LoadSession(null);
                    AssertTrue(fresh.Id != source.Id && fresh.Id != peer.Id, "Office resumes with a new chat after document reset");
                    if (!store.IsPersisted(fresh)) store.Commit(fresh, new ConversationCommit());
                }
                else
                {
                    var workspaces = new WorkspaceStore(paths); var workspace = workspaces.OpenConnected(source.WorkspaceId);
                    AssertEqual(0, workspaces.ListSessions(store, workspace).Length, "Workspace catalog observes the batch deletion");
                    AssertTrue(workspaces.LoadSession(store, workspace, source.Id) == null, "Workspace cannot load an erased live chat");
                    fresh = workspaces.CreateSession(store, workspace, "After deletion");
                }
                AssertTrue(((IEventStore)journal).FindAgent(ConversationMetadata.From(fresh), AgentChatAddress.IdFor(source.Id)) == null,
                    "deleted aliases are not delivery destinations");
                AssertEqual(0, groups.Read(fresh).Single().Members.Count, "group survives without dead memberships");
                RuntimeThrows<InvalidOperationException>(() => store.Commit(source, new ConversationCommit()));
                journal.Rebuild();
                AssertTrue(store.Load(source.Id) == null && store.Load(peer.Id) == null && store.Load(fresh.Id) != null, "rebuild preserves cutoff and newer chat");
                if (!office)
                {
                    var backup = Path.Combine(paths.Root, "document-delete-backup"); JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                    var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "document-delete-restore"), _ => StorageProtector.None);
                    var copy = new ConversationJournal(restored, scope); IConversationStore restoredStore = copy;
                    AssertTrue(restoredStore.Load(source.Id) == null && restoredStore.Load(peer.Id) == null && restoredStore.Load(fresh.Id) != null,
                        "clean restore preserves the atomic deletion boundary");
                    AssertEqual(0, NativeGroups(restored, copy, fresh, new ResourceJournal(restored)).Read(fresh).Single().Members.Count,
                        "resource-owned group survives full chat-set deletion and restore");
                }
            });
        }

        private static void JournalDocumentDeleteReceipts()
        {
            foreach (var fault in new[] { "before_append", "after_flush", "before_index", "after_document_delete_row", "before_delete_readback" })
                WithJournalInput(false, (paths, journal, source, scope) => {
                    IConversationStore store = journal;
                    var peer = store.CreateTransient(source.Host, source.DocumentKey, source.DocumentTitle, "Peer");
                    store.Commit(peer, new ConversationCommit()); store.SaveActiveSessionId(source.Host, source.DocumentKey, source.Id);
                    var hit = false;
                    Action<string> fail = point => { if (point == fault && !hit) { hit = true; throw new IOException("fixture: " + fault); } };
                    journal.Journal.Fault = fail; journal.Fault = fail;
                    if (fault == "before_append" || fault == "before_delete_readback")
                        RuntimeThrows<IOException>(() => store.DeleteDocument(source.Host, source.DocumentKey));
                    else AssertTrue(store.DeleteDocument(source.Host, source.DocumentKey), "canonical receipt proves the complete batch");
                    journal.Journal.Fault = null; journal.Fault = null;
                    AssertTrue(hit, "requested document deletion fault was reached");
                    var accepted = fault != "before_append";
                    AssertEqual(!accepted, store.Load(source.Id) != null, "first target follows canonical acceptance");
                    AssertEqual(!accepted, store.Load(peer.Id) != null, "projection failure cannot leave a partial deletion");
                    journal = new ConversationJournal(paths, scope); store = journal; journal.Rebuild();
                    AssertEqual(!accepted, store.Load(source.Id) != null, "restart retains accepted batch only");
                    AssertEqual(!accepted, store.Load(peer.Id) != null, "rebuild restores all-or-none deletion");
                    if (accepted)
                    {
                        AssertTrue(!store.DeleteDocument(source.Host, source.DocumentKey), "retry does not append the accepted deletion again");
                        AssertEqual(0L, journal.Journal.WrittenBytes, "rebuild and retry write no canonical bytes");
                    }
                });
        }

        private static void JournalDocumentDeleteBoundary()
        {
            WithJournalInput(false, (paths, journal, source, scope) => {
                IConversationStore store = journal;
                var busy = store.CreateTransient(source.Host, source.DocumentKey, source.DocumentTitle, "Busy");
                store.Commit(busy, new ConversationCommit());
                // Put the veto after an idle candidate to detect partial batch writes.
                if (string.CompareOrdinal(source.Id, busy.Id) > 0) { var first = busy; busy = source; source = first; }
                var initial = CheckpointState("document-delete-run", RunLifecycle.Running, 0, 0);
                busy.LastRun = new ChatRunRecord { RunId = initial.Summary.RunId, TurnId = initial.Summary.TurnId,
                    StartedUtc = DateTime.UtcNow, KernelState = initial, ConversationPolicy = ConversationRunPolicy.For(ChatModes.Agent) };
                new ConversationRunCommit(busy, new AgentRunEvent(AgentRunEventKind.Started, initial.Summary, limits: initial.Limits),
                    new ChatMessage[0]).Persist(store.CommitRun, busy);
                var written = journal.Journal.WrittenBytes;
                RuntimeThrows<InvalidOperationException>(() => store.DeleteDocument(source.Host, source.DocumentKey));
                AssertEqual(written, journal.Journal.WrittenBytes, "one active run rejects the entire batch before append");
                AssertTrue(store.Load(source.Id) != null && store.Load(busy.Id) != null, "idle chats are not partially deleted on guard failure");
                busy = RecoverJournalRun(store, busy, false);
                var resources = new ResourceJournal(paths);
                using (resources.AcquireScope(DocumentArtifactStore.PlanScope(source)))
                    RuntimeThrows<IOException>(() => store.DeleteDocument(source.Host, source.DocumentKey));

                ChatSession late = null;
                journal.Fault = point => {
                    if (point != "before_delete_readback") return;
                    journal.Fault = null;
                    IConversationStore other = new ConversationJournal(paths, scope);
                    late = other.CreateTransient(source.Host, source.DocumentKey, source.DocumentTitle, "Created after boundary");
                    other.Commit(late, new ConversationCommit()); other.SaveActiveSessionId(late.Host, late.DocumentKey, late.Id);
                };
                AssertTrue(store.DeleteDocument(source.Host, source.DocumentKey), "receipt remains valid when another writer creates a later chat");
                AssertTrue(store.Load(source.Id) == null && store.Load(busy.Id) == null && store.Load(late.Id) != null, "only the accepted pre-boundary set is deleted");
                AssertEqual(late.Id, store.LoadActiveSessionId(source.Host, source.DocumentKey), "newer selection is not cleared by deletion read-back");
                journal.Rebuild();
                AssertTrue(store.ReadMetadata(source.Id) == null && store.ReadMetadata(late.Id) != null, "rebuild preserves concurrent creation ordering");
                AssertTrue(store.DeleteDocument(late.Host, late.DocumentKey), "a later explicit deletion owns a new boundary");
            });
        }
    }
}
