using System;
using System.IO;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void AssertStorageSummary(ChatSessionHeader expected, ChatSessionSummary actual, string context)
        {
            AssertEqual(expected.JsonlByteLength, actual.JsonlByteLength, context + ": per-chat JSONL bytes");
            AssertEqual(expected.CasBlobCount, actual.CasBlobCount, context + ": unique CAS count");
            AssertEqual(expected.CasLogicalByteLength, actual.CasLogicalByteLength, context + ": logical CAS bytes");
            AssertEqual(expected.CasStoredByteLength, actual.CasStoredByteLength, context + ": stored CAS bytes");
            AssertEqual(expected.CasMissingBlobCount, actual.CasMissingBlobCount, context + ": missing CAS count");
            AssertEqual(expected.CasReferenceIssueCount, actual.CasReferenceIssueCount, context + ": CAS reference issues");
            AssertEqual(expected.StorageWarningLevel, actual.StorageWarningLevel, context + ": storage warning");
        }

        private static void AssertJournalStorageSummaries(ConversationJournal journal, ChatSession chat, string context)
        {
            IConversationStore store = journal;
            var expected = store.ListHeaders().Single(item => item.Id == chat.Id);
            var service = new ChatSessionService(FakeOfficeAdapter.ForHost("Excel"), store);
            var bodies = journal.ChatBodyReads; var records = journal.Journal.ReadRecords;
            var revision = chat.Revision; var cursor = chat.StorageByteLength;
            var hash = chat.StorageHeadHash; var tail = chat.StorageTailByteOffset;
            AssertStorageSummary(expected, service.GetChatSummary(chat), context + " active");
            AssertStorageSummary(expected, service.GetChatSummaries(chat.Id).Single(item => item.Id == chat.Id), context + " catalog");
            AssertEqual(bodies, journal.ChatBodyReads, context + ": summary/catalog hydrate no history or CAS bodies");
            AssertTrue(journal.Journal.ReadRecords - records <= 12, context + ": summary/catalog read only bounded metadata frames");
            AssertEqual(revision, chat.Revision, context + ": usage read cannot advance revision");
            AssertEqual(cursor, chat.StorageByteLength, context + ": physical end cursor remains unchanged");
            AssertEqual(hash, chat.StorageHeadHash, context + ": physical head remains unchanged");
            AssertEqual(tail, chat.StorageTailByteOffset, context + ": physical tail remains unchanged");
        }

        private static ChatSession CreateStorageUsageChat(IConversationStore store, string title)
        {
            var chat = store.CreateTransient("Excel", "usage-book", "Usage book", title);
            store.Commit(chat, new ConversationCommit());
            return chat;
        }

        private static void JournalStorageUsageSharedSegment()
        {
            WithTempPaths(paths => {
                var journal = new ConversationJournal(paths, "document:per-chat-usage"); IConversationStore store = journal;
                var active = CreateStorageUsageChat(store, "Active");
                var text = new string('ж', 32000);
                for (var batch = 0; batch < 6; batch++)
                {
                    var messages = Enumerable.Range(0, 16).Select(_ => new ChatMessage { Role = "user", Content = text }).ToArray();
                    active.Messages.AddRange(messages); store.Commit(active, new ConversationCommit(messages));
                }
                var idle = CreateStorageUsageChat(store, "Idle"); AppendFeedMessage(store, idle, "Unchanged body");
                var idleBefore = store.ListHeaders().Single(item => item.Id == idle.Id);
                var idleRevision = idle.Revision; var idleCursor = idle.StorageByteLength;
                AssertTrue(idle.StorageByteLength > idleBefore.JsonlByteLength,
                    "the later chat's segment cursor includes bytes owned by the earlier chat");
                var activeBefore = store.ListHeaders().Single(item => item.Id == active.Id);
                var written = journal.Journal.WrittenBytes;
                for (var index = 0; index < 12; index++) AppendFeedMessage(store, active, text);
                var activeAfter = store.ListHeaders().Single(item => item.Id == active.Id);
                AssertEqual(activeBefore.JsonlByteLength + journal.Journal.WrittenBytes - written, activeAfter.JsonlByteLength,
                    "only the active chat receives its additional canonical frame bytes");
                AssertSameJournalUsage(idleBefore, store.ListHeaders().Single(item => item.Id == idle.Id));
                AssertEqual(idleRevision, idle.Revision, "other-chat writes leave the idle revision unchanged");
                AssertEqual(idleCursor, idle.StorageByteLength, "other-chat writes leave the idle cursor unchanged");
                AssertEqual(1, StorageJournal.Inventory(paths).Single().Segments.Length,
                    "both chats and all regression writes share one physical segment");
                AssertTrue(activeAfter.CasStoredByteLength < activeAfter.CasLogicalByteLength,
                    "the scenario distinguishes compressed physical CAS size from logical bytes");
                AssertJournalStorageSummaries(journal, active, "busy chat");
                AssertJournalStorageSummaries(journal, idle, "idle chat");

                // Selection is attributed to this chat but does not advance its
                // message/session receipt. The summary must ask the metadata owner.
                var revision = idle.Revision; written = journal.Journal.WrittenBytes;
                store.SaveActiveSessionId(idle.Host, idle.DocumentKey, idle.Id);
                var selected = store.ListHeaders().Single(item => item.Id == idle.Id);
                AssertEqual(idleBefore.JsonlByteLength + journal.Journal.WrittenBytes - written, selected.JsonlByteLength,
                    "selection frame is included without a new chat commit");
                AssertEqual(revision, idle.Revision, "selection does not manufacture a session revision");
                AssertJournalStorageSummaries(journal, idle, "selected idle chat");
                Console.WriteLine("journal-storage shared: active JSONL={0}, idle JSONL={1}, idle cursor={2}; history bodies for summaries=0",
                    activeAfter.JsonlByteLength, selected.JsonlByteLength, idle.StorageByteLength);
            });
        }

        private static void JournalStorageUsageRecovery()
        {
            WithTempPaths(paths => {
                const string scope = "document:per-chat-usage-recovery";
                var journal = new ConversationJournal(paths, scope); IConversationStore store = journal;
                var active = CreateStorageUsageChat(store, "Active"); AppendFeedMessage(store, active, "Retained first body");
                var idle = CreateStorageUsageChat(store, "Idle"); AppendFeedMessage(store, idle, "Idle body");
                var idleBefore = store.ListHeaders().Single(item => item.Id == idle.Id);
                var prefix = File.ReadAllBytes(journal.Journal.SegmentPath(1));
                var stale = store.Load(active.Id);
                var accepted = new ChatMessage { Role = "user", Content = new string('x', 16000) };
                active.Messages.Add(accepted); var commit = ConversationCommit.UserInput(accepted);
                journal.Journal.Fault = point => { if (point == "after_flush") throw new IOException("lost usage acknowledgement"); };
                RuntimeThrows<IOException>(() => store.Commit(active, commit)); journal.Journal.Fault = null;
                var revision = active.Revision; var written = journal.Journal.WrittenBytes;
                store.Commit(active, commit);
                AssertEqual(written, journal.Journal.WrittenBytes, "retry reuses the accepted receipt without appending");
                AssertEqual(revision, active.Revision, "retry preserves the accepted revision");
                AssertJournalStorageSummaries(journal, stale, "stale session reads current usage");
                RuntimeThrows<ChatConcurrencyException>(() => store.Commit(stale, new ConversationCommit()));
                AssertTrue(prefix.SequenceEqual(File.ReadAllBytes(journal.Journal.SegmentPath(1)).Take(prefix.Length)),
                    "new accounting and acknowledged writes preserve the previous journal prefix");
                AssertSameJournalUsage(idleBefore, store.ListHeaders().Single(item => item.Id == idle.Id));
                AssertJournalStorageSummaries(journal, active, "lost-ack recovery");
                var expected = store.ListHeaders().ToDictionary(item => item.Id);
                var physical = new[] { active, idle }.ToDictionary(item => item.Id,
                    item => Tuple.Create(item.Revision, item.StorageByteLength, item.StorageHeadHash, item.StorageTailByteOffset));
                var canonical = StorageJournal.Inventory(paths).SelectMany(item => item.Segments)
                    .Concat(Directory.EnumerateFiles(paths.ChatBlobDirectory, "*.blob", SearchOption.AllDirectories))
                    .ToDictionary(file => file, File.ReadAllBytes);

                var reopened = new ConversationJournal(paths, scope);
                for (var pass = 0; pass < 2; pass++)
                {
                    if (pass == 1) reopened.Rebuild();
                    IConversationStore copy = reopened;
                    foreach (var original in expected.Values)
                    {
                        var loaded = copy.Load(original.Id);
                        AssertSameJournalUsage(original, copy.ListHeaders().Single(item => item.Id == original.Id));
                        AssertEqual(physical[loaded.Id].Item1, loaded.Revision, "reopen/rebuild preserves chat revision");
                        AssertEqual(physical[loaded.Id].Item2, loaded.StorageByteLength, "reopen/rebuild preserves physical cursor");
                        AssertEqual(physical[loaded.Id].Item3, loaded.StorageHeadHash, "reopen/rebuild preserves physical hash");
                        AssertEqual(physical[loaded.Id].Item4, loaded.StorageTailByteOffset, "reopen/rebuild preserves physical tail");
                        AssertJournalStorageSummaries(reopened, loaded, pass == 0 ? "reopened" : "rebuilt");
                    }
                }
                AssertTrue(canonical.All(item => item.Value.SequenceEqual(File.ReadAllBytes(item.Key))),
                    "reopen and disposable-index rebuild never rewrite journal or CAS bytes");
                var backup = Path.Combine(paths.Root, "usage-summary-backup");
                JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                var restoredPaths = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "usage-summary-restore"), _ => StorageProtector.None);
                var restored = new ConversationJournal(restoredPaths, scope); IConversationStore restoredStore = restored;
                foreach (var original in expected.Values)
                {
                    var loaded = restoredStore.Load(original.Id);
                    AssertSameJournalUsage(original, restoredStore.ListHeaders().Single(item => item.Id == original.Id));
                    AssertEqual(physical[loaded.Id].Item1, loaded.Revision, "restore preserves revision");
                    AssertEqual(physical[loaded.Id].Item2, loaded.StorageByteLength, "restore preserves physical cursor");
                    AssertJournalStorageSummaries(restored, loaded, "restored");
                }
                AssertTrue(canonical.All(item => item.Value.SequenceEqual(File.ReadAllBytes(
                    Path.Combine(restoredPaths.Root, item.Key.Substring(paths.Root.Length + 1))))),
                    "backup/restore retains exact canonical journal and CAS bytes");
                AssertEqual(accepted.Content, restoredStore.Load(active.Id).Messages.Last().Content,
                    "restored body remains readable from the original CAS publication");
            });
        }

        private static void JournalStorageUsageWarnings()
        {
            WithTempPaths(paths => {
                const string scope = "document:per-chat-usage-warning";
                var journal = new ConversationJournal(paths, scope); IConversationStore store = journal;
                var chat = CreateStorageUsageChat(store, "Warnings");
                var text = new string('z', 32000); AppendFeedMessage(store, chat, text);
                var idle = CreateStorageUsageChat(store, "Idle");
                var cursor = chat.StorageByteLength;
                try
                {
                    chat.StorageByteLength = ChatStorageUsagePolicy.CriticalJsonlByteLength + 1;
                    // Synthetic counters exercise the existing metadata owner's
                    // warning boundaries; this does not claim a 256 MiB journal.
                    var bytes = new[] { ChatStorageUsagePolicy.WarningJsonlByteLength - 1,
                        ChatStorageUsagePolicy.WarningJsonlByteLength, ChatStorageUsagePolicy.CriticalJsonlByteLength - 1,
                        ChatStorageUsagePolicy.CriticalJsonlByteLength };
                    var levels = new[] { ChatStorageWarningLevels.None, ChatStorageWarningLevels.Warning,
                        ChatStorageWarningLevels.Warning, ChatStorageWarningLevels.Critical };
                    for (var index = 0; index < bytes.Length; index++)
                    {
                        using (var db = JournalIndex.Open(paths))
                        using (var command = db.CreateCommand())
                        {
                            command.CommandText = "UPDATE chat_state SET journal_bytes=$bytes WHERE stream=$stream AND chat=$chat";
                            command.Parameters.AddWithValue("$bytes", bytes[index]); command.Parameters.AddWithValue("$stream", scope);
                            command.Parameters.AddWithValue("$chat", chat.Id);
                            AssertEqual(1, command.ExecuteNonQuery(), "fixture updates exactly the selected chat's derived counter");
                        }
                        AssertEqual(levels[index], store.ListHeaders().Single(item => item.Id == chat.Id).StorageWarningLevel,
                            "warning boundary " + bytes[index]);
                        AssertJournalStorageSummaries(journal, chat, "warning boundary " + bytes[index]);
                        AssertEqual(ChatStorageWarningLevels.None, new ChatSessionService(FakeOfficeAdapter.ForHost("Excel"), store)
                            .GetChatSummary(idle).StorageWarningLevel, "another chat's large counter cannot warn the idle chat");
                    }
                }
                finally { chat.StorageByteLength = cursor; }
                journal.Rebuild(); AssertJournalStorageSummaries(journal, chat, "canonical counters restored");
                var blobs = new ChatBlobStore(paths); var reference = blobs.StoreText(text, "text/plain");
                var path = blobs.PathFor(reference.Sha256); var content = File.ReadAllBytes(path);
                File.Delete(path);
                var missing = store.ListHeaders().Single(item => item.Id == chat.Id);
                AssertEqual(1, missing.CasMissingBlobCount, "exactly the removed test CAS body is unavailable");
                AssertEqual(ChatStorageWarningLevels.Critical, missing.StorageWarningLevel, "missing CAS produces Critical");
                AssertJournalStorageSummaries(journal, chat, "missing CAS");
                File.WriteAllBytes(path, content);
                var repaired = store.ListHeaders().Single(item => item.Id == chat.Id);
                AssertEqual(0, repaired.CasMissingBlobCount, "a restored test CAS body is visible on the next read");
                AssertEqual(ChatStorageWarningLevels.None, repaired.StorageWarningLevel, "restored CAS removes the warning");
                AssertJournalStorageSummaries(journal, chat, "restored CAS");
            });
        }

        private static void ChatStorageSummaryLegacyAndTransient()
        {
            WithTempPaths(paths => {
                var legacy = new ChatStore(paths); var store = ConversationStore(legacy);
                var service = new ChatSessionService(FakeOfficeAdapter.ForHost("Excel"), store);
                var chat = store.CreateTransient("Excel", "usage-book", "Usage book", "Unsaved");
                var transient = service.GetChatSummary(chat);
                AssertEqual(0L, transient.JsonlByteLength, "transient chat has no attributed journal bytes");
                AssertEqual(ChatStorageWarningLevels.None, transient.StorageWarningLevel, "transient chat has no storage warning");
                store.Commit(chat, new ConversationCommit()); AppendFeedMessage(store, chat, new string('q', 32000));
                legacy.AppendTrace(chat, SessionEventTypes.AssistantChunk, new { count = 1 }, new string('q', 32000),
                    "text/plain", "usage-run", "usage-turn", "usage-step");
                var expected = store.ListHeaders().Single();
                AssertTrue(expected.JsonlByteLength > 0 && expected.CasStoredByteLength > 0, "legacy fixture has persisted JSONL and CAS");
                chat.Title = "Local draft title";
                var summary = service.GetChatSummary(chat);
                AssertStorageSummary(expected, summary, "legacy active");
                AssertEqual(chat.Title, summary.Title, "storage metadata does not overwrite current presentation fields");
                AssertEqual(chat.Revision, summary.Revision, "storage metadata does not overwrite current revision");
                var metadataOnly = store.ReadHeader(chat.Id);
                AssertEqual(0L, metadataOnly.CasStoredByteLength, "default header request remains metadata-only");
            });
        }
    }
}
