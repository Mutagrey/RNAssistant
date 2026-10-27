using System;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Application.Conversations;
using RNAssistant.Core.Models;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Runtime
{
    public sealed partial class WorkspaceConversationService
    {
        private readonly ConversationWorkService _runWork = new ConversationWorkService();

        public Task RunsDrained { get { return _runWork.Drained; } }

        public Task CloseAsync()
        {
            _titleLifetime.Cancel();
            return _runWork.CloseAsync();
        }

        public Task<WorkspaceRunResult> SubmitInputAsync(WorkspaceDescriptor workspace,
            WorkspaceSubmitInputRequest request, AppSettings settings, Func<string> apiKeyProvider,
            Action<WorkspaceRunEvent> progress = null, CancellationToken cancellationToken = default(CancellationToken),
            string modelDigest = null)
        {
            return StartInput(workspace, request, settings, apiKeyProvider, progress, cancellationToken,
                modelDigest, detachAfterAcceptance: false).Completion.Task;
        }

        // UI admission ends before model completion. CLI SubmitInputAsync awaits
        // Completion on the same owner and keeps its explicit cancellation token.
        internal Task<WorkspaceInputReceipt> AdmitInputAsync(WorkspaceDescriptor workspace,
            WorkspaceSubmitInputRequest request, AppSettings settings, Func<string> apiKeyProvider,
            Action<WorkspaceRunEvent> progress, CancellationToken cancellationToken)
        {
            if (request?.SteerRunId != null) return SteerInputAsync(workspace, request, cancellationToken);
            return StartInput(workspace, request, settings, apiKeyProvider, progress, cancellationToken,
                null, detachAfterAcceptance: true).Receipt.Task;
        }

        private RunOperation StartInput(WorkspaceDescriptor workspace, WorkspaceSubmitInputRequest request,
            AppSettings settings, Func<string> apiKeyProvider, Action<WorkspaceRunEvent> progress,
            CancellationToken callerToken, string modelDigest, bool detachAfterAcceptance)
        {
            settings = settings?.Clone();
            return StartRun(operation => ExecuteInputAsync(workspace, request, settings, apiKeyProvider,
                operation.Report, operation.Token, modelDigest), progress, callerToken, detachAfterAcceptance);
        }

        public Task<WorkspaceRunResult> ResolvePendingAsync(WorkspaceDescriptor workspace,
            string sessionId, string pendingId, bool approve, AppSettings settings,
            Func<string> apiKeyProvider, Action<WorkspaceRunEvent> progress = null,
            CancellationToken cancellationToken = default(CancellationToken), string modelDigest = null)
        {
            return StartPending(workspace, sessionId, pendingId, approve, settings, apiKeyProvider,
                progress, cancellationToken, modelDigest, detachAfterAcceptance: false).Completion.Task;
        }

        internal Task AdmitPendingAsync(WorkspaceDescriptor workspace, string sessionId, string pendingId,
            bool approve, AppSettings settings, Func<string> apiKeyProvider,
            Action<WorkspaceRunEvent> progress, CancellationToken cancellationToken, string modelDigest)
        {
            return StartPending(workspace, sessionId, pendingId, approve, settings, apiKeyProvider,
                progress, cancellationToken, modelDigest, detachAfterAcceptance: true).Admission.Task;
        }

        private RunOperation StartPending(WorkspaceDescriptor workspace, string sessionId, string pendingId,
            bool approve, AppSettings settings, Func<string> apiKeyProvider, Action<WorkspaceRunEvent> progress,
            CancellationToken callerToken, string modelDigest, bool detachAfterAcceptance)
        {
            settings = settings?.Clone();
            return StartRun(operation => ExecutePendingAsync(workspace, sessionId, pendingId, approve,
                settings, apiKeyProvider, operation.Report, operation.Token, modelDigest,
                operation.AcceptContinuation, waitForLease: !detachAfterAcceptance),
                progress, callerToken, detachAfterAcceptance);
        }

        private RunOperation StartRun(Func<RunOperation, Task<WorkspaceRunResult>> execute,
            Action<WorkspaceRunEvent> progress, CancellationToken callerToken, bool detachAfterAcceptance)
        {
            if (_runWork.IsClosing) throw new ObjectDisposedException(nameof(WorkspaceConversationService));
            var operation = new RunOperation(_runWork.Token, callerToken, detachAfterAcceptance, progress);
            try { _runWork.Start(async token =>
            {
                try
                {
                    operation.Token.ThrowIfCancellationRequested();
                    using (var runtimeUse = _runtimeUseLease?.Invoke())
                    {
                        var result = await execute(operation).ConfigureAwait(false);
                        operation.Receipt.TrySetResult(result.Receipt);
                        operation.Admission.TrySetResult(true);
                        operation.Completion.TrySetResult(result);
                    }
                }
                catch (OperationCanceledException)
                {
                    operation.Receipt.TrySetCanceled();
                    operation.Admission.TrySetCanceled();
                    operation.Completion.TrySetCanceled();
                }
                catch (Exception error)
                {
                    operation.Receipt.TrySetException(error);
                    operation.Admission.TrySetException(error);
                    operation.Completion.TrySetException(error);
                    // An admission API may have returned already. Observe all tasks;
                    // durable kernel state/recovery remains the run authority.
                    var receiptError = operation.Receipt.Task.Exception;
                    var admissionError = operation.Admission.Task.Exception;
                    var completionError = operation.Completion.Task.Exception;
                    System.Diagnostics.Trace.TraceError("Workspace run failed: {0}", error.Message);
                }
                finally
                {
                    operation.Dispose();
                }
            }); }
            catch { operation.Dispose(); throw; }
            return operation;
        }

        private sealed class RunOperation : IDisposable
        {
            internal readonly TaskCompletionSource<bool> Admission =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<WorkspaceInputReceipt> Receipt =
                new TaskCompletionSource<WorkspaceInputReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<WorkspaceRunResult> Completion =
                new TaskCompletionSource<WorkspaceRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly object _sync = new object();
            private readonly CancellationTokenSource _cancellation;
            private readonly CancellationTokenRegistration _caller;
            private readonly bool _detachAfterAcceptance;
            private Action<WorkspaceRunEvent> _progress;
            private WorkspaceInputReceipt _delivered;
            private bool _accepted;
            internal CancellationToken Token { get { return _cancellation.Token; } }

            internal RunOperation(CancellationToken lifetime, CancellationToken caller, bool detachAfterAcceptance,
                Action<WorkspaceRunEvent> progress)
            {
                _cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                _detachAfterAcceptance = detachAfterAcceptance; _progress = progress;
                _caller = caller.Register(() =>
                {
                    lock (_sync)
                    {
                        if (_detachAfterAcceptance && _accepted) return;
                        _cancellation.Cancel();
                    }
                });
            }

            internal void AcceptContinuation()
            {
                lock (_sync) _accepted = true;
                Admission.TrySetResult(true);
            }

            internal void Report(WorkspaceRunEvent value)
            {
                if (value.Receipt != null)
                {
                    lock (_sync) _accepted = true;
                    if (value.Receipt.DeliveryRecorded) _delivered = value.Receipt;
                }
                // InputApplied alone precedes the first kernel checkpoint. Wait
                // for its durable run identity so the returned UI snapshot runs.
                if (_delivered != null && value.Summary?.RunId == _delivered.RunId)
                    Receipt.TrySetResult(_delivered);
                try { _progress?.Invoke(value); }
                catch (Exception error)
                {
                    _progress = null;
                    System.Diagnostics.Trace.TraceError("Workspace progress observer detached: {0}", error.Message);
                }
            }

            public void Dispose() { _caller.Dispose(); _cancellation.Dispose(); }
        }
    }
}
