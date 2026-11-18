using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace RNAssistant.Office
{
    public sealed class OfficeStaDispatcher : IOfficeStaDispatcher, IOfficeDispatcherLifetime, IDisposable
    {
        private readonly BlockingCollection<WorkItem> _queue;
        private readonly Thread _thread;
        private volatile int _disposed;
        private int _threadId;
        private readonly object _lifetimeGate = new object();
        private int _documentOperations;
        private bool _disposeRequested;
        private TaskCompletionSource<bool> _documentOperationCompletion;

        public OfficeStaDispatcher()
        {
            _queue = new BlockingCollection<WorkItem>();
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "RNAssistant Office COM STA"
            };
            TrySetStaApartment(_thread);
            _thread.Start();
        }

        public bool CheckAccess
        {
            get { return Thread.CurrentThread.ManagedThreadId == _threadId; }
        }

        public T Invoke<T>(Func<T> action)
        {
            if (action == null)
            {
                throw new ArgumentNullException("action");
            }

            if (CheckAccess)
            {
                return action();
            }

            ThrowIfDisposed();

            var item = new WorkItem(delegate { return action(); });
            try
            {
                _queue.Add(item);
            }
            catch (InvalidOperationException ex)
            {
                throw new ObjectDisposedException(GetType().FullName, ex);
            }

            item.Wait();
            if (item.Error != null)
            {
                item.Error.Throw();
            }

            return (T)item.Result;
        }

        public void Invoke(Action action)
        {
            if (action == null)
            {
                throw new ArgumentNullException("action");
            }

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
            _queue.CompleteAdding();
            if (Thread.CurrentThread.ManagedThreadId != _threadId)
            {
                if (_thread.Join(TimeSpan.FromSeconds(2)))
                {
                    _queue.Dispose();
                }
                return;
            }
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
            private OfficeStaDispatcher _owner;
            internal DocumentOperationLifetime(OfficeStaDispatcher owner) { _owner = owner; }
            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner != null) owner.ReleaseDocumentOperation();
            }
        }

        private void Run()
        {
            _threadId = Thread.CurrentThread.ManagedThreadId;
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                item.Execute();
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed != 0)
            {
                throw new ObjectDisposedException(GetType().FullName);
            }
        }

        private static void TrySetStaApartment(Thread thread)
        {
            try
            {
#pragma warning disable CA1416
                thread.SetApartmentState(ApartmentState.STA);
#pragma warning restore CA1416
            }
            catch (PlatformNotSupportedException)
            {
                // Non-Windows harness runs have no COM apartment support.
            }
        }

        private sealed class WorkItem
        {
            private readonly Func<object> _action;
            private readonly ManualResetEventSlim _completed;

            public WorkItem(Func<object> action)
            {
                _action = action;
                _completed = new ManualResetEventSlim(false);
            }

            public object Result { get; private set; }
            public ExceptionDispatchInfo Error { get; private set; }

            public void Execute()
            {
                try
                {
                    Result = _action();
                }
                catch (Exception ex)
                {
                    Error = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    _completed.Set();
                }
            }

            public void Wait()
            {
                _completed.Wait();
                _completed.Dispose();
            }
        }
    }
}
