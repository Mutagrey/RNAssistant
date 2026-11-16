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
        private readonly AppDataPaths _coordinationPaths;
        private readonly string _leasePattern;

        private ConversationLeaseService(string directory, Func<string, string> fileName,
            AppDataPaths coordinationPaths, string leasePattern)
        { _directory = directory; _fileName = fileName; _coordinationPaths = coordinationPaths;
            _leasePattern = leasePattern; }

        internal static ConversationLeaseService ForOffice(AppDataPaths paths)
        {
            return new ConversationLeaseService(paths == null ? null : Path.Combine(paths.Root, "locks"),
                id => "run_" + AppDataPaths.SafeFileName(id) + ".lck", paths, "run_*.lck");
        }

        internal static ConversationLeaseService ForWorkspace(AppDataPaths paths)
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            return new ConversationLeaseService(Path.Combine(paths.WorkspaceDirectory, "run-leases"),
                id => TextPatternEngine.Sha256(id) + ".lck", null, "*.lck");
        }

        // New-generation consumers in either host use the same exact scope/chat
        // namespace. Existing binaries retain their old paths until cutover.
        internal static ConversationLeaseService ForScope(AppDataPaths paths, string scope)
        {
            if (paths == null || string.IsNullOrWhiteSpace(scope) ||
                !(scope.StartsWith("document:", StringComparison.Ordinal) || scope.StartsWith("workspace:", StringComparison.Ordinal)) || scope.EndsWith(":", StringComparison.Ordinal))
                throw new ArgumentException("An exact document/workspace scope is required.");
            return new ConversationLeaseService(Path.Combine(paths.Root, "locks"),
                id => "run_scope_" + TextPatternEngine.Sha256(scope + "\n" + id) + ".lck", paths, "run_*.lck");
        }

        // Short coordination excludes a concurrent Office-wide maintenance
        // operation while acquiring an exact chat lease. Workspace has no
        // Office-wide maintenance gate and uses only the exact lease.
        internal IDisposable Reserve(string conversationId, bool wait = false,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var path = ExactLeasePath(conversationId);
            if (path == null) return null;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                using (AcquireMaintenance(cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                    catch (IOException) { if (!wait || DateTime.UtcNow >= deadline) throw; }
                }
                // The owner of this exact run may need the registry to publish
                // its next journal fact before it can release the run lease.
                if (cancellationToken.CanBeCanceled) cancellationToken.WaitHandle.WaitOne(20);
                else Thread.Sleep(20);
            }
        }

        internal IDisposable AcquireMaintenance() { return AcquireMaintenance(CancellationToken.None); }

        private IDisposable AcquireMaintenance(CancellationToken cancellationToken)
        {
            if (_coordinationPaths == null) return null;
            try { return RuntimeStorageActivity.AcquireCoordination(_coordinationPaths, cancellationToken); }
            catch (IOException ex)
            { throw new InvalidOperationException("Не удалось начать операцию: другое окно RNAssistant обновляет состояние запусков.", ex); }
            catch (UnauthorizedAccessException ex)
            { throw new InvalidOperationException("Не удалось получить системную блокировку RNAssistant.", ex); }
        }

        private string ExactLeasePath(string conversationId)
        {
            if (string.IsNullOrWhiteSpace(conversationId))
                throw new ArgumentException("A conversation ID is required.", nameof(conversationId));
            if (_directory == null) return null;
            StorageFileSystem.EnsureRegularDirectory(_directory);
            var path = Path.Combine(_directory, _fileName(conversationId));
            if (File.Exists(path) && StorageFileSystem.IsReparsePoint(path))
                throw new IOException("Conversation lease cannot be a link.");
            return path;
        }

        internal bool IsHeld(string conversationId)
        {
            if (_directory == null) return false;
            try { using (new FileStream(ExactLeasePath(conversationId), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) return false; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        internal bool AnyHeld()
        {
            if (_directory == null || !Directory.Exists(_directory)) return false;
            string[] paths;
            try { paths = Directory.GetFiles(_directory, _leasePattern); }
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
