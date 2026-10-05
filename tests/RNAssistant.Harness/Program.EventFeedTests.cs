using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void EventFeedPagesAndPollsWithoutHistoryMaterialization()
        {
            WithTempPaths(paths =>
            {
                var writer = new ChatStore(paths);
                var session = writer.Create("Word", "event-feed", "Events.docx", "Events");
                var initial = ConversationEventFeed.Cursor(session);
                for (var index = 0; index < 130; index++)
                    writer.AppendTrace(session, SessionEventTypes.AssistantChunk, new { index }, "private body", "text/plain", null, null, null);
                var highWater = ConversationEventFeed.Cursor(session);
                var reader = new ChatStore(paths);
                var feed = new ConversationEventFeed(new ChatEventStoreAdapter(reader));
                var cursor = initial;
                var notices = new List<ConversationEventNotice>();
                do
                {
                    var page = feed.Read(session, cursor, highWater, 7);
                    AssertTrue(!page.ResyncRequired && page.Events.Count <= 7, "bounded canonical page");
                    AssertEqual(highWater, page.HighWater, "pinned high-water");
                    notices.AddRange(page.Events); cursor = page.NextCursor;
                    if (!page.HasMore) break;
                } while (true);
                AssertEqual(130, notices.Count, "no missing records");
                AssertEqual(130, notices.Select(item => item.Sequence).Distinct().Count(), "no duplicate records");
                AssertEqual(highWater, cursor, "page cursor reaches exact snapshot");
                AssertEqual(1L, reader.EventReaderFullValidationCount, "paging an unchanged stream validates its prefix once");
                AssertEqual(0, feed.Read(session, cursor).Events.Count, "idle read has no fabricated notices");
                AssertEqual(1L, reader.EventReaderFullValidationCount, "idle polling does not scan history");
                writer.AppendTrace(session, SessionEventTypes.AssistantChunk, new { index = 130 }, null, null, null, null, null);
                var delta = feed.Read(session, cursor);
                AssertEqual(1, delta.Events.Count, "external append is delivered exactly once");
                AssertEqual(2L, reader.EventReaderFullValidationCount, "changed file revalidates its prefix");
                AssertEqual(ConversationEventFeed.Cursor(session), delta.NextCursor, "new canonical head");
                AssertEqual(0L, reader.ProjectionFullReplayCount, "feed does not construct a session projection");
            });
        }

        private static void EventFeedRejectsChangedOrIncompleteHistory()
        {
            WithTempPaths(paths =>
            {
                var writer = new ChatStore(paths);
                var session = writer.Create("Word", "event-feed-integrity", "Events.docx", "Original");
                var initial = ConversationEventFeed.Cursor(session);
                var feed = new ConversationEventFeed(new ChatEventStoreAdapter(new ChatStore(paths)));
                AssertTrue(!feed.Read(session, initial).ResyncRequired, "warm the validated prefix");
                var path = SessionEventFile(paths, session);
                writer.AppendTrace(session, SessionEventTypes.AssistantChunk, new { index = 1 }, null, null, null, null, null);
                var retained = File.ReadAllBytes(path);
                ReplaceFileBytes(path, "Original", "Corrupt!");
                AssertEqual(ConversationResyncReason.HistoryUnavailable, feed.Read(session, initial).Reason,
                    "a valid appended suffix cannot mask a damaged prefix");
                File.WriteAllBytes(path, retained);
                var valid = feed.Read(session, initial);
                AssertTrue(!valid.ResyncRequired, "fresh validation after explicit repair");
                File.AppendAllText(path, "{\"unfinished\":");
                AssertEqual(ConversationResyncReason.HistoryUnavailable, feed.Read(session, valid.NextCursor).Reason,
                    "torn suffix requires resync");
                File.WriteAllBytes(path, retained);
                AssertTrue(!feed.Read(session, initial).ResyncRequired, "restored exact stream");
                var lines = File.ReadAllLines(path);
                File.WriteAllText(path, lines[0] + "\n");
                AssertEqual(ConversationResyncReason.HistoryChanged, feed.Read(session, valid.NextCursor).Reason,
                    "truncated anchor is not an empty successful page");
                File.WriteAllBytes(path, retained);
                var other = writer.Create("Word", "event-feed-other", "Other.docx", "Other");
                AssertEqual(ConversationResyncReason.DifferentSession, feed.Read(session, ConversationEventFeed.Cursor(other)).Reason,
                    "cross-session anchor rejected");
            });
        }

        private static void EventFeedSubscriptionBackpressureAndCancellation()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Word", "event-feed-subscribe", "Events.docx", "Events");
                var feed = new ConversationEventFeed(new ChatEventStoreAdapter(store));
                var entered = new TaskCompletionSource<bool>();
                var release = new TaskCompletionSource<bool>();
                var delivered = 0;
                using (var cancel = new CancellationTokenSource())
                {
                    var subscribe = feed.SubscribeAsync(session, ConversationEventFeed.Cursor(session), async page =>
                    {
                        AssertTrue(!page.ResyncRequired, "healthy subscription");
                        delivered++;
                        if (delivered == 1) { entered.SetResult(true); await release.Task; }
                        else { AssertEqual(1, page.Events.Count, "append after snapshot delivered"); cancel.Cancel(); }
                    }, 1, cancel.Token);
                    AssertTrue(entered.Task.Wait(2000), "initial snapshot acknowledgement is delivered");
                    // Callback cannot retain a storage lock or queue more pages.
                    var append = Task.Run(() => store.AppendTrace(session, SessionEventTypes.AssistantChunk, new { index = 1 }, null, null, null, null, null));
                    AssertTrue(append.Wait(2000), "writer proceeds while subscriber waits");
                    AssertEqual(1, delivered, "delivery is awaited for backpressure");
                    release.SetResult(true);
                    AssertTrue(Task.WhenAny(subscribe, Task.Delay(3000)).GetAwaiter().GetResult() == subscribe, "bounded cancellation");
                    RuntimeThrows<OperationCanceledException>(() => subscribe.GetAwaiter().GetResult());
                    AssertEqual(2, delivered, "no extra callback after cancellation");
                }
            });
        }
    }
}
