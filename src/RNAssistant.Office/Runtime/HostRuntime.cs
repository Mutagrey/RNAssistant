using System;
using System.IO;
using System.Threading;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Runtime
{
    // Owns the current synchronous document-access boundary. Bound document objects,
    // runtime-identity gates and preparation under that gate are the Phase 5B switch.
    internal sealed class HostRuntime
    {
        private static readonly TimeSpan MutationLockTimeout = TimeSpan.FromSeconds(10);
        private static readonly object FallbackMutationGate = new object();
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly string _mutationLockDirectory;
        private readonly AsyncLocal<int> _documentAccessDepth = new AsyncLocal<int>();

        internal HostRuntime(IOfficeApplicationAdapter adapter, AppDataPaths paths)
        {
            _adapter = adapter;
            _mutationLockDirectory = paths == null ? null : Path.Combine(paths.Root, "locks");
        }

        internal ToolResult ExecuteForExpectedDocument(
            OfficeDocumentExecutionExpectation target,
            bool requiresOfficeDocument,
            Func<ToolResult> action)
        {
            if (!requiresOfficeDocument) return action();
            var expectation = target == null ||
                string.IsNullOrWhiteSpace(target.Host) ||
                (string.IsNullOrWhiteSpace(target.DocumentKey) && string.IsNullOrWhiteSpace(target.RuntimeDocumentKey))
                ? null
                : target;
            var documentGuard = _adapter as IOfficeDocumentExecutionGuard;
            if (expectation != null && documentGuard == null)
            {
                var mismatch = OfficeDocumentExecutionGuardState.Validate(_adapter, expectation);
                if (mismatch != null) return mismatch;
            }

            using (documentGuard == null || expectation == null
                ? null
                : documentGuard.BeginExpectedDocument(
                    expectation.Host,
                    expectation.DocumentKey,
                    expectation.RuntimeDocumentKey))
            {
                return action();
            }
        }

        internal ToolResult ExecuteMutation(
            OfficeDocumentExecutionExpectation target,
            bool mutatesSharedLocalState,
            bool mutatesDocument,
            CancellationToken cancellationToken,
            Func<ToolResult> action)
        {
            var actionStarted = false;
            try
            {
                if (!mutatesSharedLocalState && !mutatesDocument)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    actionStarted = true;
                    return action();
                }
                if (string.IsNullOrWhiteSpace(_mutationLockDirectory))
                {
                    EnterMutationGate(FallbackMutationGate, cancellationToken);
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        actionStarted = true;
                        return InDocumentAccessScope(mutatesDocument, action);
                    }
                    finally
                    {
                        Monitor.Exit(FallbackMutationGate);
                    }
                }

                using (AcquireMutationFileLock(mutatesSharedLocalState ? "local_state" : null, cancellationToken))
                using (AcquireMutationFileLock(
                    mutatesDocument ? "document_" + AppDataPaths.SafeFileName(DocumentMutationKey(target)) : null,
                    cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    actionStarted = true;
                    return InDocumentAccessScope(mutatesDocument, action);
                }
            }
            catch (OperationCanceledException) when (actionStarted)
            {
                return ToolResult.Fail(
                    "Cancellation was observed after mutation execution started. The external effect may have been applied; inspect state before retrying.",
                    null,
                    "tool_effect_uncertain",
                    false);
            }
        }

        internal IDisposable BeginDocumentAccess(OfficeDocumentExecutionExpectation target)
        {
            if (_documentAccessDepth.Value > 0) return new ActionLease(null);
            IDisposable lockLease;
            if (string.IsNullOrWhiteSpace(_mutationLockDirectory))
            {
                EnterMutationGate(FallbackMutationGate, CancellationToken.None);
                lockLease = new ActionLease(delegate { Monitor.Exit(FallbackMutationGate); });
            }
            else
            {
                lockLease = AcquireMutationFileLock(
                    "document_" + AppDataPaths.SafeFileName(DocumentMutationKey(target)),
                    CancellationToken.None);
            }
            _documentAccessDepth.Value += 1;
            return new ActionLease(delegate
            {
                _documentAccessDepth.Value = Math.Max(0, _documentAccessDepth.Value - 1);
                if (lockLease != null) lockLease.Dispose();
            });
        }

        private ToolResult InDocumentAccessScope(bool enabled, Func<ToolResult> action)
        {
            if (!enabled) return action();
            _documentAccessDepth.Value += 1;
            try
            {
                return action();
            }
            finally
            {
                _documentAccessDepth.Value = Math.Max(0, _documentAccessDepth.Value - 1);
            }
        }

        private static void EnterMutationGate(object gate, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow.Add(MutationLockTimeout);
            while (!Monitor.TryEnter(gate, 100))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTime.UtcNow >= deadline)
                {
                    throw new MutationLockException(
                        "Another RNAssistant action is still changing the same state. Retry after it finishes.",
                        true);
                }
            }
        }

        private string DocumentMutationKey(OfficeDocumentExecutionExpectation target)
        {
            return target == null
                ? (_adapter.HostName + "|" + _adapter.DocumentKey)
                : ((target.Host ?? _adapter.HostName) + "|" + (target.DocumentKey ?? _adapter.DocumentKey));
        }

        private IDisposable AcquireMutationFileLock(string lockName, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_mutationLockDirectory) || string.IsNullOrWhiteSpace(lockName)) return null;
            try
            {
                Directory.CreateDirectory(_mutationLockDirectory);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new MutationLockException("RNAssistant cannot access its mutation lock directory.", false, ex);
            }
            catch (IOException ex)
            {
                throw new MutationLockException("RNAssistant cannot access its mutation lock directory.", false, ex);
            }
            var path = Path.Combine(_mutationLockDirectory, lockName + ".lck");
            var deadline = DateTime.UtcNow.Add(MutationLockTimeout);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException ex)
                {
                    if (DateTime.UtcNow >= deadline)
                    {
                        throw new MutationLockException(
                            "Another RNAssistant action is still changing the same state. Retry after it finishes.",
                            true,
                            ex);
                    }
                    if (cancellationToken.WaitHandle.WaitOne(100)) cancellationToken.ThrowIfCancellationRequested();
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw new MutationLockException("RNAssistant cannot acquire its mutation lock.", false, ex);
                }
            }
        }

        internal sealed class MutationLockException : InvalidOperationException
        {
            internal MutationLockException(string message, bool retryable, Exception innerException = null)
                : base(message, innerException)
            {
                Retryable = retryable;
            }

            public bool Retryable { get; private set; }
        }

        private sealed class ActionLease : IDisposable
        {
            private Action _dispose;

            public ActionLease(Action dispose)
            {
                _dispose = dispose;
            }

            public void Dispose()
            {
                var dispose = Interlocked.Exchange(ref _dispose, null);
                if (dispose != null) dispose();
            }
        }
    }
}
