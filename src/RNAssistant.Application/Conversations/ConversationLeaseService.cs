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

        private ConversationLeaseService(string directory, Func<string, string> fileName)
        { _directory = directory; _fileName = fileName; }

        internal static ConversationLeaseService ForOffice(AppDataPaths paths)
        {
            return new ConversationLeaseService(paths == null ? null : Path.Combine(paths.Root, "locks"),
                id => "run_" + AppDataPaths.SafeFileName(id) + ".lck");
        }

        internal static ConversationLeaseService ForWorkspace(AppDataPaths paths)
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            return new ConversationLeaseService(Path.Combine(paths.WorkspaceDirectory, "run-leases"),
                id => TextPatternEngine.Sha256(id) + ".lck");
        }

        internal IDisposable Acquire(string conversationId, bool wait = false,
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
            try { using (Acquire(conversationId)) return false; }
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
