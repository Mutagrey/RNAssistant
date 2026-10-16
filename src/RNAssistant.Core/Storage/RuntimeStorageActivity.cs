using System;
using System.IO;
using System.Threading;

namespace RNAssistant.Core.Storage
{
    // A background storage operation participates in the existing run-registry
    // maintenance protocol without manufacturing a chat or a model run.
    public static class RuntimeStorageActivity
    {
        // All application instances share one heavy source reader. Do not hold
        // the maintenance coordination lock while waiting for this slot.
        public static IDisposable AcquireSourceReader(AppDataPaths paths, CancellationToken token)
        {
            var directory = Path.Combine(paths.Root, "locks");
            StorageFileSystem.EnsureRegularDirectory(directory);
            var path = Path.Combine(directory, "source-reader.lck");
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException)
                {
                    if (DateTime.UtcNow >= deadline)
                        throw new WorkspaceFileException("source_reader_busy", "Не удалось получить доступ к обработчику источников. Повторите после завершения другой очереди.");
                    // A short, cancellable wait; no opened workbook or body buffer.
                    if (token.CanBeCanceled) token.WaitHandle.WaitOne(25);
                    else Thread.Sleep(25);
                }
            }
        }

        public static IDisposable Acquire(AppDataPaths paths, CancellationToken token = default(CancellationToken))
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            var directory = Path.Combine(paths.Root, "locks");
            StorageFileSystem.EnsureRegularDirectory(directory);
            using (StorageFileSystem.AcquireWriteLock(Path.Combine(directory, "run-registry.lck"), token))
            {
                token.ThrowIfCancellationRequested();
                var path = Path.Combine(directory, "run_storage_" + Guid.NewGuid().ToString("N") + ".lck");
                return new Activity(path, new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None));
            }
        }

        private sealed class Activity : IDisposable
        {
            private readonly string _path;
            private FileStream _stream;
            internal Activity(string path, FileStream stream) { _path = path; _stream = stream; }
            public void Dispose()
            {
                var stream = Interlocked.Exchange(ref _stream, null);
                if (stream == null) return;
                stream.Dispose();
                // A released lock is not an active operation, even if cleanup fails.
                try { File.Delete(_path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
