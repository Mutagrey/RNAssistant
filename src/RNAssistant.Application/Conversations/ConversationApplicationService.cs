using System;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Models;

namespace RNAssistant.Application.Conversations
{
    internal interface IConversationQueuePort
    {
        bool IsClosing { get; }
        ChatSession Reload(string chatId);
        void ValidateBinding(ChatSession session, ConversationInput input);
        Task ContinuePendingAsync(ChatSession session, string pendingId, CancellationToken token);
        Task StartTurnAsync(ChatSession session, ConversationInput input, CancellationToken token);
        void Changed(ChatSession session);
        void Failed(Exception error);
    }

    // One Application owner for tracked conversation work. The queue chooses the
    // next input from the canonical inbox; the port only prepares domain execution.
    internal sealed class ConversationApplicationService
    {
        private readonly ConversationInboxService _inbox;
        private readonly ConversationRunOwner _runs;
        private readonly ConversationWorkService _work = new ConversationWorkService();

        internal ConversationApplicationService(ConversationInboxService inbox, ConversationRunOwner runs)
        {
            _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
            _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        }

        internal bool HasWorkers { get { return _work.HasWorkers; } }
        internal Task Drained { get { return _work.Drained; } }
        internal Task CloseAsync() { return _work.CloseAsync(); }

        internal Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> execute,
            CancellationToken callerToken)
        { return _work.ExecuteAsync(execute, callerToken); }

        internal void StartQueue(ChatSession session, IConversationQueuePort port)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (port == null) throw new ArgumentNullException(nameof(port));
            if (!session.UsesCurrentProtocol || _work.IsClosing ||
                port.IsClosing || _runs.IsRunning(session.Id) || !_inbox.TryStartWorker(session)) return;
            try
            {
                _work.Start(async token =>
                {
                    try
                    {
                        while (!token.IsCancellationRequested && !port.IsClosing)
                        {
                            // Avoid reloading the entire conversation after its last
                            // input completes, or after Stop has paused retained work.
                            if (!_inbox.HasRunnable(session)) break;
                            session = port.Reload(session.Id);
                            var input = _inbox.TakeNext(session);
                            if (input == null) break;
                            port.ValidateBinding(session, input);
                            var pendingId = ConversationInboxService.PendingConfirmationId(session);
                            if (pendingId != null)
                                await port.ContinuePendingAsync(session, pendingId, token).ConfigureAwait(false);
                            else
                                await port.StartTurnAsync(session, input, token).ConfigureAwait(false);
                        }
                    }
                    catch (Exception error)
                    {
                        try { _inbox.Pause(session, error.Message); }
                        catch (Exception storageError)
                        { System.Diagnostics.Trace.TraceError("Queue pause could not be persisted: {0}", storageError.Message); }
                        port.Failed(error);
                    }
                    finally
                    {
                        try
                        {
                            _inbox.EndWorker(session);
                            if (!token.IsCancellationRequested && !port.IsClosing && _inbox.HasRunnable(session))
                                StartQueue(session, port);
                            if (!token.IsCancellationRequested && !port.IsClosing) port.Changed(session);
                        }
                        catch (Exception error)
                        { System.Diagnostics.Trace.TraceError("Queue projection failed: {0}", error.Message); }
                    }
                });
            }
            catch (ObjectDisposedException) { _inbox.EndWorker(session); }
        }

        internal ConversationOperation<TResult, TReceipt, TProgress> StartOperation<TResult, TReceipt, TProgress>(
            Func<ConversationOperation<TResult, TReceipt, TProgress>, Task<TResult>> execute,
            Func<TResult, TReceipt> receipt, Action<TProgress> progress,
            CancellationToken callerToken, bool detachAfterAcceptance, Func<IDisposable> runtimeUse = null)
            where TReceipt : class
        {
            if (execute == null) throw new ArgumentNullException(nameof(execute));
            if (receipt == null) throw new ArgumentNullException(nameof(receipt));
            if (_work.IsClosing) throw new ObjectDisposedException(nameof(ConversationApplicationService));
            var operation = new ConversationOperation<TResult, TReceipt, TProgress>(
                _work.Token, callerToken, detachAfterAcceptance, progress);
            try
            {
                _work.Start(async token =>
                {
                    try
                    {
                        operation.Token.ThrowIfCancellationRequested();
                        using (var use = runtimeUse?.Invoke())
                        {
                            var result = await execute(operation).ConfigureAwait(false);
                            operation.Complete(result, receipt(result));
                        }
                    }
                    catch (OperationCanceledException) { operation.Cancel(); }
                    catch (Exception error) { operation.Fail(error); }
                    finally { operation.Dispose(); }
                });
            }
            catch { operation.Dispose(); throw; }
            return operation;
        }
    }

    // A bridge may leave after durable acceptance. The owned task continues;
    // CLI callers keep cancellation attached until terminal completion.
    internal sealed class ConversationOperation<TResult, TReceipt, TProgress> : IDisposable
        where TReceipt : class
    {
        internal readonly TaskCompletionSource<bool> Admission =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<TReceipt> Receipt =
            new TaskCompletionSource<TReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<TResult> Completion =
            new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _sync = new object();
        private readonly CancellationTokenSource _cancellation;
        private readonly CancellationTokenRegistration _caller;
        private readonly bool _detachAfterAcceptance;
        private Action<TProgress> _progress;
        private TReceipt _delivered;
        private string _deliveredRunId;
        private bool _accepted;

        internal CancellationToken Token { get { return _cancellation.Token; } }

        internal ConversationOperation(CancellationToken lifetime, CancellationToken caller,
            bool detachAfterAcceptance, Action<TProgress> progress)
        {
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            _detachAfterAcceptance = detachAfterAcceptance;
            _progress = progress;
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

        internal void Report(TProgress value, TReceipt receipt, bool deliveryRecorded,
            string receiptRunId, string checkpointRunId)
        {
            lock (_sync)
            {
                if (receipt != null)
                {
                    _accepted = true;
                    if (deliveryRecorded) { _delivered = receipt; _deliveredRunId = receiptRunId; }
                }
                // An applied input alone precedes the first kernel checkpoint.
                if (_delivered != null && !string.IsNullOrWhiteSpace(_deliveredRunId) &&
                    checkpointRunId == _deliveredRunId)
                    Receipt.TrySetResult(_delivered);
            }
            try { _progress?.Invoke(value); }
            catch (Exception error)
            {
                _progress = null;
                System.Diagnostics.Trace.TraceError("Conversation progress observer detached: {0}", error.Message);
            }
        }

        internal void Complete(TResult result, TReceipt receipt)
        {
            Receipt.TrySetResult(receipt);
            Admission.TrySetResult(true);
            Completion.TrySetResult(result);
        }

        internal void Cancel()
        {
            Receipt.TrySetCanceled(); Admission.TrySetCanceled(); Completion.TrySetCanceled();
        }

        internal void Fail(Exception error)
        {
            Receipt.TrySetException(error); Admission.TrySetException(error); Completion.TrySetException(error);
            var receiptError = Receipt.Task.Exception;
            var admissionError = Admission.Task.Exception;
            var completionError = Completion.Task.Exception;
            System.Diagnostics.Trace.TraceError("Conversation operation failed: {0}", error.Message);
        }

        public void Dispose() { _caller.Dispose(); _cancellation.Dispose(); }
    }
}
