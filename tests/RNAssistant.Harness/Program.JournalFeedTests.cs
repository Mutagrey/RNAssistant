using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Application.Conversations;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Runtime;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void AppendFeedMessage(IConversationStore store, ChatSession chat, string text)
        {
            var message = new ChatMessage { Role = "user", Content = text };
            chat.Messages.Add(message); store.Commit(chat, ConversationCommit.UserInput(message));
        }

        private static void WorkspaceFeedUsesOnlyMetadata()
        {
            WithTempPaths(paths => {
                var workspaceStore = new WorkspaceStore(paths);
                var workspace = workspaceStore.Open(Path.Combine(paths.Root, "workspace"));
                var chats = new ChatStore(paths); var store = ConversationStore(chats);
                var chat = workspaceStore.CreateSession(store, workspace, "Metadata feed");
                var start = ConversationEventFeed.Cursor(chat);
                AppendFeedMessage(store, chat, new string('x', 12000));
                var other = workspaceStore.Open(Path.Combine(paths.Root, "other"));
                var unavailable = paths.ChatBlobDirectory + "-unavailable";
                Directory.Move(paths.ChatBlobDirectory, unavailable);
                try
                {
                    using (var service = new WorkspaceConversationService(paths))
                    {
                        var page = service.ReadEvents(workspace, chat.Id, start);
                        AssertTrue(!page.ResyncRequired && page.Events.Count == 1, "Workspace feed does not hydrate unavailable message CAS");
                        AssertEqual(ConversationEventFeed.Cursor(chat), page.NextCursor, "metadata route retains the saved cursor");
                        AssertEqual(ConversationResyncReason.HistoryUnavailable, service.ReadEvents(other, chat.Id, start).Reason,
                            "metadata lookup cannot cross Workspace ownership");
                        using (var cancel = new CancellationTokenSource())
                        {
                            var delivered = 0;
                            var follow = service.SubscribeEventsAsync(workspace, chat.Id, start, value => {
                                AssertTrue(!value.ResyncRequired && value.Events.Count == 1, "Workspace subscription also uses metadata");
                                delivered++; cancel.Cancel(); return Task.CompletedTask;
                            }, cancellationToken: cancel.Token);
                            RuntimeThrows<OperationCanceledException>(() => follow.GetAwaiter().GetResult());
                            AssertEqual(1, delivered, "subscription cancellation does not reload or reconcile the chat");
                        }
                    }
                }
                finally
                {
                    if (Directory.Exists(paths.ChatBlobDirectory)) Directory.Delete(paths.ChatBlobDirectory, true);
                    Directory.Move(unavailable, paths.ChatBlobDirectory);
                }
            });
        }

        private static void JournalFeedPagesAndRecovery()
        {
            WithJournalInput(false, (paths, journal, chat, scope) => {
                IConversationStore store = journal;
                var start = ConversationEventFeed.Cursor(chat); var stale = store.Load(chat.Id);
                var other = store.CreateTransient(chat.Host, chat.DocumentKey, chat.DocumentTitle, "Other");
                store.Commit(other, new ConversationCommit());
                var expected = new List<long>();
                for (var i = 0; i < 7; i++)
                {
                    AppendFeedMessage(store, chat, "private body " + i); expected.Add(chat.Revision);
                    AppendFeedMessage(store, other, "foreign body " + i);
                }
                var inbox = new ConversationInboxService(journal);
                inbox.Submit(chat, InboxInput("input", "private input", InputDelivery.Queue)); expected.Add(chat.Revision);
                var high = ConversationEventFeed.Cursor(chat);
                var feed = new ConversationEventFeed(journal);
                var cursor = start; var seen = new List<ConversationEventNotice>();
                var bodies = journal.ChatBodyReads;
                var first = feed.Read(ConversationMetadata.From(stale), cursor, high, 3);
                AssertEqual(3, first.Events.Count, "bounded first page");
                AssertTrue(!first.ResyncRequired && first.HasMore, "old snapshot can follow its pinned native prefix");
                seen.AddRange(first.Events); cursor = first.NextCursor;
                AppendFeedMessage(store, chat, "after high water");
                while (cursor != high)
                {
                    var page = feed.Read(ConversationMetadata.From(stale), cursor, high, 3);
                    AssertTrue(!page.ResyncRequired && page.Events.Count > 0 && page.Events.Count <= 3, "bounded continuation");
                    AssertEqual(high, page.HighWater, "later append cannot move the pinned high water");
                    seen.AddRange(page.Events); cursor = page.NextCursor;
                }
                AssertTrue(seen.Select(item => item.Sequence).SequenceEqual(expected), "interleaved chats cause neither missing nor foreign notices");
                AssertEqual(SessionEventTypes.ConversationInputChanged, seen.Last().Type, "Inbox notification uses the same page");
                AssertEqual(bodies, journal.ChatBodyReads, "pages do not hydrate historical bodies");
                var delta = feed.Read(ConversationMetadata.From(stale), cursor);
                AssertEqual(1, delta.Events.Count, "unpinned continuation observes exactly the later append");
                AssertEqual(0, feed.Read(ConversationMetadata.From(stale), delta.NextCursor).Events.Count, "idle polling invents no events");
                var reader = ((IEventStore)journal).OpenReader(ConversationMetadata.From(stale));
                var bad = reader.Read(new SessionEventPosition(chat.Id, chat.Revision, new string('0', 64)), null, 3, CancellationToken.None);
                AssertEqual(ConversationResyncReason.HistoryChanged, bad.Reason, "a hash mismatch is not an empty successful page");
                AssertEqual(ConversationResyncReason.DifferentSession, feed.Read(ConversationMetadata.From(stale), ConversationEventFeed.Cursor(other)).Reason, "foreign cursor is rejected");
                RuntimeThrows<ArgumentOutOfRangeException>(() => reader.Read(new SessionEventPosition(chat.Id, chat.Revision, chat.StorageHeadHash), null, 101, CancellationToken.None));
                journal.Rebuild();
                AssertEqual(delta.NextCursor, feed.Read(ConversationMetadata.From(stale), high).NextCursor, "rebuild preserves cursor identities");
                var backup = Path.Combine(paths.Root, "feed-backup");
                JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "feed-restore"), _ => StorageProtector.None);
                var copy = new ConversationJournal(restored, scope);
                var copied = ((IConversationStore)copy).Load(chat.Id);
                var restoredPage = new ConversationEventFeed(copy).Read(ConversationMetadata.From(copied), high);
                AssertTrue(!restoredPage.ResyncRequired && restoredPage.Events.Count == 1, "restore continues the original cursor without starting work");
                AssertEqual(delta.NextCursor, restoredPage.NextCursor, "restore preserves the accepted hash and sequence");
            });
        }

        private static void JournalFeedSubscriptionAndIntegrity()
        {
            WithJournalInput(false, (paths, journal, chat, scope) => {
                IConversationStore store = journal;
                var feed = new ConversationEventFeed(journal); var start = ConversationEventFeed.Cursor(chat);
                var entered = new TaskCompletionSource<bool>(); var release = new TaskCompletionSource<bool>(); var callbacks = 0;
                using (var cancel = new CancellationTokenSource())
                {
                    var subscription = feed.SubscribeAsync(ConversationMetadata.From(chat), start, async page => {
                        AssertTrue(!page.ResyncRequired, "healthy native subscription"); callbacks++;
                        if (callbacks == 1) { entered.SetResult(true); await release.Task; }
                        else { AssertEqual(1, page.Events.Count, "one native append notification"); cancel.Cancel(); }
                    }, 1, cancel.Token);
                    AssertTrue(entered.Task.Wait(2000), "initial empty acknowledgement");
                    var append = Task.Run(() => AppendFeedMessage(store, chat, "while subscriber waits"));
                    AssertTrue(append.Wait(2000), "subscriber callback holds no journal/admission/run lock");
                    AssertEqual(1, callbacks, "backpressure does not enqueue pages"); release.SetResult(true);
                    AssertTrue(Task.WhenAny(subscription, Task.Delay(3000)).GetAwaiter().GetResult() == subscription, "subscriber cancellation is bounded");
                    RuntimeThrows<OperationCanceledException>(() => subscription.GetAwaiter().GetResult());
                    AssertEqual(2, callbacks, "no callback after cancellation");
                }
                var beforeLostAck = ConversationEventFeed.Cursor(chat);
                journal.Journal.Fault = point => { if (point == "after_flush") throw new IOException("lost append acknowledgement"); };
                try { RuntimeThrows<IOException>(() => AppendFeedMessage(store, chat, "accepted despite lost acknowledgement")); }
                finally { journal.Journal.Fault = null; }
                var afterAck = feed.Read(ConversationMetadata.From(chat), beforeLostAck);
                AssertTrue(!afterAck.ResyncRequired && afterAck.Events.Count == 1, "lost acknowledgement remains visible once");
                var path = journal.Journal.SegmentPath(1); var original = File.ReadAllBytes(path);
                File.AppendAllText(path, "{\"complete-invalid\":true}\n"); var damaged = File.ReadAllBytes(path);
                AssertEqual(ConversationResyncReason.HistoryUnavailable, feed.Read(ConversationMetadata.From(chat), afterAck.NextCursor).Reason, "invalid complete suffix fails closed");
                AssertTrue(damaged.SequenceEqual(File.ReadAllBytes(path)), "invalid accepted-looking bytes are preserved");
                File.WriteAllBytes(path, original);
                AssertTrue(!feed.Read(ConversationMetadata.From(chat), afterAck.NextCursor).ResyncRequired, "explicit source repair restores cursor reads");
            });
        }

        private static void JournalFeedCostCurve()
        {
            foreach (var count in new[] { 100, 1000, 10000 }) WithTempPaths(paths => {
                var journal = new ConversationJournal(paths, "document:feed-cost"); IConversationStore store = journal;
                var chat = store.CreateTransient("Word", "feed-cost", "Cost", "Cost"); store.Commit(chat, new ConversationCommit());
                var start = ConversationEventFeed.Cursor(chat);
                for (var offset = 0; offset < count; offset += 25)
                {
                    var batch = Enumerable.Range(offset, 25).Select(index => new ChatMessage {
                        Id = index.ToString("D8"), Role = "user", Content = "Shared private body", CreatedUtc = chat.CreatedUtc }).ToArray();
                    chat.Messages.AddRange(batch); store.Commit(chat, new ConversationCommit(batch));
                }
                var feed = new ConversationEventFeed(journal);
                var records = journal.Journal.ReadRecords; var bytes = journal.Journal.ReadBytes; var bodies = journal.ChatBodyReads;
                var page = feed.Read(ConversationMetadata.From(chat), start, limit: 2);
                var reads = journal.Journal.ReadRecords - records; var readBytes = journal.Journal.ReadBytes - bytes;
                AssertTrue(!page.ResyncRequired && page.Events.Count == 2 && page.HasMore, "fixed page size across history lengths");
                AssertTrue(reads <= 7 && readBytes < 200000, "indexed page cost is independent of historical messages");
                var idleStart = journal.Journal.ReadRecords;
                AssertEqual(0, feed.Read(ConversationMetadata.From(chat), ConversationEventFeed.Cursor(chat)).Events.Count, "idle poll reads no historical page");
                var idle = journal.Journal.ReadRecords - idleStart;
                AssertTrue(idle <= 3, "idle poll has constant addressed validation");
                var beforeAppend = ConversationEventFeed.Cursor(chat); var metadata = ConversationMetadata.From(chat);
                AppendFeedMessage(new ConversationJournal(paths, "document:feed-cost"), chat, "external append");
                var appendedStart = journal.Journal.ReadRecords;
                var delta = feed.Read(metadata, beforeAppend);
                var appended = journal.Journal.ReadRecords - appendedStart;
                AssertTrue(!delta.ResyncRequired && delta.Events.Count == 1 && appended <= 7,
                    "another writer's append does not rescan the historical feed");
                AssertEqual(bodies, journal.ChatBodyReads, "page and idle read zero CAS bodies");
                Console.WriteLine("journal-feed H={0}: page records={1}, bytes={2}; idle records={3}; after append records={4}; CAS bodies=0",
                    count, reads, readBytes, idle, appended);
            });
        }
    }
}
