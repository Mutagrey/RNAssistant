using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using RNAssistant.Office.Diagnostics;

namespace RNAssistant.Office
{
    public sealed class OfficeUiDispatcher : IOfficeStaDispatcher, IOfficeDispatcherLifetime, IDisposable
    {
        private readonly Control _control;
        private readonly int _threadId;
        private volatile int _disposed;
        private readonly object _lifetimeGate = new object();
        private int _documentOperations;
        private bool _disposeRequested;
        private TaskCompletionSource<bool> _documentOperationCompletion;

        public OfficeUiDispatcher()
        {
            _threadId = Thread.CurrentThread.ManagedThreadId;
            _control = new Control();
            var handle = _control.Handle;
            if (handle == IntPtr.Zero)
            {
                throw new InvalidOperationException("Could not create the Office UI dispatcher handle.");
            }
        }

        public bool CheckAccess
        {
            get { return Thread.CurrentThread.ManagedThreadId == _threadId; }
        }

        public T Invoke<T>(Func<T> action)
        {
            if (action == null) throw new ArgumentNullException("action");
            ThrowIfDisposed();
            if (CheckAccess) return action();
            return (T)_control.Invoke(action);
        }

        public void Invoke(Action action)
        {
            if (action == null) throw new ArgumentNullException("action");
            Invoke<object>(delegate
            {
                action();
                return null;
            });
        }

        public void Dispose()
        {
            lock (_lifetimeGate)
            {
                if (_disposeRequested) return;
                _disposeRequested = true;
                if (_documentOperations != 0) return;
                _disposed = 1;
            }
            DisposeCore();
        }

        private void DisposeCore()
        {
            if (Thread.CurrentThread.ManagedThreadId == _threadId)
            {
                _control.Dispose();
                return;
            }
            try { _control.Invoke(new Action(delegate { _control.Dispose(); })); }
            catch (InvalidOperationException) { }
        }

        public Task DocumentOperationCompletion
        {
            get
            {
                lock (_lifetimeGate)
                    return _documentOperations == 0 ? Task.FromResult(true) : _documentOperationCompletion.Task;
            }
        }

        public IDisposable RetainForDocumentOperation()
        {
            lock (_lifetimeGate)
            {
                if (_disposeRequested || _disposed != 0)
                    throw new ObjectDisposedException(GetType().FullName);
                if (_documentOperations++ == 0)
                    _documentOperationCompletion = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                return new DocumentOperationLifetime(this);
            }
        }

        private void ReleaseDocumentOperation()
        {
            TaskCompletionSource<bool> completion = null;
            var dispose = false;
            lock (_lifetimeGate)
            {
                if (--_documentOperations == 0)
                {
                    completion = _documentOperationCompletion;
                    if (_disposeRequested && _disposed == 0) { _disposed = 1; dispose = true; }
                }
            }
            if (completion != null) completion.TrySetResult(true);
            if (dispose) DisposeCore();
        }

        private sealed class DocumentOperationLifetime : IDisposable
        {
            private OfficeUiDispatcher _owner;
            internal DocumentOperationLifetime(OfficeUiDispatcher owner) { _owner = owner; }
            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner != null) owner.ReleaseDocumentOperation();
            }
        }

        public void DisposeAfter(Task work)
        {
            if (work == null || work.IsCompleted)
            {
                Dispose();
                return;
            }
            work.ContinueWith(ignored =>
            {
                try { Dispose(); }
                catch (Exception ex) { RuntimeLog.Error("Office UI dispatcher cleanup failed.", ex); }
            }, TaskContinuationOptions.ExecuteSynchronously);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed != 0) throw new ObjectDisposedException(GetType().FullName);
        }
    }
}
