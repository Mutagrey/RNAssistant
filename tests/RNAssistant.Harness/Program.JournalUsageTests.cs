using System;
using System.IO;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void AssertSameJournalUsage(ChatSessionHeader expected, ChatSessionHeader actual)
        {
            AssertEqual(expected.JsonlByteLength, actual.JsonlByteLength, "canonical frame attribution is stable");
            AssertEqual(expected.CasBlobCount, actual.CasBlobCount, "unique retained CAS count is stable");
            AssertEqual(expected.CasLogicalByteLength, actual.CasLogicalByteLength, "logical CAS bytes are stable");
            AssertEqual(expected.CasStoredByteLength, actual.CasStoredByteLength, "stored CAS bytes are stable");
        }

        private static void JournalUsageConsumers()
        {
            foreach (var office in new[] { true, false }) WithJournalInput(office, (paths, journal, source, scope) => {
                IConversationStore store = journal;
                var before = store.ListHeaders().Single();
                var text = new string('ж', 32000);
                var reference = new ChatBlobStore(paths).StoreText(text, "text/plain");
                var blobPath = Path.Combine(paths.ChatBlobDirectory, reference.Sha256.Substring(0, 2), reference.Sha256 + ".blob");
                var bytes = journal.Journal.WrittenBytes;
                for (var index = 0; index < 16; index++) AppendFeedMessage(store, source, text);
                var reads = journal.ChatBodyReads; var records = journal.Journal.ReadRecords;
                var header = store.ListHeaders().Single();
                AssertEqual(reads, journal.ChatBodyReads, "usage catalog hydrates no retained bodies");
                AssertTrue(journal.Journal.ReadRecords - records <= 8, "healthy usage lookup does not replay historical frames");
                AssertEqual(before.JsonlByteLength + journal.Journal.WrittenBytes - bytes, header.JsonlByteLength, "new frame bytes are attributed exactly once");
                AssertEqual(before.CasBlobCount + 1, header.CasBlobCount, "repeated message bodies count once per chat");
                AssertEqual(before.CasLogicalByteLength + reference.ByteLength, header.CasLogicalByteLength, "logical bytes follow the exact reference");
                AssertEqual(before.CasStoredByteLength + new FileInfo(blobPath).Length, header.CasStoredByteLength, "physical metadata includes compressed bytes once");
                AssertTrue(header.CasStoredByteLength < header.CasLogicalByteLength, "physical and logical sizes remain distinct");
                var peer = store.CreateTransient(source.Host, source.DocumentKey, source.DocumentTitle, "Peer");
                store.Commit(peer, new ConversationCommit()); AppendFeedMessage(store, peer, "Independent body");
                AssertSameJournalUsage(header, store.ListHeaders().Single(item => item.Id == source.Id));
                if (office)
                {
                    var summaries = new ChatSessionService(new FakeOfficeAdapter(), store).GetChatSummaries(source.Id);
                    var actual = summaries.Single(item => item.Id == source.Id);
                    AssertEqual(header.CasStoredByteLength, actual.CasStoredByteLength, "Office catalog consumes native usage");
                    AssertEqual(header.JsonlByteLength, actual.JsonlByteLength, "Office catalog uses per-chat frame bytes");
                }
                else
                {
                    var workspaces = new WorkspaceStore(paths); var workspace = workspaces.OpenConnected(source.WorkspaceId);
                    AssertSameJournalUsage(header, workspaces.ListSessions(store, workspace).Single(item => item.Id == source.Id));
                }
                var peerBefore = store.ListHeaders().Single(item => item.Id == peer.Id); bytes = journal.Journal.WrittenBytes;
                store.MoveDocument(source.Host, source.DocumentKey, source.Host, office ? "saved" : source.DocumentKey, "Relocated", null);
                var moved = store.ListHeaders();
                AssertEqual(header.JsonlByteLength + journal.Journal.WrittenBytes - bytes, moved.Single(item => item.Id == source.Id).JsonlByteLength,
                    "shared relocation frame is attributed once to each affected chat");
                AssertEqual(peerBefore.JsonlByteLength + journal.Journal.WrittenBytes - bytes, moved.Single(item => item.Id == peer.Id).JsonlByteLength,
                    "second batch participant has the same shared frame attribution");
                journal.Rebuild();
                foreach (var expected in moved) AssertSameJournalUsage(expected, store.ListHeaders().Single(item => item.Id == expected.Id));
                var backup = Path.Combine(paths.Root, "usage-backup"); JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "usage-restore"), _ => StorageProtector.None);
                var copy = (IConversationStore)new ConversationJournal(restored, scope);
                foreach (var expected in moved) AssertSameJournalUsage(expected, copy.ListHeaders().Single(item => item.Id == expected.Id));

                var blob = File.ReadAllBytes(blobPath); File.Delete(blobPath);
                var metadata = store.ListHeaders(false).Single(item => item.Id == source.Id);
                AssertEqual(header.CasBlobCount, metadata.CasBlobCount, "metadata-only catalog retains bounded logical counters");
                AssertEqual(0L, metadata.CasStoredByteLength, "metadata-only request omits physical size enumeration");
                var missing = store.ListHeaders().Single(item => item.Id == source.Id);
                AssertEqual(1, missing.CasMissingBlobCount, "catalog surfaces an unavailable retained body");
                AssertEqual(ChatStorageWarningLevels.Critical, missing.StorageWarningLevel, "missing CAS cannot appear healthy");
                File.WriteAllBytes(blobPath, blob);
                AssertEqual(0, store.ListHeaders().Single(item => item.Id == source.Id).CasMissingBlobCount, "next catalog read does not retain stale missing state");
            });
        }

        private static void JournalUsageReceipts()
        {
            foreach (var fault in new[] { "before_append", "after_flush", "before_index", "after_usage_index" })
                WithJournalInput(false, (paths, journal, source, scope) => {
                    IConversationStore store = journal; var before = store.ListHeaders().Single();
                    var message = new ChatMessage { Role = "user", Content = "New retained bytes" }; source.Messages.Add(message);
                    var commit = ConversationCommit.UserInput(message); var hit = false;
                    Action<string> fail = point => { if (point == fault && !hit) { hit = true; throw new IOException("fixture: " + fault); } };
                    journal.Journal.Fault = fail; journal.Fault = fail;
                    if (fault == "before_append" || fault == "after_flush") RuntimeThrows<IOException>(() => store.Commit(source, commit));
                    else store.Commit(source, commit);
                    journal.Journal.Fault = null; journal.Fault = null;
                    AssertTrue(hit, "requested usage fault was reached");
                    var actual = store.ListHeaders().Single();
                    if (fault == "before_append") AssertSameJournalUsage(before, actual);
                    else
                    {
                        AssertEqual(before.CasBlobCount + 1, actual.CasBlobCount, "accepted body counts once after interrupted index work");
                        var bytes = journal.Journal.WrittenBytes; store.Commit(source, commit);
                        AssertEqual(bytes, journal.Journal.WrittenBytes, "lost acknowledgement does not append another frame");
                        AssertSameJournalUsage(actual, store.ListHeaders().Single());
                    }
                    journal = new ConversationJournal(paths, scope); journal.Rebuild();
                    AssertSameJournalUsage(actual, ((IConversationStore)journal).ListHeaders().Single());
                });
        }
    }
}
