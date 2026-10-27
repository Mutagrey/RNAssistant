using System;
using System.IO;
using System.Threading;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Application.Conversations
{
    // One application mechanism for exact conversation ownership. The environment
    // chooses its existing lease path; this service never changes the lock namespace.
    internal sealed class ConversationLeaseService
    {
        private readonly string _directory;
        private readonly Func<string, string> _fileName;
        private readonly string _coordinationFile;
        private readonly ThreadLocal<CoordinationLockState> _coordinationState =
            new ThreadLocal<CoordinationLockState>();

        private ConversationLeaseService(string directory, Func<string, string> fileName, string coordinationFile)
        { _directory = directory; _fileName = fileName; _coordinationFile = coordinationFile; }

        internal static ConversationLeaseService ForOffice(AppDataPaths paths)
        {
            return new ConversationLeaseService(paths == null ? null : Path.Combine(paths.Root, "locks"),
                id => "run_" + AppDataPaths.SafeFileName(id) + ".lck", "run-registry.lck");
        }

        internal static ConversationLeaseService ForWorkspace(AppDataPaths paths)
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            return new ConversationLeaseService(Path.Combine(paths.WorkspaceDirectory, "run-leases"),
                id => TextPatternEngine.Sha256(id) + ".lck", null);
        }

        // Short coordination excludes a concurrent Office-wide maintenance
        // operation while acquiring an exact chat lease. Workspace has no
        // Office-wide maintenance gate and uses only the exact lease.
        internal IDisposable Reserve(string conversationId, bool wait = false,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            using (AcquireMaintenance()) return AcquireExact(conversationId, wait, cancellationToken);
        }

        internal IDisposable AcquireMaintenance()
        {
            if (_directory == null || _coordinationFile == null) return null;
            var current = _coordinationState.Value;
            if (current != null)
            {
                current.Depth++;
                return new CoordinationLockLease(this, current);
            }
            StorageFileSystem.EnsureRegularDirectory(_directory);
            var path = Path.Combine(_directory, _coordinationFile);
            if (File.Exists(path) && StorageFileSystem.IsReparsePoint(path))
                throw new IOException("Conversation coordination lease cannot be a link.");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                try
                {
                    var state = new CoordinationLockState
                    {
                        Depth = 1,
                        Stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
                    };
                    _coordinationState.Value = state;
                    return new CoordinationLockLease(this, state);
                }
                catch (IOException)
                {
                    if (DateTime.UtcNow >= deadline)
                        throw new InvalidOperationException("Не удалось начать операцию: другое окно RNAssistant обновляет состояние запусков.");
                    Thread.Sleep(25);
                }
                catch (UnauthorizedAccessException ex)
                { throw new InvalidOperationException("Не удалось получить системную блокировку RNAssistant.", ex); }
            }
        }

        private void ReleaseMaintenance(CoordinationLockState state)
        {
            if (state == null || state.Depth <= 0) return;
            if (--state.Depth > 0) return;
            if (ReferenceEquals(_coordinationState.Value, state)) _coordinationState.Value = null;
            state.Stream.Dispose();
        }

        private sealed class CoordinationLockState
        {
            internal int Depth;
            internal FileStream Stream;
        }

        private sealed class CoordinationLockLease : IDisposable
        {
            private readonly ConversationLeaseService _owner;
            private readonly CoordinationLockState _state;
            private int _disposed;

            internal CoordinationLockLease(ConversationLeaseService owner, CoordinationLockState state)
            { _owner = owner; _state = state; }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) _owner.ReleaseMaintenance(_state);
            }
        }

        private IDisposable AcquireExact(string conversationId, bool wait = false,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(conversationId))
                throw new ArgumentException("A conversation ID is required.", nameof(conversationId));
            if (_directory == null) return null;
            StorageFileSystem.EnsureRegularDirectory(_directory);
            var path = Path.Combine(_directory, _fileName(conversationId));
            if (File.Exists(path) && StorageFileSystem.IsReparsePoint(path))
                throw new IOException("Conversation lease cannot be a link.");
            cancellationToken.ThrowIfCancellationRequested();
            return wait ? StorageFileSystem.AcquireWriteLock(path, cancellationToken) :
                new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        internal bool IsHeld(string conversationId)
        {
            if (_directory == null) return false;
            try { using (AcquireExact(conversationId)) return false; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        internal bool AnyHeld(string pattern)
        {
            if (_directory == null || !Directory.Exists(_directory)) return false;
            string[] paths;
            try { paths = Directory.GetFiles(_directory, pattern); }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
            foreach (var path in paths)
            {
                try { using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { } }
                catch (IOException) { return true; }
                catch (UnauthorizedAccessException) { return true; }
            }
            return false;
        }
    }
}
