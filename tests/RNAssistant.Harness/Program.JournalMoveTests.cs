using System;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void JournalMoveHeaderRecovery()
        {
            foreach (var action in new[] { "single", "document", "delete" }) WithJournalInput(true, (paths, journal, source, scope) => {
                var rotating = new StorageJournal(Path.GetDirectoryName(journal.Journal.SegmentPath(1)), scope, rotationRecords: 1);
                using (rotating.AcquireAppendLock())
                {
                    var cursor = rotating.CatchUp(null, (record, position) => { }, null, CancellationToken.None);
                    rotating.Fault = point => { if (point == "before_append") throw new IOException("header accepted, fact not written"); };
                    RuntimeThrows<IOException>(() => rotating.Append(cursor, "unwritten", "unwritten", "chat.selected", new JObject()));
                }
                AssertTrue(File.Exists(journal.Journal.SegmentPath(2)), "fixture retains an empty successor header");
                IConversationStore store = journal;
                if (action == "single") store.Move(source, source.Host, "saved", "Saved");
                else if (action == "document") store.MoveDocument(source.Host, source.DocumentKey, source.Host, "saved", "Saved", null);
                else AssertTrue(store.DeleteDocument(source.Host, source.DocumentKey), "deletion accepts the post-header boundary");
                journal.Rebuild();
                AssertEqual(action == "delete" ? null : "saved", store.ReadMetadata(source.Id)?.DocumentKey,
                    "replay resolves the canonical successor-header boundary for " + action);
            });
        }

        private static void JournalMoveConsumers()
        {
            foreach (var office in new[] { true, false }) WithJournalInput(office, (paths, journal, source, scope) => {
                IConversationStore store = journal;
                AppendFeedMessage(store, source, "History survives address changes.");
                var start = new SessionEventPosition(source.Id, source.Revision, source.StorageHeadHash);
                var peer = store.CreateTransient(source.Host, source.DocumentKey, source.DocumentTitle, "Peer");
                store.Commit(peer, new ConversationCommit());
                var oldKey = source.DocumentKey;
                var key = office ? "saved-again" : oldKey;
                ChatSession alias = null;
                if (office)
                {
                    var adapter = new FakeOfficeAdapter();
                    alias = store.CreateTransient(source.Host, "old-alias", source.DocumentTitle, "Alias");
                    alias.DocumentPath = adapter.DocumentPathValue; store.Commit(alias, new ConversationCommit());
                    store.SaveActiveSessionId(alias.Host, alias.DocumentKey, alias.Id);
                    var sessions = new ChatSessionService(adapter, store);
                    sessions.LoadSession(source.Id);
                    AssertEqual(oldKey, store.ReadMetadata(alias.Id).DocumentKey, "Office alias reconciliation uses native single-chat Move");
                    adapter.DocumentKeyValue = "saved"; adapter.DocumentPathValue = "C:\\Demo\\Saved.xlsx";
                    var draft = sessions.CreateChat("After Save As");
                    AssertEqual("saved", draft.DocumentKey, "Office fresh draft follows Save As");
                    AssertEqual(draft.Id, sessions.ActiveTransientSessionId, "relocation does not select an old chat over a fresh draft");
                    AssertEqual("saved", store.ReadMetadata(peer.Id).DocumentKey, "Office Save As moves every chat in the document");
                    oldKey = "saved";
                }
                store.SaveActiveSessionId(source.Host, oldKey, peer.Id);
                var reads = journal.ChatBodyReads; var bytes = journal.Journal.WrittenBytes;
                store.MoveDocument(source.Host, oldKey, source.Host, key, "Relocated", "relocated-path");
                AssertEqual(reads, journal.ChatBodyReads, "document move hydrates no message/context bodies");
                AssertTrue(journal.Journal.WrittenBytes - bytes < 4096, "document move is one compact frame without a target array");
                var loaded = store.Load(source.Id); var movedPeer = store.Load(peer.Id);
                AssertEqual(key, loaded.DocumentKey, "Store returns the current document address");
                AssertEqual(key, loaded.Context.DocumentKey, "loaded context derives its current binding");
                AssertEqual(source.DocumentAuthorityId, loaded.DocumentAuthorityId, "stable document authority is unchanged");
                AssertEqual(source.WorkspaceId, loaded.WorkspaceId, "workspace authority is unchanged");
                AssertEqual("History survives address changes.", loaded.Messages.Single().Content, "canonical message bodies survive");
                AssertEqual(peer.Id, store.LoadActiveSessionId(source.Host, key), "whole-document relocation preserves selection");
                if (office)
                {
                    AssertTrue(store.Load(source.Host, oldKey, source.Id) == null, "old address no longer loads the moved chat");
                    AssertTrue(store.Load(alias.Id).PreviousDocumentKeys.SequenceEqual(new[] { "old-alias", "doc", "saved" }),
                        "repeated single/batch moves preserve each chat's exact lineage");
                    store.SaveActiveSessionId(source.Host, oldKey, string.Empty);
                    AssertEqual(peer.Id, store.LoadActiveSessionId(source.Host, key), "clearing an old alias cannot clear the new selection");
                }
                else
                {
                    var workspaces = new WorkspaceStore(paths); var workspace = workspaces.OpenConnected(source.WorkspaceId);
                    AssertEqual("Relocated", workspaces.LoadSession(store, workspace, source.Id).DocumentTitle, "Workspace reads rebound metadata through its existing consumer");
                    AssertEqual(2, workspaces.ListSessions(store, workspace).Length, "Workspace catalog retains both chats");
                }
                AssertTrue(((IEventStore)journal).FindAgent(ConversationMetadata.From(loaded), AgentChatAddress.IdFor(peer.Id)) != null,
                    "alias index resolves a moved card");
                var feed = ((IEventStore)journal).OpenReader(ConversationMetadata.From(loaded)).Read(start, null, 100, CancellationToken.None);
                AssertEqual(loaded.Revision, feed.Next.Sequence, "bounded feed reaches the relocation head");
                AssertTrue(((IEventStore)journal).Read(ConversationMetadata.From(loaded), SessionEventReadMode.Validated).Count >= 2,
                    "full event consumer resolves shared move locators per chat");
                store.Move(movedPeer, movedPeer.Host, key, "Single relocation");
                store.SetTitle(movedPeer, "After relocation");
                journal.Rebuild();
                AssertEqual("After relocation", store.Load(peer.Id).Title, "a normal commit after relocation survives rebuild");
                var backup = Path.Combine(paths.Root, "move-backup"); JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "move-restore"), _ => StorageProtector.None);
                IConversationStore copy = new ConversationJournal(restored, scope);
                AssertEqual(key, copy.Load(source.Id).DocumentKey, "clean restore retains the relocation");
                AssertEqual(loaded.Messages.Single().Content, copy.Load(source.Id).Messages.Single().Content, "restore retains original history");
                AssertTrue(copy.DeleteDocument(source.Host, key), "document deletion validates relocated canonical metadata");
                AssertTrue(copy.Load(source.Id) == null, "relocated chat remains logically deleted");
                store.SaveActiveSessionId(source.Host, key, string.Empty); journal.Rebuild();
                AssertTrue(store.LoadActiveSessionId(source.Host, key) == null, "explicit selection clearing survives replay");
            });
        }

        private static void JournalMoveReceipts()
        {
            foreach (var batch in new[] { false, true })
            foreach (var fault in new[] { "before_append", "after_flush", "before_index", "after_move_row", "before_move_readback" })
                WithJournalInput(true, (paths, journal, source, scope) => {
                    IConversationStore store = journal;
                    var peer = store.CreateTransient(source.Host, source.DocumentKey, source.DocumentTitle, "Peer");
                    store.Commit(peer, new ConversationCommit());
                    var oldKey = source.DocumentKey; var oldRevision = source.Revision; var hit = false;
                    Action<string> fail = point => { if (point == fault && !hit) { hit = true; throw new IOException("fixture: " + fault); } };
                    journal.Journal.Fault = fail; journal.Fault = fail;
                    Action move = () => { if (batch) store.MoveDocument(source.Host, oldKey, source.Host, "saved", "Saved", null);
                        else store.Move(source, source.Host, "saved", "Saved"); };
                    var failed = fault == "before_append" || fault == "before_move_readback";
                    if (failed) RuntimeThrows<IOException>(move); else move();
                    journal.Journal.Fault = null; journal.Fault = null;
                    AssertTrue(hit, "requested relocation fault was reached");
                    if (failed) { AssertEqual(oldKey, source.DocumentKey, "failed acknowledgement does not mutate caller binding");
                        AssertEqual(oldRevision, source.Revision, "failed acknowledgement does not advance caller revision"); }
                    var accepted = fault != "before_append";
                    journal = new ConversationJournal(paths, scope); store = journal; journal.Rebuild();
                    AssertEqual(accepted ? "saved" : oldKey, store.Load(source.Id).DocumentKey, "restart follows canonical acceptance");
                    AssertEqual(accepted && batch ? "saved" : oldKey, store.Load(peer.Id).DocumentKey, "index failure cannot partially move a batch");
                    if (accepted && batch) store.MoveDocument(source.Host, oldKey, source.Host, "saved", "Saved", null);
                    else if (accepted && failed) RuntimeThrows<ChatConcurrencyException>(() => store.Move(source, source.Host, "saved", "Saved"));
                    AssertEqual(0L, journal.Journal.WrittenBytes, "restart/read-back does not replay the move");
                });
        }

        private static void JournalMoveGuards()
        {
            WithJournalInput(true, (paths, journal, source, scope) => {
                IConversationStore store = journal;
                var busy = store.CreateTransient(source.Host, source.DocumentKey, source.DocumentTitle, "Busy");
                store.Commit(busy, new ConversationCommit());
                var state = CheckpointState("move-run", RunLifecycle.Running, 0, 0);
                busy.LastRun = new ChatRunRecord { RunId = state.Summary.RunId, TurnId = state.Summary.TurnId, StartedUtc = DateTime.UtcNow,
                    KernelState = state, ConversationPolicy = ConversationRunPolicy.For(ChatModes.Agent) };
                new ConversationRunCommit(busy, new AgentRunEvent(AgentRunEventKind.Started, state.Summary, limits: state.Limits),
                    new ChatMessage[0]).Persist(store.CommitRun, busy);
                var bytes = journal.Journal.WrittenBytes;
                RuntimeThrows<InvalidOperationException>(() => store.MoveDocument(source.Host, source.DocumentKey, source.Host, "saved", "Saved", null));
                RuntimeThrows<InvalidOperationException>(() => store.Move(busy, busy.Host, "saved", "Saved"));
                AssertEqual(bytes, journal.Journal.WrittenBytes, "active run vetoes the whole move before append");
                using (new ResourceJournal(paths).AcquireScope(DocumentArtifactStore.PlanScope(source)))
                    RuntimeThrows<IOException>(() => store.Move(source, source.Host, "saved", "Saved"));
                var stale = store.Load(source.Id); store.SetTitle(source, "Changed");
                RuntimeThrows<ChatConcurrencyException>(() => store.Move(stale, stale.Host, "saved", "Saved"));
                AssertEqual(stale.DocumentKey, store.ReadMetadata(source.Id).DocumentKey, "stale move cannot change the current binding");
            });
            WithJournalInput(false, (paths, journal, source, scope) => {
                IConversationStore store = journal; var bytes = journal.Journal.WrittenBytes;
                RuntimeThrows<InvalidDataException>(() => store.Move(source, "Workspace", "different-workspace", "Foreign"));
                AssertEqual(bytes, journal.Journal.WrittenBytes, "relocation cannot transfer workspace authority");
                var draft = store.CreateTransient(source.Host, source.DocumentKey, source.DocumentTitle, "Draft");
                store.Move(draft, source.Host, source.DocumentKey, "New label");
                AssertEqual(bytes, journal.Journal.WrittenBytes, "transient move remains in memory");
                store.Commit(draft, new ConversationCommit()); store.Delete(draft.Host, draft.DocumentKey, draft.Id);
                draft.Revision = 0;
                RuntimeThrows<ChatConcurrencyException>(() => store.Move(draft, draft.Host, draft.DocumentKey, "Do not resurrect"));
            });
            WithJournalInput(true, (paths, journal, source, scope) => {
                IConversationStore store = journal; ChatSession late = null;
                journal.Fault = point => {
                    if (point != "before_move_readback") return;
                    journal.Fault = null; IConversationStore other = new ConversationJournal(paths, scope);
                    late = other.CreateTransient(source.Host, source.DocumentKey, source.DocumentTitle, "After boundary");
                    other.Commit(late, new ConversationCommit()); other.SaveActiveSessionId(late.Host, late.DocumentKey, late.Id);
                    var moved = other.Load(source.Id); other.SetTitle(moved, "Newer metadata");
                };
                store.MoveDocument(source.Host, source.DocumentKey, source.Host, "saved", "Saved", null);
                AssertEqual("Newer metadata", store.Load(source.Id).Title, "read-back accepts the original receipt without rewinding newer metadata");
                AssertEqual(source.DocumentKey, store.ReadMetadata(late.Id).DocumentKey, "new chats stay outside the accepted relocation boundary");
                AssertEqual(late.Id, store.LoadActiveSessionId(source.Host, source.DocumentKey), "later selection survives read-back");
                journal.Rebuild();
                AssertEqual("saved", store.ReadMetadata(source.Id).DocumentKey, "rebuild preserves only the accepted set");
                AssertEqual(source.DocumentKey, store.ReadMetadata(late.Id).DocumentKey, "rebuild preserves later source-address creation");
            });
        }
    }
}
