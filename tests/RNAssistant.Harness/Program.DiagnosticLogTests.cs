using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void DiagnosticLogsSettingsUpdatesPreserveOtherOwners()
        {
            WithTempPaths(paths => {
                var settings = new SettingsService(paths);
                var original = settings.Load(); original.Model = "original-model"; settings.Save(original);
                var stale = settings.Load();
                var store = new DiagnosticLogStore(paths.Root);
                store.SaveOptions(new DiagnosticLogOptions { MaxFileMiB = 2, MaxTotalMiB = 8, RetentionDays = 3, MaxFiles = 4 });
                stale.Model = "new-model";
                settings.Save(stale);
                var current = settings.Load();
                AssertEqual("new-model", current.Model, "ordinary settings still update their fields");
                AssertEqual(8, current.DiagnosticLogs.MaxTotalMiB, "a stale settings snapshot preserves current retention");
                store.SaveOptions(new DiagnosticLogOptions { MaxFileMiB = 1, MaxTotalMiB = 4, RetentionDays = 2, MaxFiles = 3 });
                current = settings.Load();
                AssertEqual("new-model", current.Model, "retention update preserves the latest settings owner values");
                AssertEqual(4, current.DiagnosticLogs.MaxTotalMiB, "dedicated retention update is committed");
                var file = Path.Combine(paths.Root, "logs", "rnassistant.log");
                store.Append("rnassistant.log", new[] { "diagnostics" });
                store.ClearAll();
                AssertTrue(File.Exists(paths.SettingsFile + ".lck") && !File.Exists(file), "settings lock is not diagnostic data");
            });
        }

        private static void DiagnosticLogsBoundedUtf8Pages()
        {
            WithTempPaths(paths => {
                var store = new DiagnosticLogStore(paths.Root);
                AssertEqual(0, store.List().Files.Count, "a missing logs directory is allowed");
                store.Append("rnassistant.log", Enumerable.Range(0, 500).Select(i => "Строка 🌍 " + i));
                var text = File.ReadAllText(Path.Combine(paths.Root, "logs", "rnassistant.log"));
                var page = store.Read("rnassistant.log", maxBytes: 127);
                var joined = page.Text;
                var steps = 0;
                while (page.HasEarlier)
                {
                    var offset = page.StartOffset;
                    page = store.Read(page.FileId, page.StartOffset, page.SnapshotToken, 127);
                    AssertTrue(page.StartOffset < offset, "each previous page advances");
                    AssertTrue(page.EndOffset - page.StartOffset <= 127 && !page.Text.Contains("\ufffd"), "UTF-8 boundaries stay intact and bounded");
                    joined = page.Text + joined;
                    if (++steps > 1000) throw new Exception("Non-advancing log pagination.");
                }
                AssertEqual(text, joined, "every original byte/line is represented once across pages");
                var tail = store.Read("rnassistant.log", maxBytes: 127);
                store.Append("rnassistant.log", new[] { "Поздняя запись" });
                var older = store.Read("rnassistant.log", tail.StartOffset, tail.SnapshotToken, 127);
                AssertTrue(!older.ResetRequired && older.SnapshotLength == tail.SnapshotLength, "append preserves a pinned older-page snapshot");
                store.Clear("rnassistant.log");
                store.Append("rnassistant.log", new[] { new string('x', 30000) });
                AssertTrue(store.Read("rnassistant.log", tail.StartOffset, tail.SnapshotToken, 127).ResetRequired,
                    "clear invalidates the snapshot even after the new file grows beyond the old length");
                File.WriteAllBytes(Path.Combine(paths.Root, "logs", "rnassistant.log"), Enumerable.Repeat((byte)0x80, 1000).ToArray());
                var invalid = store.Read("rnassistant.log", maxBytes: 127);
                AssertTrue(invalid.StartOffset < invalid.EndOffset && invalid.Text.Length > 0, "damaged UTF-8 remains readable and pagination advances");
                var bounded = DiagnosticLogStore.BoundRecord(new string('я', 200000) + "🌍");
                AssertTrue(Encoding.UTF8.GetByteCount(bounded) <= DiagnosticLogStore.MaximumRecordBytes && bounded.Contains("truncated"),
                    "one giant record is visibly clipped before queueing");
                var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                RuntimeThrows<OperationCanceledException>(() => store.List(cancelled.Token));
            });
        }

        private static void DiagnosticLogsRetentionAndScope()
        {
            WithTempPaths(paths => {
                var store = new DiagnosticLogStore(paths.Root);
                store.SaveOptions(new DiagnosticLogOptions { MaxFileMiB = 1, MaxTotalMiB = 2, RetentionDays = 2, MaxFiles = 3 });
                var directory = Path.Combine(paths.Root, "logs");
                var unrelated = Path.Combine(directory, "user-document.log"); File.WriteAllText(unrelated, "keep");
                var journal = Path.Combine(directory, "chat.jsonl"); File.WriteAllText(journal, "canonical");
                var nested = Path.Combine(directory, "nested"); Directory.CreateDirectory(nested);
                File.WriteAllText(Path.Combine(nested, "rnassistant.log"), "keep nested");
                File.WriteAllText(Path.Combine(paths.ChatDirectory, "journal.jsonl"), "history");
                store.Append("rnassistant.log", Enumerable.Range(0, 140).Select(i => i + new string('x', 50000)));
                var inventory = store.List();
                AssertTrue(!inventory.OverLimit && inventory.TotalBytes <= 2L * 1024 * 1024 && inventory.Files.Count <= 3,
                    "rotation enforces total bytes and file count without UI maintenance");
                AssertTrue(inventory.Files.All(f => f.ByteLength <= 1024 * 1024), "new segments never exceed the per-file cap");
                var aged = Path.Combine(directory, "20000101.log"); File.WriteAllText(aged, "old");
                File.SetLastWriteTimeUtc(aged, DateTime.UtcNow.AddDays(-3));
                var pruned = store.Prune();
                AssertTrue(!File.Exists(aged) && pruned.AffectedFiles >= 1, "retention days prune an inactive daily file");
                RuntimeThrows<ArgumentException>(() => store.Delete("../settings.json"));
                RuntimeThrows<ArgumentException>(() => store.Read(unrelated));
                RuntimeThrows<ArgumentException>(() => store.Delete("user-document.log"));
                var cleared = store.ClearAll();
                AssertTrue(cleared.AffectedFiles > 0 && cleared.Inventory.TotalBytes == 0, "clear all reports actual managed-file removal");
                AssertEqual("keep", File.ReadAllText(unrelated), "unknown .log is not managed");
                AssertEqual("canonical", File.ReadAllText(journal), "nearby JSONL is never a diagnostic file");
                AssertEqual("history", File.ReadAllText(Path.Combine(paths.ChatDirectory, "journal.jsonl")), "history root remains untouched");
                AssertEqual("keep nested", File.ReadAllText(Path.Combine(nested, "rnassistant.log")), "enumeration is never recursive");
                store.Append("rnassistant.log", new[] { "after deletion" });
                AssertTrue(store.Read("rnassistant.log").Text.Contains("after deletion"), "active writer recreates deleted file safely");
                File.WriteAllText(paths.SettingsFile, "broken json");
                var another = new DiagnosticLogStore(paths.Root);
                another.Append("rnassistant.log", new[] { "still observable" });
                AssertTrue(another.Read("rnassistant.log").Text.Contains("still observable") && another.List().Issues.Any(i => i.Operation == "settings"),
                    "malformed settings cannot silence diagnostics and fallback is visible");
            });
        }

        private static void DiagnosticLogsConcurrentOwnersAndLinks()
        {
            WithTempPaths(paths => {
                var first = new DiagnosticLogStore(paths.Root); var second = new DiagnosticLogStore(paths.Root);
                first.List();
                Parallel.Invoke(
                    () => { for (var i = 0; i < 20; i++) first.Append("rnassistant.log", new[] { "one:" + i }); },
                    () => { for (var i = 0; i < 20; i++) second.Append("rnassistant.log", new[] { "two:" + i }); });
                AssertEqual(40, first.Read("rnassistant.log").Text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Length,
                    "separate owner instances serialize complete append batches");
                var outside = Path.Combine(paths.Root, "outside.txt"); File.WriteAllText(outside, "keep outside");
                var link = Path.Combine(paths.Root, "logs", "20000101.log");
                try
                {
                    File.CreateSymbolicLink(link, outside);
                    AssertTrue(first.List().Issues.Any(i => i.FileId == "20000101.log"), "linked managed name is reported, never opened");
                    RuntimeThrows<IOException>(() => first.Read("20000101.log"));
                    first.ClearAll();
                    AssertEqual("keep outside", File.ReadAllText(outside), "clear all cannot follow a linked log");
                }
                catch (UnauthorizedAccessException) { } // Windows without symlink privilege has separate native qualification.
                finally { if (File.Exists(link)) File.Delete(link); }
            });
        }
    }
}
