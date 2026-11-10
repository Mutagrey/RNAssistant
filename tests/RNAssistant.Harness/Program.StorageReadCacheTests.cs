using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void VerifiedReadCacheAndTextWindows()
        {
            foreach (var encrypted in new[] { false, true })
            foreach (var compressed in new[] { false, true }) WithTempPaths(paths => {
                var protector = encrypted ? new StorageProtector(HistoryIntegrityModes.HmacSha256,
                    HistoryEncryptionModes.Aes256CbcHmacSha256, "cache-fixture", Enumerable.Repeat((byte)3, 16).ToArray()) : StorageProtector.None;
                var blobs = new ChatBlobStore(paths, () => protector);
                var text = string.Concat(Enumerable.Repeat("α😀\r\nb\rc\n", 8000));
                var body = blobs.StoreBytes(Encoding.UTF8.GetBytes(text), compressed ? "text/plain" : "application/octet-stream");
                var path = blobs.PathFor(body.Sha256);
                var expected = new string[24]; long beforeCharacters = 0, afterCharacters = 0;
                var allocated = GC.GetAllocatedBytesForCurrentThread(); var clock = Stopwatch.StartNew();
                for (var i = 0; i < expected.Length; i++)
                {
                    var window = new ResourceTextWindow(1 + i * 137, 2, true, 1000);
                    // The unchanged large/seek streaming reader is the pre-cache path.
                    using (var stream = CasBlobCodec.OpenVerifiedRead(path, body.ByteLength, body.Sha256, protector,
                        "blob|" + body.Sha256 + "|" + body.ByteLength, CancellationToken.None, null)) window.Read(stream);
                    expected[i] = window.Text; beforeCharacters += window.InspectedCharacters;
                }
                clock.Stop(); var beforeMs = clock.Elapsed.TotalMilliseconds;
                var beforeAllocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                var decodes = ChatBlobStore.CachedBodyDecodes; var indexes = ChatBlobStore.CachedTextIndexes; var storedBytes = ChatBlobStore.CachedStoredBytes;
                allocated = GC.GetAllocatedBytesForCurrentThread(); clock.Restart();
                for (var i = 0; i < expected.Length; i++)
                {
                    var window = new ResourceTextWindow(1 + i * 137, 2, true, 1000);
                    // Distinct consumers share the same bounded storage-owned entry.
                    using (var stream = new ChatBlobStore(paths, () => protector).OpenVerifiedRead(body, body.ByteLength)) window.Read(stream);
                    AssertEqual(expected[i], window.Text, "cached line windows preserve mixed line endings and Unicode");
                    AssertEqual(text.Length, window.TotalCharacters, "known total is retained from one exact index");
                    afterCharacters += window.InspectedCharacters;
                }
                clock.Stop();
                AssertEqual(1L, ChatBlobStore.CachedBodyDecodes - decodes, "one verified plaintext decode for repeated exact windows");
                AssertEqual(1L, ChatBlobStore.CachedTextIndexes - indexes, "one line index for the immutable body");
                AssertEqual((long)new FileInfo(path).Length * expected.Length, ChatBlobStore.CachedStoredBytes - storedBytes,
                    "every hit reads and hashes all current encoded bytes");
                Console.WriteLine("READ_CACHE encrypted=" + encrypted + " compressed=" + compressed + " sourceChars=" + text.Length +
                    " windows=" + expected.Length + " scannedBefore=" + beforeCharacters + " indexedPlusSelected=" + (text.Length + afterCharacters) +
                    " allocatedBefore=" + beforeAllocated + " allocatedAfter=" + (GC.GetAllocatedBytesForCurrentThread() - allocated) +
                    " wallBeforeMs=" + beforeMs.ToString("F2") + " wallAfterMs=" + clock.Elapsed.TotalMilliseconds.ToString("F2"));
                var clone = blobs.ReadBytes(body); clone[0] ^= 1;
                AssertEqual(text, blobs.ReadText(body), "readers cannot mutate the shared immutable bytes");
                using (var stream = blobs.OpenVerifiedRead(body, body.ByteLength))
                    RuntimeThrows<WorkspaceFileException>(() => new ResourceTextWindow(2, 1, false, 10).Read(stream));
                using (var stream = blobs.OpenVerifiedRead(body, body.ByteLength))
                {
                    var window = new ResourceTextWindow(0, 2, false, 10, true); window.Read(stream);
                    AssertEqual("α", window.Text, "implicit end trimming never splits a surrogate pair");
                }
                var encoded = File.ReadAllBytes(path); var modified = (byte[])encoded.Clone(); modified[modified.Length - 1] ^= 1;
                var stamp = File.GetLastWriteTimeUtc(path); File.WriteAllBytes(path, modified); File.SetLastWriteTimeUtc(path, stamp);
                AssertTrue(!blobs.HasVerifiedReference(body) && blobs.ReadBytes(body) == null, "same-size same-time corruption cannot reuse a cached proof");
                File.WriteAllBytes(path, encoded);
                AssertEqual(text, blobs.ReadText(body), "only restored exact bytes regain availability");
                using (var token = new CancellationTokenSource())
                using (var stream = blobs.OpenVerifiedRead(body, body.ByteLength, token.Token))
                { token.Cancel(); RuntimeThrows<OperationCanceledException>(() => stream.ReadByte()); }
                File.Delete(path);
                AssertTrue(blobs.ReadText(body) == null, "cache does not conceal a missing canonical body");
            });

            WithTempPaths(paths => {
                var blobs = new ChatBlobStore(paths);
                var body = blobs.StoreText(new string('x', 100000), "text/plain");
                var decodes = ChatBlobStore.CachedBodyDecodes;
                Task.WaitAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => {
                    using (var stream = new ChatBlobStore(paths).OpenVerifiedRead(body, body.ByteLength)) AssertEqual((int)'x', stream.ReadByte(), "parallel exact read");
                })).ToArray());
                AssertEqual(1L, ChatBlobStore.CachedBodyDecodes - decodes, "same-key misses share one plaintext decode");
                var first = blobs.OpenVerifiedRead(body, body.ByteLength);
                var other = blobs.StoreText("another exact source", "text/plain");
                AssertEqual("another exact source", blobs.ReadText(other), "a different body replaces the one cache slot");
                AssertEqual((int)'x', first.ReadByte(), "a live immutable lease survives cache eviction");
                first.Dispose(); RuntimeThrows<ObjectDisposedException>(() => first.ReadByte());
                var large = blobs.StoreBytes(new byte[ChatBlobStore.MaximumCachedBodyBytes + 1], "application/octet-stream");
                using (var stream = blobs.OpenVerifiedRead(large, large.ByteLength)) AssertTrue(!(stream is ChatBlobStore.CachedReadStream), "large body keeps bounded streaming admission");
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "project"));
                var file = Path.Combine(workspace.RootPath, "fresh.txt"); File.WriteAllText(file, "old\nview");
                var files = new WorkspaceFileService(paths);
                var before = files.ReadTextLines(workspace, "fresh.txt", 1, 1);
                File.WriteAllText(file, "new\nview");
                var after = files.ReadTextLines(workspace, "fresh.txt", 1, 1);
                AssertEqual("new\n", after.Text, "explicit live read never silently substitutes cached history");
                AssertTrue(before.Reference.Revision != after.Reference.Revision && !after.Evidence.Complete, "fresh partial read retains exact partial authority");
            });
        }
    }
}
