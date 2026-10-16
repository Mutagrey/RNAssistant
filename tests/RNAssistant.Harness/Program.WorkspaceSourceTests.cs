using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
        private static void WorkspaceLargeSourcesStayExact()
        {
            WithTempPaths(paths =>
            {
                const long length = 80L * 1024 * 1024;
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "workspace"));
                var path = Path.Combine(workspace.RootPath, "source.bin");
                var expectedHash = WriteSourceFixture(path, length);
                var files = new WorkspaceFileService(paths);
                var reports = new List<FileTransferProgress>();
                var allocationStart = GC.GetAllocatedBytesForCurrentThread();
                var captured = files.CaptureFile(workspace, "source.bin", length, progress: reports.Add);
                AssertTrue(GC.GetAllocatedBytesForCurrentThread() - allocationStart < 16L * 1024 * 1024,
                    "80 MiB retention must not allocate a whole source body");
                AssertEqual(length, captured.ByteLength, "large source length");
                AssertEqual(expectedHash, captured.ContentSha256, "full source hash");
                foreach (var stage in new[] { FileTransferStage.Hashing, FileTransferStage.Retaining, FileTransferStage.Verifying })
                    AssertTrue(reports.Any(item => item.Stage == stage && item.CompletedBytes == length && item.TotalBytes == length),
                        "completed byte progress for " + stage);
                AssertTrue(reports.All(item => item.CompletedBytes >= 0 && item.CompletedBytes <= item.TotalBytes), "real per-pass counts");
                using (var content = files.OpenExactRead(workspace, "source.bin", captured.Reference, length))
                {
                    AssertTrue(content.CanSeek && !content.CanWrite, "raw source supports read-only random access");
                    content.Seek(-1, SeekOrigin.End);
                    AssertEqual((int)((length - 1) % 251), content.ReadByte(), "read final byte without preceding array");
                    content.Seek(1234567, SeekOrigin.Begin);
                    AssertEqual(1234567 % 251, content.ReadByte(), "read selected source page");
                    content.Position = 0;
                    using (var sha = SHA256.Create())
                        AssertEqual(expectedHash, HexSourceHash(sha.ComputeHash(content)), "streamed full read");
                }
                var same = files.CaptureFile(workspace, "source.bin", length);
                AssertEqual(captured.Reference.Revision, same.Reference.Revision, "same bytes do not advance authority");
                using (var edited = new FileStream(path, FileMode.Open, FileAccess.Write)) edited.WriteByte(255);
                var changed = files.CaptureFile(workspace, "source.bin", length);
                AssertTrue(changed.Reference.Revision != captured.Reference.Revision, "external edit advances exact source revision");
                AssertEqual(EvidenceState.Superseded,
                    new EvidenceStateReducer().Reduce(captured.Evidence, files.CaptureAuthority(new[] { captured.Evidence })).State,
                    "old source evidence is superseded");
                File.Delete(path);
                files = new WorkspaceFileService(paths);
                using (var old = files.OpenExactRead(workspace, "source.bin", captured.Reference, length))
                    AssertEqual(0, old.ReadByte(), "restart/deletion retain historical bytes");
                AssertEqual(changed.Reference.Revision,
                    files.CaptureAuthority(new[] { changed.Evidence }).Snapshots.Values.Single().GetHead(new ResourceIdentity(changed.Reference.Uri)).Revision.Revision,
                    "historical reading cannot republish a head");
            });
        }

        private static void WorkspaceSourceRefusalsAndCancellation()
        {
            WithTempPaths(paths =>
            {
                const long length = 2L * 1024 * 1024 + 17;
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "workspace"));
                var files = new WorkspaceFileService(paths);
                WriteSourceFixture(Path.Combine(workspace.RootPath, "source.bin"), length);
                foreach (var stage in new[] { FileTransferStage.Hashing, FileTransferStage.Retaining, FileTransferStage.Verifying })
                using (var cancellation = new CancellationTokenSource())
                {
                    var cancelled = false;
                    try
                    {
                        files.CaptureFile(workspace, "source.bin", length, cancellation.Token, item =>
                        { if (item.Stage == stage && item.CompletedBytes > 0) cancellation.Cancel(); });
                    }
                    catch (OperationCanceledException) { cancelled = true; }
                    AssertTrue(cancelled, "cancel " + stage);
                    AssertEqual(0, Directory.GetFiles(paths.ChatBlobDirectory, "*.blob", SearchOption.AllDirectories).Length,
                        "cancelled capture cannot retain/publish an incomplete source");
                    AssertEqual(0, Directory.GetFiles(paths.ChatBlobDirectory, "*.tmp", SearchOption.AllDirectories).Length,
                        "cancelled capture cleans temporary files");
                }
                RejectSource("file_too_large", () => files.CaptureFile(workspace, "source.bin", 1024));
                var source = files.CaptureFile(workspace, "source.bin", length);
                using (new ResourceMutationJournal(paths).AcquireScope(source.Evidence.ScopeId))
                using (var cancellation = new CancellationTokenSource())
                using (var started = new ManualResetEventSlim())
                {
                    files.FaultPoint = stage => { if (stage == "before-file-lease") started.Set(); };
                    var blocked = Task.Run(() =>
                    {
                        try { files.CaptureFile(workspace, "source.bin", length, cancellation.Token); return false; }
                        catch (OperationCanceledException) { return true; }
                    });
                    AssertTrue(started.Wait(2000), "source reaches busy file lease");
                    cancellation.Cancel();
                    AssertTrue(blocked.Wait(2000) && blocked.Result, "cancel does not wait for the busy file owner");
                    files.FaultPoint = null;
                }
                RejectSource("file_too_large", () => files.OpenExactRead(workspace, "source.bin", source.Reference, 1024));
                RejectSource("file_too_large", () => files.ReadExactBytes(workspace, "source.bin", source.Reference));
                var foreign = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "foreign"));
                RejectSource("invalid_resource_ref", () => files.OpenExactRead(foreign, "source.bin", source.Reference, length));
                var unpublished = new ResourceRef(source.Reference.Uri, "r_unpublished_large");
                var authority = new ResourceAuthorityStore(paths);
                authority.RegisterRevision(source.Evidence.ScopeId,
                    new ResourceRevisionMetadata(unpublished, source.ContentSha256, source.Evidence.Payload));
                authority.RegisterView(source.Evidence.ScopeId, new ResourceRevisionView(unpublished, "raw",
                    source.ContentSha256, source.Evidence.Payload, ResourceCoverage.Whole()));
                RejectSource("snapshot_unavailable", () => files.OpenExactRead(workspace, "source.bin", unpublished, length));
                using (var cancellation = new CancellationTokenSource())
                using (var stream = files.OpenExactRead(workspace, "source.bin", source.Reference, length, cancellation.Token))
                {
                    cancellation.Cancel();
                    var cancelled = false;
                    try { stream.ReadByte(); } catch (OperationCanceledException) { cancelled = true; }
                    AssertTrue(cancelled, "cancellation remains active while consuming the stream");
                }
                var blob = new ChatBlobStore(paths).PathFor(source.ContentSha256);
                using (var damaged = new FileStream(blob, FileMode.Open, FileAccess.Write))
                { damaged.Position = length - 1; damaged.WriteByte(255); }
                RejectSource("snapshot_unavailable", () => files.OpenExactRead(workspace, "source.bin", source.Reference, length));
                RejectSource("snapshot_unavailable", () => files.CaptureFile(workspace, "source.bin", length));
                File.Delete(blob);
                RejectSource("snapshot_unavailable", () => files.CaptureFile(workspace, "source.bin", length));
                AssertTrue(!File.Exists(blob), "live source cannot silently repair the retained version");
                File.WriteAllBytes(Path.Combine(workspace.RootPath, "empty.bin"), new byte[0]);
                var empty = files.CaptureFile(workspace, "empty.bin", 1);
                using (var stream = files.OpenExactRead(workspace, "empty.bin", empty.Reference, 1))
                    AssertEqual(-1, stream.ReadByte(), "empty source is valid");
            });
        }

        private static void BlobStreamsPreserveEncodingAndProtection()
        {
            foreach (var encrypt in new[] { false, true })
            foreach (var compress in new[] { false, true })
            WithTempPaths(paths =>
            {
                var protector = encrypt ? new StorageProtector(HistoryIntegrityModes.Sha256,
                    HistoryEncryptionModes.Aes256CbcHmacSha256, "test-only-stream-key",
                    Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()) : StorageProtector.None;
                var store = new ChatBlobStore(paths, () => protector);
                var bytes = Encoding.UTF8.GetBytes(new string('ж', 1024 * 1024));
                var path = Path.Combine(paths.Root, "source.txt");
                File.WriteAllBytes(path, bytes);
                var reference = store.StoreFile(path, compress ? "text/plain" : "application/octet-stream");
                using (var stream = store.OpenVerifiedRead(reference, bytes.Length))
                using (var sha = SHA256.Create())
                {
                    AssertEqual(!compress && !encrypt, stream.CanSeek, "encoded content discloses sequential access");
                    AssertTrue(!stream.CanWrite, "read stream never exposes CAS mutation");
                    AssertEqual(reference.Sha256, HexSourceHash(sha.ComputeHash(stream)), "exact decoded bytes for each storage encoding");
                }
                using (var cancellation = new CancellationTokenSource())
                {
                    var cancelled = false;
                    try
                    {
                        store.OpenVerifiedRead(reference, bytes.Length, cancellation.Token, item =>
                        { if (item.CompletedBytes > 0) cancellation.Cancel(); });
                    }
                    catch (OperationCanceledException) { cancelled = true; }
                    AssertTrue(cancelled, "verification cancellation disposes every encoding stream");
                }
                File.Delete(store.PathFor(reference.Sha256));
                AssertEqual(0, Directory.GetFiles(paths.Root, "*.tmp", SearchOption.AllDirectories).Length,
                    "no plaintext spool or leaked temporary file");
            });
        }

        private static string WriteSourceFixture(string path, long length)
        {
            using (var output = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[81920];
                for (long position = 0; position < length;)
                {
                    var count = (int)Math.Min(buffer.Length, length - position);
                    for (var i = 0; i < count; i++) buffer[i] = (byte)((position + i) % 251);
                    output.Write(buffer, 0, count);
                    sha.TransformBlock(buffer, 0, count, buffer, 0);
                    position += count;
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return HexSourceHash(sha.Hash);
            }
        }

        private static string HexSourceHash(byte[] hash)
        { return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant(); }

        private static void RejectSource(string code, Action action)
        {
            try { action(); }
            catch (WorkspaceFileException error) { AssertEqual(code, error.Code, "source refusal"); return; }
            throw new InvalidOperationException("Expected source refusal " + code);
        }
    }
}
