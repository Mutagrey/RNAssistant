using System;
using System.Threading;
using System.Windows.Forms;

namespace RNAssistant.Office
{
    public sealed class OfficeUiDispatcher : IOfficeStaDispatcher, IDisposable
    {
        private readonly Control _control;
        private readonly int _threadId;
        private int _disposed;

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
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (Thread.CurrentThread.ManagedThreadId == _threadId)
            {
                _control.Dispose();
                return;
            }
            try { _control.Invoke(new Action(delegate { _control.Dispose(); })); }
            catch (InvalidOperationException) { }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed != 0) throw new ObjectDisposedException(GetType().FullName);
        }
    }
}
