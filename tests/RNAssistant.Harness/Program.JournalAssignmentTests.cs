using System;
using System.IO;
using System.Linq;
using RNAssistant.Application.Conversations;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void JournalAssignmentPages()
        {
            foreach (var office in new[] { true, false }) WithJournalInput(office, (paths, journal, recipient, scope) => {
                IConversationStore store = journal; IEventStore events = journal;
                var summary = new string('x', 159) + "…";
                var text = "  " + new string('x', 159) + "😀 not in the summary";
                ChatSession sender = null;
                for (var index = 0; index < 61; index++)
                {
                    var source = store.CreateTransient(recipient.Host, recipient.DocumentKey, recipient.DocumentTitle, "Source " + index);
                    store.Commit(source, new ConversationCommit());
                    var id = "page-" + index;
                    AcceptJournalPeerCall(store, source, id, id);
                    journal.SendAgent(id, source.Id, id, id, AgentChatAddress.IdFor(recipient.Id), source.Id, id, 2, text,
                        assignment: index < 51, recipientRuntimeKey: recipient.WorkspaceId ?? recipient.DocumentKey);
                    if (index == 0) sender = store.Load(source.Id);
                }
                recipient = store.Load(recipient.Id);
                var inbox = new ConversationInboxService(journal);
                var service = new AgentCommunicationService(() => 2);
                service.Register(new AgentConversationScope { Key = scope,
                    Contains = chat => chat.Host == recipient.Host && chat.DocumentKey == recipient.DocumentKey,
                    ReadCatalog = () => { throw new Exception("Assignment listing must not enumerate chat catalog."); } });
                var revision = recipient.Revision; var reads = journal.Journal.ReadRecords; var bodies = journal.ChatBodyReads;
                var first = service.Assignments(inbox, recipient, 2, "incoming", null);
                var recordReads = journal.Journal.ReadRecords - reads;
                AssertTrue(first.Items.Count == 50 && first.Truncated && first.NextRequest != null, "native page stops at 50 assignments");
                AssertEqual(50L, journal.ChatBodyReads - bodies, "cold listing hydrates only this page's request bodies, never the entire Inbox");
                AssertTrue(recordReads <= 110, "canonical assignment reads stay bounded by the selected page");
                Console.WriteLine("journal-assignments " + (office ? "Office" : "Workspace") + ": page records=" + recordReads + ", request bodies=50, retained inputs=61");
                var next = service.Assignments(inbox, recipient, 2, "incoming", first.NextRequest);
                AssertTrue(!next.Truncated && next.NextRequest == null && next.Items.Count == 1, "native tail does not include ordinary peer messages");
                AssertEqual(51, first.Items.Concat(next.Items).Select(item => item.RequestId).Distinct().Count(), "cursor neither skips nor duplicates assignments");
                AssertTrue(first.Items.All(item => item.Summary == summary && item.State == AgentAssignmentState.Open &&
                    item.InputStatus == ConversationInputStatus.Pending), "summary preserves trim/surrogate bounds and pending intake");
                AssertEqual(0, service.Assignments(inbox, recipient, 2, "outgoing", null).Items.Count, "opposite direction is empty");
                var outgoing = service.Assignments(inbox, sender, 2, "outgoing", null).Items.Single();
                AssertTrue(outgoing.Summary == summary && outgoing.InputStatus == null && outgoing.AgentId == AgentChatAddress.IdFor(recipient.Id),
                    "outgoing rows expose no recipient input status");
                AssertEqual(revision, store.ReadMetadata(recipient.Id).Revision, "listing writes no chat or input transition");
                var stale = ConversationMetadata.From(recipient);
                store.SetTitle(recipient, "Renamed recipient");
                RuntimeThrows<ChatConcurrencyException>(() => events.ReadAgentAssignments(stale, true, null));
                RuntimeThrows<ChatConcurrencyException>(() => events.ReadAgentAssignments(new ConversationMetadata(recipient.Id, recipient.Revision,
                    recipient.Host, recipient.DocumentKey + "-foreign", recipient.WorkspaceId, recipient.DocumentAuthorityId), true, null));
                journal.Rebuild();
                var rebuilt = events.ReadAgentAssignments(ConversationMetadata.From(store.Load(recipient.Id)), true, null);
                AssertEqual(first.NextRequest, rebuilt.NextRequest, "rebuild recreates assignment flags and keyset order");
                AssertTrue(first.Items.Select(item => item.RequestId).SequenceEqual(rebuilt.Items.Select(item => item.RequestId)), "rebuild preserves exact native intake");
                if (!office)
                {
                    var backup = Path.Combine(paths.Root, "assignments-backup"); JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                    var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "assignments-restore"), _ => StorageProtector.None);
                    var copy = new ConversationJournal(restored, scope);
                    var page = ((IEventStore)copy).ReadAgentAssignments(ConversationMetadata.From(((IConversationStore)copy).Load(recipient.Id)), true, null);
                    AssertEqual(first.NextRequest, page.NextRequest, "restore retains the same assignment page without execution");
                }
            });
        }
    }
}
