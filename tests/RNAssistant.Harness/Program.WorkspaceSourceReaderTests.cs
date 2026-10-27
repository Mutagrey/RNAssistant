using System;
using RNAssistant.Application.Conversations;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void BlobSeekableReadsPreserveExactProtectedBlocks()
        {
            foreach (var encrypt in new[] { false, true })
            foreach (var compress in new[] { false, true })
            WithTempPaths(paths =>
            {
                var protector = encrypt ? new StorageProtector(HistoryIntegrityModes.Sha256,
                    HistoryEncryptionModes.Aes256CbcHmacSha256, "seek-block-test-key",
                    Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()) : StorageProtector.None;
                var store = new ChatBlobStore(paths, () => protector);
                const int block = 256 * 1024;
                const int length = 3 * block + 313;
                var file = Path.Combine(paths.Root, "source.dat");
                WriteSourceFixture(file, length);
                var reference = store.StoreFile(file, compress ? "text/plain" : "application/octet-stream");
                var reports = new List<FileTransferProgress>();
                var stream = store.OpenVerifiedRead(reference, length, progress: reports.Add, requireSeek: true);
                try
                {
                    AssertTrue(stream.CanSeek && !stream.CanWrite, "explicit seek read for every encoding");
                    AssertEqual(encrypt || compress, reports.Any(item => item.Stage == FileTransferStage.Staging),
                        "only sequential CAS encodings need scratch blocks");
                    if (encrypt || compress)
                        AssertEqual((long)length, reports.Last().CompletedBytes, "staging reports decoded bytes in its own pass");
                    foreach (var offset in new[] { length - 129, block - 63, 0, 2 * block - 37, 31 })
                    {
                        AssertEqual((long)offset, stream.Seek(offset, SeekOrigin.Begin), "arbitrary seek including backwards");
                        var buffer = new byte[129];
                        var read = 0;
                        while (read < buffer.Length) read += stream.Read(buffer, read, buffer.Length - read);
                        for (var i = 0; i < buffer.Length; i++)
                            AssertEqual((byte)((offset + i) % 251), buffer[i], "exact bytes across protected block boundaries");
                    }
                    AssertEqual((long)length, stream.Seek(0, SeekOrigin.End), "seek to end");
                    AssertEqual(0, stream.Read(new byte[1], 0, 1), "EOF is explicit");
                    try { stream.Seek(1, SeekOrigin.End); throw new Exception("Expected range refusal"); }
                    catch (ArgumentOutOfRangeException) { }
                }
                finally { stream.Dispose(); }
                AssertEqual(0, Directory.GetFiles(paths.Root, "*.tmp", SearchOption.AllDirectories).Length, "scratch removed on close");

                if (encrypt || compress)
                {
                    using (var cancelled = new CancellationTokenSource())
                    {
                        try
                        {
                            store.OpenVerifiedRead(reference, length, cancelled.Token, item => {
                                if (item.Stage == FileTransferStage.Staging && item.CompletedBytes > 0) cancelled.Cancel();
                            }, requireSeek: true);
                            throw new Exception("Expected staging cancellation");
                        }
                        catch (OperationCanceledException) { }
                    }
                    AssertEqual(0, Directory.GetFiles(paths.Root, "*.tmp", SearchOption.AllDirectories).Length, "cancelled scratch removed");
                    try
                    {
                        store.OpenVerifiedRead(reference, length, progress: item => {
                            if (item.Stage == FileTransferStage.Staging) throw new InvalidOperationException("progress consumer failed");
                        }, requireSeek: true);
                        throw new Exception("Expected failed staging");
                    }
                    catch (InvalidOperationException error) { AssertEqual("progress consumer failed", error.Message, "callback failure propagated"); }
                    AssertEqual(0, Directory.GetFiles(paths.Root, "*.tmp", SearchOption.AllDirectories).Length, "failed staging removed");
                }
                using (File.Open(store.PathFor(reference.Sha256), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            });
        }

        private static void WorkspaceSourceReaderKeepsSelectionAndBounds()
        {
            WithTempPaths(paths =>
            {
                using (var app = new WorkspaceApplicationService(paths))
                using (var cancellation = new CancellationTokenSource())
                {
                    var state = CreateSourceQueueProject(app, paths);
                    var file = Path.Combine(state.Workspace.RootPath, "source.bin");
                    const int length = 256 * 1024 + 97;
                    WriteSourceFixture(file, length);
                    var queue = app.PrepareSources(SourceQueueRequest(state, new List<string> { "source.bin" }));
                    queue = WaitSourceQueue(app, queue.QueueId, page => page.State == WorkspaceSourceQueueState.Finished);
                    File.WriteAllText(file, "changed live bytes");
                    var callerThread = Thread.CurrentThread.ManagedThreadId;
                    var reader = app.OpenPreparedSourceAsync(queue.QueueId, queue.Items[0].Id, cancellation.Token,
                        progress => AssertTrue(Thread.CurrentThread.ManagedThreadId != callerThread, "source verification is off caller thread"))
                        .GetAwaiter().GetResult();
                    try
                    {
                        AssertEqual("source.bin", reader.Label, "source label without authority locator");
                        AssertEqual((long)length, reader.ByteLength, "exact retained length");
                        AssertTrue(ConversationLeaseService.ForOffice(paths).AnyHeld(), "open source holds maintenance activity after queue finishes");
                        ExpectSourceQueueError("source_queue_active", app.Reset);
                        ExpectSourceQueueError("source_queue_active", () => app.SaveTabularProjectSources(new SaveTabularProjectSourcesRequest {
                            BindingId = state.Workspace.BindingId, ProjectRevision = state.Revision,
                            ManifestPath = state.Project.ManifestPath, Paths = Array.Empty<string>() }));
                        ExpectSourceQueueError("source_reader_active", () => app.OpenPreparedSourceAsync(queue.QueueId, queue.Items[0].Id));
                        ExpectSourceQueueError("source_range_invalid", () => reader.ReadRangeAsync(-1, 1));
                        ExpectSourceQueueError("source_range_invalid", () => reader.ReadRangeAsync(0, 0));
                        ExpectSourceQueueError("source_range_invalid", () => reader.ReadRangeAsync(length - 1, 2));
                        ExpectSourceQueueError("source_range_invalid", () => reader.ReadRangeAsync(long.MaxValue, 1));
                        ExpectSourceQueueError("source_range_invalid", () => reader.ReadRangeAsync(0, WorkspaceSourceReader.MaximumReadBytes + 1));
                        var bytes = reader.ReadRangeAsync(length - 128, 128).GetAwaiter().GetResult();
                        for (var i = 0; i < bytes.Length; i++) AssertEqual((byte)((length - 128 + i) % 251), bytes[i], "retained bytes across a block boundary");
                        bytes[0] = 255;
                        AssertEqual((byte)((length - 128) % 251), reader.ReadRangeAsync(length - 128, 1).GetAwaiter().GetResult()[0], "consumer owns an isolated result buffer");
                        long remaining = length * 3L - 129;
                        while (remaining > 0)
                        {
                            var count = (int)Math.Min(WorkspaceSourceReader.MaximumReadBytes, remaining);
                            reader.ReadRangeAsync(0, count).GetAwaiter().GetResult();
                            remaining -= count;
                        }
                        ExpectSourceQueueError("source_read_limit", () => reader.ReadRangeAsync(0, 1));
                        cancellation.Cancel();
                        AssertTrue(reader.IsClosed, "caller cancellation revokes access synchronously");
                        reader.Drained.GetAwaiter().GetResult();
                        AssertTrue(!ConversationLeaseService.ForOffice(paths).AnyHeld(), "cancellation releases maintenance after drain");
                        app.EnsureNoSourceWork();
                    }
                    finally { reader.CloseAsync().GetAwaiter().GetResult(); }
                    File.AppendAllText(Path.Combine(state.Workspace.RootPath, "project.json"), " ");
                    try { app.OpenPreparedSourceAsync(queue.QueueId, queue.Items[0].Id).GetAwaiter().GetResult(); throw new Exception("Expected project drift refusal"); }
                    catch (TabularProjectException error) { AssertEqual("project_changed", error.Code, "source open checks exact selected manifest"); }
                    app.EnsureNoSourceWork();
                    AssertTrue(!ConversationLeaseService.ForOffice(paths).AnyHeld(), "failed open drains owners");
                }
            });
        }

        private static void WorkspaceSourceReaderRevokesBeforeIoDrains()
        {
            WithTempPaths(paths =>
            {
                using (var entered = new ManualResetEventSlim())
                using (var release = new ManualResetEventSlim())
                using (var cancellation = new CancellationTokenSource())
                {
                    var reader = new WorkspaceSourceReader(32, "blocked.bin");
                    var source = new BlockedSourceReadStream(entered, release);
                    reader.OpenAsync(paths, token => source, cancellation.Token).GetAwaiter().GetResult();
                    var read = reader.ReadRangeAsync(0, 16);
                    try
                    {
                        AssertTrue(entered.Wait(5000), "background range read started");
                        ExpectSourceQueueError("source_read_pending", () => reader.ReadRangeAsync(0, 1));
                        cancellation.Cancel();
                        AssertTrue(reader.IsClosed && !reader.Drained.IsCompleted, "revoked now; handles held until in-flight I/O settles");
                        AssertTrue(ConversationLeaseService.ForOffice(paths).AnyHeld(), "maintenance remains excluded while read is draining");
                        try { reader.ReadRangeAsync(0, 1); throw new Exception("Expected closed source"); }
                        catch (ObjectDisposedException) { }
                    }
                    finally { release.Set(); }
                    try { read.GetAwaiter().GetResult(); throw new Exception("Expected cancelled result"); }
                    catch (OperationCanceledException) { }
                    reader.CloseAsync().GetAwaiter().GetResult();
                    AssertTrue(source.WasDisposed, "native stream closed after late read");
                    AssertTrue(!ConversationLeaseService.ForOffice(paths).AnyHeld(), "drained read releases maintenance");
                    using (RuntimeStorageActivity.AcquireSourceReader(paths, CancellationToken.None)) { }
                }

                using (var occupied = RuntimeStorageActivity.AcquireSourceReader(paths, CancellationToken.None))
                using (var cancellation = new CancellationTokenSource())
                {
                    var opened = false;
                    var reader = new WorkspaceSourceReader(32, "waiting.bin");
                    var opening = reader.OpenAsync(paths, token => { opened = true; return new MemoryStream(new byte[32]); }, cancellation.Token);
                    AssertTrue(SpinWait.SpinUntil(() => ConversationLeaseService.ForOffice(paths).AnyHeld(), 5000), "waiting reader participates in maintenance");
                    cancellation.Cancel();
                    try { opening.GetAwaiter().GetResult(); throw new Exception("Expected cancelled admission"); }
                    catch (OperationCanceledException) { }
                    reader.Drained.GetAwaiter().GetResult();
                    AssertTrue(!opened, "cancelled slot wait never opens the source");
                }
            });
        }

        private static void WorkspaceSourceReaderApplicationCloseDrains()
        {
            foreach (var close in new[] { false, true })
            WithTempPaths(paths =>
            {
                using (var app = new WorkspaceApplicationService(paths))
                using (var entered = new ManualResetEventSlim())
                using (var release = new ManualResetEventSlim())
                {
                    var state = CreateSourceQueueProject(app, paths);
                    File.WriteAllText(Path.Combine(state.Workspace.RootPath, "source.csv"), "year,value\n2026,42");
                    var queue = app.PrepareSources(SourceQueueRequest(state, new List<string> { "source.csv" }));
                    queue = WaitSourceQueue(app, queue.QueueId, page => page.State == WorkspaceSourceQueueState.Finished);
                    var opening = app.OpenPreparedSourceAsync(queue.QueueId, queue.Items[0].Id, progress: item => {
                        if (entered.IsSet) return;
                        entered.Set(); AssertTrue(release.Wait(5000), "release pending source verification");
                    });
                    Task drained = null;
                    try
                    {
                        AssertTrue(entered.Wait(5000), "source open reaches verification off application lock");
                        ExpectSourceQueueError("source_queue_active", app.Reset);
                        AssertTrue(ConversationLeaseService.ForOffice(paths).AnyHeld(), "pending open holds storage activity");
                        if (close)
                        {
                            drained = app.CloseAsync();
                            AssertTrue(!drained.IsCompleted, "application close waits for pending verification");
                        }
                        else File.AppendAllText(Path.Combine(state.Workspace.RootPath, "project.json"), " ");
                    }
                    finally { release.Set(); }
                    try { opening.GetAwaiter().GetResult(); throw new Exception("Expected rejected source open"); }
                    catch (OperationCanceledException) { AssertTrue(close, "closing application cancels pending open"); }
                    catch (TabularProjectException error)
                    { AssertTrue(!close, "project drift is distinct from cancellation"); AssertEqual("project_changed", error.Code, "project checked again after verification"); }
                    (drained ?? app.CloseAsync()).GetAwaiter().GetResult();
                    AssertTrue(!ConversationLeaseService.ForOffice(paths).AnyHeld(), "failed/cancelled open drains all owners");
                    using (RuntimeStorageActivity.AcquireSourceReader(paths, CancellationToken.None)) { }
                }
            });
        }

        private sealed class BlockedSourceReadStream : MemoryStream
        {
            private readonly ManualResetEventSlim _entered, _release;
            internal bool WasDisposed;
            internal BlockedSourceReadStream(ManualResetEventSlim entered, ManualResetEventSlim release)
                : base(new byte[32], false) { _entered = entered; _release = release; }
            public override int Read(byte[] buffer, int offset, int count)
            { _entered.Set(); AssertTrue(_release.Wait(5000), "release blocked source I/O"); return base.Read(buffer, offset, count); }
            protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
        }
    }
}
