using System;
using System.IO;
using System.Threading;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Runtime
{
    internal sealed class HostRuntime
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly string _mutationLockDirectory;

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
            return ExecuteForExpectedDocument(target, requiresOfficeDocument, CancellationToken.None, action);
        }

        internal ToolResult ExecuteForExpectedDocument(
            OfficeDocumentExecutionExpectation target,
            bool requiresOfficeDocument,
            CancellationToken cancellationToken,
            Func<ToolResult> action)
        {
            // A public execution entry is never reentrant merely because a UI callback
            // happened on the thread of a different, still-running operation.
            using (DocumentAccessGate.BeginOperation())
            {
                if (!requiresOfficeDocument) return action();
                try
                {
                    var access = CaptureAccess(target);
                    using (EnterAccess(access, cancellationToken))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return ExecuteGuarded(access, target, cancellationToken, action);
                    }
                }
                catch (OfficeDocumentGuardException ex)
                {
                    return ToolResult.Fail(ex.Message, null, ex.ErrorCode, ex.Retryable);
                }
                catch (MutationLockException ex)
                {
                    return LockFailure(ex);
                }
            }
        }

        internal ToolResult ExecuteMutation(
            OfficeDocumentExecutionExpectation target,
            bool mutatesSharedLocalState,
            bool mutatesDocument,
            CancellationToken cancellationToken,
            Func<ToolResult> action)
        {
            var access = mutatesDocument ? CaptureAccess(target) : null;
            // The outer document scope also covers preparation. Direct callers
            // take the same gate here, always before the short shared-state lock.
            using (access == null ? null : EnterAccess(access, cancellationToken))
            using (!mutatesSharedLocalState ? null : DocumentAccessGate.Enter(
                "shared_local_state|" + (_mutationLockDirectory ?? string.Empty), null,
                _mutationLockDirectory, "local_state", MayWait(access), cancellationToken, false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Func<ToolResult> execute = delegate
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try { return action(); }
                    catch (OperationCanceledException)
                    {
                        return ToolResult.Fail(
                            "Cancellation was observed after mutation execution started. The external effect may have been applied; inspect state before retrying.",
                            null, "tool_effect_uncertain", false);
                    }
                    catch (Exception ex) when (ex is MutationLockException || ex is OfficeDocumentGuardException)
                    {
                        // A nested read/guard may fail after a write. Do not let the outer
                        // access boundary misclassify that as a retryable pre-dispatch refusal.
                        return ToolResult.Fail(
                            "Document access failed after mutation execution started. The external effect may have been applied; inspect state before retrying. " + ex.Message,
                            null, "tool_effect_uncertain", false);
                    }
                };
                return access == null ? execute() : ExecuteGuarded(access, target, cancellationToken, execute);
            }
        }

        internal IDisposable BeginDocumentAccess(OfficeDocumentExecutionExpectation target)
        {
            var access = CaptureAccess(target);
            var lease = EnterAccess(access, CancellationToken.None);
            try
            {
                var mismatch = CheckTarget(access, target);
                if (mismatch != null) throw new OfficeDocumentGuardException(mismatch);
                return lease;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        internal T ReadDocument<T>(OfficeDocumentExecutionExpectation target, Func<T> action)
        {
            // UI/context/catalog reads are independent roots, including callbacks
            // reentered on an STA already executing another document operation.
            using (DocumentAccessGate.BeginOperation())
            {
                var provider = _adapter as IOfficeDocumentSessionProvider;
                if (target == null && (provider == null || provider.DocumentSession == null))
                {
                    target = new OfficeDocumentExecutionExpectation
                    {
                        Host = _adapter.HostName,
                        DocumentKey = _adapter.DocumentKey,
                        RuntimeDocumentKey = _adapter.RuntimeDocumentKey
                    };
                }
                var access = CaptureAccess(target);
                using (EnterAccess(access, CancellationToken.None))
                {
                    return ExecuteGuarded<T>(access, target, CancellationToken.None, action);
                }
            }
        }

        private ToolResult ExecuteGuarded(DocumentAccess access,
            OfficeDocumentExecutionExpectation target, CancellationToken cancellationToken, Func<ToolResult> action)
        {
            try { return ExecuteGuarded<ToolResult>(access, target, cancellationToken, action); }
            catch (OfficeDocumentGuardException ex)
            {
                return ToolResult.Fail(ex.Message, null, ex.ErrorCode, ex.Retryable);
            }
        }

        private T ExecuteGuarded<T>(DocumentAccess access,
            OfficeDocumentExecutionExpectation target, CancellationToken cancellationToken, Func<T> action)
        {
            Func<T> guarded = delegate
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mismatch = CheckTarget(access, target);
                if (mismatch != null) throw new OfficeDocumentGuardException(mismatch);
                var expectation = HasExpectation(target) ? target : access.Session == null ? null :
                    new OfficeDocumentExecutionExpectation
                    {
                        Host = access.Session.Host,
                        DocumentKey = access.Session.StableDocumentId,
                        RuntimeDocumentKey = access.Session.RuntimeDocumentId
                    };
                var guard = _adapter as IOfficeDocumentExecutionGuard;
                using (guard == null || expectation == null ? null : guard.BeginExpectedDocument(
                    expectation.Host, expectation.DocumentKey, expectation.RuntimeDocumentKey))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return action();
                }
            };
            // A bound host operation stays on its owner STA. Legacy adapters keep
            // their current per-access dispatcher until the Excel identity switch.
            return access.Session == null ? guarded() :
                DocumentAccessGate.Invoke(access.Dispatcher, guarded);
        }

        private ToolResult CheckTarget(DocumentAccess access, OfficeDocumentExecutionExpectation target)
        {
            if (access.Session != null)
            {
                return DocumentAccessGate.Invoke(access.Dispatcher,
                    () => OfficeDocumentExecutionGuardState.ValidateBoundSession(access.Session, target));
            }
            return HasExpectation(target) ? OfficeDocumentExecutionGuardState.Validate(_adapter, target) : null;
        }

        private IDisposable EnterAccess(DocumentAccess access, CancellationToken cancellationToken)
        {
            return DocumentAccessGate.Enter(access.Key, access.Gate, _mutationLockDirectory,
                access.LockName, MayWait(access), cancellationToken);
        }

        private bool MayWait(DocumentAccess access)
        {
            var dispatcher = access == null ? null : access.Dispatcher;
            if (dispatcher == null)
            {
                var provider = _adapter as IOfficeDispatcherProvider;
                dispatcher = provider == null ? null : provider.StaDispatcher;
            }
            // Waiting on the owner STA can deadlock a worker that holds this gate
            // while waiting for its queued COM callback. Busy is explicit and retryable.
            return dispatcher == null || !dispatcher.CheckAccess;
        }

        private DocumentAccess CaptureAccess(OfficeDocumentExecutionExpectation target)
        {
            var provider = _adapter as IOfficeDocumentSessionProvider;
            var session = provider == null ? null : provider.DocumentSession;
            var dispatchProvider = _adapter as IOfficeDispatcherProvider;
            var dispatcher = session == null
                ? (dispatchProvider == null ? null : dispatchProvider.StaDispatcher)
                : session.StaDispatcher;
            if (session != null)
            {
                if (string.IsNullOrWhiteSpace(session.Host) || string.IsNullOrWhiteSpace(session.RuntimeDocumentId) ||
                    session.MutationGate == null || dispatcher == null)
                {
                    throw new OfficeDocumentGuardException(ToolResult.Fail(
                        "The bound Office document session is incomplete. No Office action was started.",
                        null, "document_session_unavailable", false));
                }
                var identity = session.Host.ToLowerInvariant() + "|" + session.RuntimeDocumentId;
                return new DocumentAccess("bound_document|" + identity,
                    "runtime_document_" + AppDataPaths.SafeFileName(identity),
                    session.MutationGate, session, dispatcher);
            }

            // Compatibility boundary for the not-yet-switched production hosts.
            // These keys do not identify a live workbook across lifetimes.
            var host = target == null ? _adapter.HostName : target.Host ?? _adapter.HostName;
            var document = target == null ? _adapter.DocumentKey : target.DocumentKey ?? _adapter.DocumentKey;
            var legacyIdentity = (host ?? string.Empty) + "|" + (document ?? string.Empty);
            return new DocumentAccess("legacy_document|" + legacyIdentity.ToLowerInvariant(),
                "document_" + AppDataPaths.SafeFileName(legacyIdentity), null, null, dispatcher);
        }

        private static bool HasExpectation(OfficeDocumentExecutionExpectation target)
        {
            return target != null && !string.IsNullOrWhiteSpace(target.Host) &&
                (!string.IsNullOrWhiteSpace(target.DocumentKey) || !string.IsNullOrWhiteSpace(target.RuntimeDocumentKey));
        }

        private static ToolResult LockFailure(MutationLockException exception)
        {
            return ToolResult.Fail(exception.Message, null,
                exception.Retryable ? "tool_mutation_busy" : "tool_mutation_lock_unavailable",
                exception.Retryable);
        }

        private sealed class DocumentAccess
        {
            internal readonly string Key;
            internal readonly string LockName;
            internal readonly object Gate;
            internal readonly IOfficeDocumentSession Session;
            internal readonly IOfficeStaDispatcher Dispatcher;

            internal DocumentAccess(string key, string lockName, object gate,
                IOfficeDocumentSession session, IOfficeStaDispatcher dispatcher)
            {
                Key = key;
                LockName = lockName;
                Gate = gate;
                Session = session;
                Dispatcher = dispatcher;
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
    }
}
