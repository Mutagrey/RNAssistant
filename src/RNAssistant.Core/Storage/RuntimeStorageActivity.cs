using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace RNAssistant.Core.Storage
{
    // A background storage operation participates in the existing run-registry
    // maintenance protocol without manufacturing a chat or a model run.
    public static class RuntimeStorageActivity
    {
        private static readonly ThreadLocal<Dictionary<string, CoordinationState>> CoordinationStates =
            new ThreadLocal<Dictionary<string, CoordinationState>>(() => new Dictionary<string, CoordinationState>(
                Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal));

        // One synchronous owner for run reservation, maintenance and nested
        // journal/CAS work. Reentry never flows into another worker or task.
        public static IDisposable AcquireCoordination(AppDataPaths paths, CancellationToken token = default(CancellationToken))
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            token.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Combine(paths.Root, "locks", "run-registry.lck"));
            var states = CoordinationStates.Value;
            lock (states)
            {
                CoordinationState current;
                if (states.TryGetValue(path, out current))
                {
                    current.Depth++;
                    return new CoordinationLease(states, path, current);
                }
            }
            StorageFileSystem.EnsureRegularDirectory(Path.GetDirectoryName(path));
            if (File.Exists(path) && StorageFileSystem.IsReparsePoint(path))
                throw new IOException("Conversation coordination lease cannot be a link.");
            var state = new CoordinationState { Depth = 1, Gate = StorageFileSystem.AcquireWriteLock(path, token) };
            lock (states) states.Add(path, state);
            return new CoordinationLease(states, path, state);
        }

        private sealed class CoordinationState
        {
            internal int Depth;
            internal IDisposable Gate;
        }

        private sealed class CoordinationLease : IDisposable
        {
            private readonly Dictionary<string, CoordinationState> _states;
            private readonly string _path;
            private readonly CoordinationState _state;
            private int _disposed;
            internal CoordinationLease(Dictionary<string, CoordinationState> states, string path, CoordinationState state)
            { _states = states; _path = path; _state = state; }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                lock (_states)
                {
                    if (--_state.Depth != 0) return;
                    _states.Remove(_path);
                    _state.Gate.Dispose();
                }
            }
        }

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
            using (AcquireCoordination(paths, token))
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
