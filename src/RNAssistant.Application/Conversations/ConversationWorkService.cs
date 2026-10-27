using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RNAssistant.Application.Conversations
{
    // Application lifetime for admitted conversation work, independent of the
    // bridge request and selected chat. Kernel state remains the run authority.
    internal sealed class ConversationWorkService
    {
        private readonly object _sync = new object();
        private readonly HashSet<Task> _workers = new HashSet<Task>();
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private Task _closing;

        internal CancellationToken Token { get { return _lifetime.Token; } }
        internal bool IsClosing { get { lock (_sync) return _closing != null; } }
        internal bool HasWorkers { get { lock (_sync) return _workers.Count != 0; } }
        internal Task Drained { get { lock (_sync) return Task.WhenAll(_workers.ToArray()); } }

        internal void Start(Func<CancellationToken, Task> work)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                if (_closing != null) throw new ObjectDisposedException(nameof(ConversationWorkService));
                _workers.Add(done.Task);
            }
            Task.Run(async () =>
            {
                try { await work(_lifetime.Token).ConfigureAwait(false); }
                catch (Exception error)
                { System.Diagnostics.Trace.TraceError("Conversation work failed: {0}", error.Message); }
                finally
                {
                    lock (_sync) _workers.Remove(done.Task);
                    done.TrySetResult(true);
                }
            });
        }

        internal Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> work,
            CancellationToken callerToken)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            var completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Start(async lifetime =>
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, callerToken))
                {
                    try { completion.TrySetResult(await work(linked.Token).ConfigureAwait(false)); }
                    catch (OperationCanceledException) { completion.TrySetCanceled(); }
                    catch (Exception error) { completion.TrySetException(error); }
                }
            });
            return completion.Task;
        }

        internal Task CloseAsync()
        {
            Task[] workers;
            TaskCompletionSource<bool> closed;
            lock (_sync)
            {
                if (_closing != null) return _closing;
                closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _closing = closed.Task;
                workers = _workers.ToArray();
            }
            Exception cancelError = null;
            try { _lifetime.Cancel(); }
            catch (Exception error) { cancelError = error; }
            Task.WhenAll(workers).ContinueWith(task =>
            {
                _lifetime.Dispose();
                if (cancelError != null) closed.TrySetException(cancelError);
                else if (task.IsFaulted) closed.TrySetException(task.Exception);
                else closed.TrySetResult(true);
            }, TaskScheduler.Default);
            return _closing;
        }
    }
}
