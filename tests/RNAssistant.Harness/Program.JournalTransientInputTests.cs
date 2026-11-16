using System;
using System.Linq;
using RNAssistant.Application.Conversations;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void JournalTransientInputRead()
        {
            foreach (var office in new[] { true, false }) WithTempPaths(paths => {
                var scope = office ? "document:draft-input" : "workspace:draft-input";
                var router = new ConversationJournalRouter(paths, (host, key) => scope);
                IConversationStore store = router; IEventStore events = router;
                var draft = store.CreateTransient(office ? "Excel" : "Workspace", office ? "doc" : "draft-input",
                    "Document", "New chat");
                var initial = ConversationMetadata.From(draft);
                var inbox = new ConversationInboxService(router);
                AssertEqual(0, inbox.Snapshot(draft).Items.Count, "new draft exposes an empty inbox before its first commit");
                AssertEqual(0, inbox.ReadUndelivered(draft).Count, "read-only draft delivery lookup is empty");
                AssertEqual(0, new ConversationInputJournal(router).Read(draft).Count, "draft input validation accepts an empty journal");
                AssertEqual(0L, draft.Revision, "reading an inbox does not commit the draft");
                AssertTrue(!store.IsPersisted(draft) && store.ListHeaders(false).Count == 0,
                    "empty inbox reads do not create durable history");

                IEventStore exact = new ConversationJournal(paths, scope);
                var wrongScope = new ConversationMetadata(draft.Id, 0, draft.Host, draft.DocumentKey,
                    office ? null : "other", office ? "other" : null);
                RuntimeThrows<ChatConcurrencyException>(() => exact.Read(wrongScope, SessionEventReadMode.RequireCompleteInputJournal));
                var missingRevision = new ConversationMetadata(draft.Id, 1, draft.Host, draft.DocumentKey,
                    draft.WorkspaceId, draft.DocumentAuthorityId);
                RuntimeThrows<ChatConcurrencyException>(() => events.Read(missingRevision, SessionEventReadMode.RequireCompleteInputJournal));
                RuntimeThrows<ChatConcurrencyException>(() => events.Read(initial, SessionEventReadMode.RequireComplete));

                store.Commit(draft, new ConversationCommit());
                RuntimeThrows<ChatConcurrencyException>(() => events.Read(initial, SessionEventReadMode.RequireCompleteInputJournal));
                var foreign = (IEventStore)new ConversationJournal(paths, office ? "document:other" : "workspace:other");
                var reusedId = new ConversationMetadata(draft.Id, 0, draft.Host, office ? "foreign" : "other",
                    office ? null : "other", office ? "other" : null);
                RuntimeThrows<ChatConcurrencyException>(() => foreign.Read(reusedId, SessionEventReadMode.RequireCompleteInputJournal));
                inbox.Submit(draft, InboxInput("first-input", "first accepted input", InputDelivery.Queue));
                AssertEqual("first accepted input", inbox.ReadUndelivered(draft).Single().Text,
                    "the owner that observed a draft still reads its subsequently accepted input");
                AssertTrue(store.Delete(draft.Host, draft.DocumentKey, draft.Id), "remove persisted test chat");
                RuntimeThrows<ChatConcurrencyException>(() => events.Read(initial, SessionEventReadMode.RequireCompleteInputJournal));
            });
        }

        private static void JournalBoundedInputReads()
        {
            WithTempPaths(paths => {
                var journal = new ConversationJournal(paths, "workspace:bounded-inputs");
                IConversationStore store = journal;
                var session = store.CreateTransient("Workspace", "bounded-inputs", "Bounded inputs", "Bounded inputs");
                store.Commit(session, new ConversationCommit());
                var inbox = new ConversationInboxService(journal);
                var fingerprints = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
                for (var index = 0; index < 100; index++)
                {
                    var operation = "input-" + index;
                    var input = InboxInput(operation, "Pending " + index, InputDelivery.Queue);
                    fingerprints[operation] = input.RequestFingerprint;
                    inbox.Submit(session, input);
                }
                var allPendingRevision = session.Revision;
                for (var index = 0; index < 100; index += 2)
                    inbox.Update(session, new ConversationInputUpdate { InputId = "input-" + index,
                        ExpectedRevision = 1, Text = null }, "remove");
                var edited = inbox.Find(session, "input-1");
                for (var revision = 1; revision <= 12; revision++)
                {
                    var update = inbox.Update(session, new ConversationInputUpdate { InputId = "input-1",
                        ExpectedRevision = revision, Text = "Edited " + revision }, "edit-" + revision);
                    edited = update;
                }

                var port = (IConversationInputReadStore)journal;
                var reads = journal.Journal.ReadRecords;
                var bodies = journal.ChatBodyReads;
                var receipt = port.ReadInputReceipt(ConversationMetadata.From(session), "input-74", session.Revision);
                AssertEqual(2, receipt.Count, "exact receipt returns only the accepted and current versions");
                AssertEqual(1L, receipt[0].Change.Input.Revision, "receipt preserves the original accepted revision");
                AssertEqual(ConversationInputStatus.Removed, receipt[1].Change.Input.Status,
                    "receipt observes the current terminal transition");
                var receiptRecords = journal.Journal.ReadRecords - reads;
                var receiptBodies = journal.ChatBodyReads - bodies;
                AssertEqual(0L, receiptBodies, "receipt metadata does not hydrate historical input text");
                AssertTrue(receiptRecords <= 8,
                    "receipt reads exact indexed transitions instead of all input history");

                reads = journal.Journal.ReadRecords; bodies = journal.ChatBodyReads;
                var current = port.ReadCurrentInputs(ConversationMetadata.From(session), session.Revision)
                    .Where(item => item.Change.Input != null).ToArray();
                AssertEqual(50, current.Length, "current projection contains only undelivered inputs");
                AssertTrue(current.All(item => item.Change.Input.Status == ConversationInputStatus.Pending),
                    "removed inputs stay outside the pending projection");
                var pendingBodies = journal.ChatBodyReads - bodies;
                var pendingRecords = journal.Journal.ReadRecords - reads;
                AssertEqual(50L, pendingBodies, "pending projection hydrates one body per current input");
                AssertTrue(pendingRecords <= 300,
                    "pending reads stay proportional to the queue rather than transition history");
                Console.WriteLine("journal-input-reads H=100: receiptRecords={0}, receiptBodies={1}, pendingRecords={2}, pendingBodies={3}",
                    receiptRecords, receiptBodies, pendingRecords, pendingBodies);

                var stale = port.ReadCurrentInputs(ConversationMetadata.From(session), allPendingRevision)
                    .Where(item => item.Change.Input != null).ToArray();
                AssertEqual(100, stale.Length, "a stale revision reads its exact pending prefix");
                AssertEqual(1L, stale.Single(item => item.Change.Input.Id == "input-0").Change.Input.Revision,
                    "stale projection does not expose later removal");

                journal.Rebuild();
                var reopened = (IConversationInputReadStore)new ConversationJournal(paths, "workspace:bounded-inputs");
                var restored = reopened.ReadInputReceipt(ConversationMetadata.From(session), "input-1", session.Revision);
                AssertEqual(13L, restored[1].Change.Input.Revision,
                    "rebuild preserves the exact current input revision");
                AssertEqual("Edited 12", reopened.ReadCurrentInputs(ConversationMetadata.From(session), session.Revision)
                    .Single(item => item.Change.Input?.Id == "input-1").Change.Input.Text,
                    "bounded pending read hydrates the current body after rebuild");
            });
        }
    }
}
