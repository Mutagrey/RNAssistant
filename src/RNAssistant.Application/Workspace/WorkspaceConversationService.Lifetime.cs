using System;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Application.Conversations;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Runtime
{
    public sealed partial class WorkspaceConversationService
    {
        public Task RunsDrained { get { return _conversationApplication.Drained; } }

        public Task CloseAsync()
        {
            _titleLifetime.Cancel();
            return _conversationApplication.CloseAsync();
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
            if (request?.SteerRunId != null || request?.Enqueue == true) return SteerInputAsync(workspace, request, cancellationToken);
            return StartInput(workspace, request, settings, apiKeyProvider, progress, cancellationToken,
                null, detachAfterAcceptance: true).Receipt.Task;
        }

        private ConversationOperation<WorkspaceRunResult, WorkspaceInputReceipt, WorkspaceRunEvent> StartInput(
            WorkspaceDescriptor workspace, WorkspaceSubmitInputRequest request,
            AppSettings settings, Func<string> apiKeyProvider, Action<WorkspaceRunEvent> progress,
            CancellationToken callerToken, string modelDigest, bool detachAfterAcceptance)
        {
            settings = settings?.Clone();
            return _conversationApplication.StartOperation<WorkspaceRunResult, WorkspaceInputReceipt, WorkspaceRunEvent>(async operation => {
                return await DeferTerminalProgressAsync(value => Report(operation, value), async report => {
                    var result = await ExecuteInputAsync(workspace, request, settings, apiKeyProvider,
                        report, operation.Token, modelDigest,
                        waitForLease: !detachAfterAcceptance).ConfigureAwait(false);
                    StartQueuedInputs(workspace, result.SessionId, settings, apiKeyProvider, progress, modelDigest);
                    return result;
                }).ConfigureAwait(false);
            }, result => result.Receipt,
                progress, callerToken, detachAfterAcceptance, _runtimeUseLease);
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

        private ConversationOperation<WorkspaceRunResult, WorkspaceInputReceipt, WorkspaceRunEvent> StartPending(
            WorkspaceDescriptor workspace, string sessionId, string pendingId,
            bool approve, AppSettings settings, Func<string> apiKeyProvider, Action<WorkspaceRunEvent> progress,
            CancellationToken callerToken, string modelDigest, bool detachAfterAcceptance)
        {
            settings = settings?.Clone();
            return _conversationApplication.StartOperation<WorkspaceRunResult, WorkspaceInputReceipt, WorkspaceRunEvent>(async operation => {
                return await DeferTerminalProgressAsync(value => Report(operation, value), async report => {
                    var result = await ExecutePendingAsync(workspace, sessionId, pendingId, approve, settings, apiKeyProvider,
                        report, operation.Token, modelDigest,
                        operation.AcceptContinuation, waitForLease: !detachAfterAcceptance).ConfigureAwait(false);
                    StartQueuedInputs(workspace, sessionId, settings, apiKeyProvider, progress, modelDigest);
                    return result;
                }).ConfigureAwait(false);
            },
                result => result.Receipt, progress, callerToken, detachAfterAcceptance, _runtimeUseLease);
        }

        // Publish a terminal projection only after the exact run reservation and
        // session lease have left their owner scopes. Otherwise the UI can offer a
        // new turn while admission still reports the finished run as busy.
        private static async Task<T> DeferTerminalProgressAsync<T>(Action<WorkspaceRunEvent> publish,
            Func<Action<WorkspaceRunEvent>, Task<T>> execute)
        {
            WorkspaceRunEvent terminal = null;
            try
            {
                return await execute(value => {
                    if (IsTerminalRunEvent(value)) terminal = value;
                    else publish?.Invoke(value);
                }).ConfigureAwait(false);
            }
            finally
            {
                // execute has left its using scopes, including the run reservation,
                // before either a terminal projection or its failure is published.
                if (terminal != null) publish?.Invoke(terminal);
            }
        }

        private static bool IsTerminalRunEvent(WorkspaceRunEvent value)
        {
            var lifecycle = value?.Summary?.Lifecycle;
            return lifecycle == RunLifecycle.Completed || lifecycle == RunLifecycle.Cancelled || lifecycle == RunLifecycle.Failed;
        }

        private static void Report(
            ConversationOperation<WorkspaceRunResult, WorkspaceInputReceipt, WorkspaceRunEvent> operation,
            WorkspaceRunEvent value)
        {
            operation.Report(value, value.Receipt, value.Receipt?.DeliveryRecorded == true,
                value.Receipt?.RunId, value.Summary?.RunId);
        }
    }
}
