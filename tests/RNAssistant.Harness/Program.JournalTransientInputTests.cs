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
    }
}
