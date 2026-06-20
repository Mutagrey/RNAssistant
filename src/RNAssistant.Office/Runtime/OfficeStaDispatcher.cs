using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace RNAssistant.Office
{
    public sealed class OfficeStaDispatcher : IDisposable
    {
        private readonly BlockingCollection<WorkItem> _queue;
        private readonly Thread _thread;
        private int _disposed;
        private int _threadId;

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

        public T Invoke<T>(Func<T> action)
        {
            if (action == null)
            {
                throw new ArgumentNullException("action");
            }

            if (Thread.CurrentThread.ManagedThreadId == _threadId)
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
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

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
