using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Application.Conversations;
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
        private static void JournalDeleteConsumers()
        {
            foreach (var office in new[] { true, false }) WithJournalInput(office, (paths, journal, source, scope) => {
                IConversationStore store = journal; IEventStore events = journal;
                var member = store.CreateTransient(source.Host, source.DocumentKey, source.DocumentTitle, "Survivor");
                store.Commit(member, new ConversationCommit());
                var resources = new ResourceJournal(paths); var groups = NativeGroups(paths, journal, source, resources);
                var group = groups.Change(source, new ChatGroupChange { Action = ChatGroupAction.Create, Name = "Retained group" }, "group-create").Group;
                groups.Change(source, new ChatGroupChange { Action = ChatGroupAction.Add, GroupId = group.Id,
                    ExpectedRevision = group.Revision, Members = new[] { source.Id, member.Id } }, "group-add");
                var head = member.StorageHeadHash;
                store.SaveActiveSessionId(source.Host, source.DocumentKey, source.Id);
                AssertTrue(!store.Delete(source.Host, source.DocumentKey + "-foreign", source.Id), "foreign scope cannot delete a chat");
                if (office)
                {
                    var sessions = new ChatSessionService(FakeOfficeAdapter.ForHost(source.Host), store);
                    AssertEqual(member.Id, sessions.DeleteAndSelectNext(source).Id, "Office selects a surviving chat");
                    AssertEqual(member.Id, store.LoadActiveSessionId(source.Host, source.DocumentKey), "Office selection is durable");
                }
                else
                {
                    var workspaces = new WorkspaceStore(paths); var workspace = workspaces.OpenConnected(source.WorkspaceId);
                    AssertTrue(workspaces.DeleteSession(store, workspace, source.Id), "Workspace consumer deletes its exact chat");
                    AssertTrue(store.LoadActiveSessionId(source.Host, source.DocumentKey) == null, "deleted selection is cleared atomically");
                }
                AssertTrue(store.Load(source.Id) == null && store.ReadMetadata(source.Id) == null && !store.IsPersisted(source), "deleted chat is not a live Store entity");
                AssertTrue(store.ReadMessageWindow(source.Id, 0, 20) == null, "deleted history cannot masquerade as an active page");
                AssertEqual(member.Id, store.ListHeaders(false).Single().Id, "catalog excludes the tombstone");
                AssertTrue(events.FindAgent(ConversationMetadata.From(member), AgentChatAddress.IdFor(source.Id)) == null, "deleted alias cannot receive new peer delivery");
                AssertEqual(member.Id, groups.Read(member).Single().Members.Single(), "creator deletion keeps the group and filters only dead membership");
                AssertEqual(head, store.Load(member.Id).StorageHeadHash, "deletion does not rewrite another chat");
                RuntimeThrows<InvalidOperationException>(() => store.Commit(source, new ConversationCommit()));
                RuntimeThrows<InvalidDataException>(() => journal.CreateChat("recreate", source.Id, "Resurrection", ConversationRunPolicy.For(ChatModes.Agent)));
                AssertTrue(!store.Delete(source.Host, source.DocumentKey, source.Id), "repeat deletion is a read-only absence");
                journal.Rebuild();
                AssertTrue(store.Load(source.Id) == null, "rebuild cannot resurrect a deleted chat");
                AssertEqual(member.Id, groups.Read(member).Single().Members.Single(), "rebuild retains group filtering");
                if (!office)
                {
                    var backup = Path.Combine(paths.Root, "delete-backup"); JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                    var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "delete-restore"), _ => StorageProtector.None);
                    var copy = new ConversationJournal(restored, scope); IConversationStore restoredStore = copy;
                    AssertTrue(restoredStore.Load(source.Id) == null && restoredStore.Load(member.Id) != null, "clean restore preserves deletion and survivors");
                    AssertEqual(member.Id, NativeGroups(restored, copy, member, new ResourceJournal(restored)).Read(member).Single().Members.Single(),
                        "restore keeps resource-owned groups without resurrecting their creator");
                }
            });
        }

        private static void JournalDeleteReceipts()
        {
            foreach (var fault in new[] { "before_append", "after_flush", "before_index", "before_delete_readback" })
                WithJournalInput(false, (paths, journal, source, scope) => {
                    IConversationStore store = journal;
                    var message = new ChatMessage { Role = "user", Content = "Retain these canonical bytes." };
                    source.Messages.Add(message); store.Commit(source, ConversationCommit.UserInput(message));
                    store.SaveActiveSessionId(source.Host, source.DocumentKey, source.Id);
                    var bodies = journal.ChatBodyReads; var written = journal.Journal.WrittenBytes; var hit = false;
                    Action<string> fail = point => {
                        if (point != fault || hit) return;
                        hit = true; throw new IOException("fixture: " + fault);
                    };
                    journal.Journal.Fault = fail; journal.Fault = fail;
                    if (fault == "before_append" || fault == "before_delete_readback")
                        RuntimeThrows<IOException>(() => store.Delete(source.Host, source.DocumentKey, source.Id));
                    else AssertTrue(store.Delete(source.Host, source.DocumentKey, source.Id), "exact receipt proves deletion despite lost acknowledgement/index update");
                    journal.Journal.Fault = null; journal.Fault = null;
                    AssertTrue(hit, "requested deletion fault was reached");
                    AssertEqual(bodies, journal.ChatBodyReads, "delete hydrates no history body");
                    var accepted = fault != "before_append";
                    AssertEqual(accepted, journal.Journal.WrittenBytes > written, "only an accepted deletion appends a frame");
                    journal = new ConversationJournal(paths, scope); store = journal; journal.Rebuild();
                    AssertEqual(!accepted, store.Load(source.Id) != null, "restart follows canonical acceptance, not a failed caller acknowledgement");
                    if (accepted)
                    {
                        AssertTrue(store.LoadActiveSessionId(source.Host, source.DocumentKey) == null, "tombstone and selection deletion rebuild together");
                        AssertTrue(!store.Delete(source.Host, source.DocumentKey, source.Id), "lost read-back does not redispatch deletion");
                        AssertEqual(0L, journal.Journal.WrittenBytes, "restart/retry appends no journal bytes");
                    }
                });
        }

        private static void JournalDeleteGuards()
        {
            WithJournalInput(false, (paths, journal, source, scope) => {
                IConversationStore store = journal;
                var initial = CheckpointState("delete-run", RunLifecycle.Running, 0, 0);
                source.LastRun = new ChatRunRecord { RunId = initial.Summary.RunId, TurnId = initial.Summary.TurnId,
                    StartedUtc = DateTime.UtcNow, KernelState = initial, ConversationPolicy = ConversationRunPolicy.For(ChatModes.Agent) };
                new ConversationRunCommit(source, new AgentRunEvent(AgentRunEventKind.Started, initial.Summary, limits: initial.Limits),
                    new ChatMessage[0]).Persist(store.CommitRun, source);
                var written = journal.Journal.WrittenBytes;
                RuntimeThrows<InvalidOperationException>(() => store.Delete(source.Host, source.DocumentKey, source.Id));
                AssertEqual(written, journal.Journal.WrittenBytes, "active run rejection appends no deletion");
                source = RecoverJournalRun(store, source, false);
                AssertTrue(store.Delete(source.Host, source.DocumentKey, source.Id), "existing interrupted-run recovery permits deletion after terminal");
                journal.Rebuild();
                AssertTrue(store.ReadMetadata(source.Id) == null, "retained terminal checkpoint cannot resurrect the deleted chat");
            });
            WithJournalInput(false, (paths, journal, source, scope) => {
                IConversationStore store = journal;
                var member = store.CreateTransient(source.Host, source.DocumentKey, source.DocumentTitle, "Racing member");
                store.Commit(member, new ConversationCommit());
                var resources = new ResourceJournal(paths); var groups = NativeGroups(paths, journal, source, resources);
                var group = groups.Change(source, new ChatGroupChange { Action = ChatGroupAction.Create, Name = "Race" }, "create-race").Group;
                using (var entered = new ManualResetEventSlim())
                using (var release = new ManualResetEventSlim())
                {
                    var adding = Task.Run(() => groups.Change(source, new ChatGroupChange { Action = ChatGroupAction.Add,
                        GroupId = group.Id, ExpectedRevision = group.Revision, Members = new[] { member.Id } }, "race-add", () => {
                            entered.Set(); AssertTrue(release.Wait(5000), "fixture releases group dispatch");
                        }));
                    try
                    {
                        AssertTrue(entered.Wait(5000), "group mutation holds its scope lease");
                        RuntimeThrows<IOException>(() => store.Delete(member.Host, member.DocumentKey, member.Id));
                    }
                    finally { release.Set(); }
                    AssertTrue(adding.Wait(5000), "group mutation finishes");
                    group = adding.Result.Group;
                }
                AssertTrue(store.Delete(member.Host, member.DocumentKey, member.Id), "deletion follows completed membership publication");
                AssertEqual(0, groups.Read(source).Single().Members.Count, "serialized add/delete cannot retain a live deleted member");
                RuntimeThrows<ChatConcurrencyException>(() => groups.Change(source, new ChatGroupChange { Action = ChatGroupAction.Add,
                    GroupId = group.Id, ExpectedRevision = group.Revision, Members = new[] { member.Id } }, "late-add"));
            });
        }
    }
}
