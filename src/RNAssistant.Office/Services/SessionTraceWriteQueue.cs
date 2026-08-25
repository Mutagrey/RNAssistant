using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace RNAssistant.Office.Services
{
    internal sealed class SessionTraceWriteQueue
    {
        private const int DefaultMaxPendingWrites = 16;
        private readonly object _sync = new object();
        private readonly Dictionary<string, SessionQueue> _queues =
            new Dictionary<string, SessionQueue>(StringComparer.OrdinalIgnoreCase);
        private readonly int _maxPendingWrites;

        public SessionTraceWriteQueue()
            : this(DefaultMaxPendingWrites)
        {
        }

        internal SessionTraceWriteQueue(int maxPendingWrites)
        {
            _maxPendingWrites = Math.Max(1, maxPendingWrites);
        }

        public void Enqueue(string sessionId, Action write)
        {
            Queue(sessionId).Schedule(write);
        }

        public void EnqueueAndDrain(string sessionId, Action write)
        {
            var queue = Queue(sessionId);
            queue.WaitAndThrow(queue.Schedule(write));
        }

        public void Drain(string sessionId)
        {
            EnqueueAndDrain(sessionId, delegate { });
        }

        internal int PendingCount(string sessionId)
        {
            SessionQueue queue;
            lock (_sync)
            {
                if (!_queues.TryGetValue(sessionId ?? string.Empty, out queue)) return 0;
            }
            return queue.PendingCount;
        }

        private SessionQueue Queue(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw new ArgumentException("Session id is required.", "sessionId");
            }
            lock (_sync)
            {
                SessionQueue queue;
                if (_queues.TryGetValue(sessionId, out queue)) return queue;
                queue = new SessionQueue(_maxPendingWrites);
                _queues.Add(sessionId, queue);
                return queue;
            }
        }

        private sealed class SessionQueue
        {
            private readonly object _sync = new object();
            private readonly SemaphoreSlim _slots;
            private Task _tail = Task.FromResult(0);
            private Exception _failure;
            private int _pending;

            public SessionQueue(int maxPendingWrites)
            {
                _slots = new SemaphoreSlim(maxPendingWrites, maxPendingWrites);
            }

            public int PendingCount
            {
                get
                {
                    lock (_sync) return _pending;
                }
            }

            public Task Schedule(Action write)
            {
                if (write == null) throw new ArgumentNullException("write");
                _slots.Wait();
                lock (_sync)
                {
                    _pending += 1;
                    var predecessor = _tail;
                    _tail = predecessor.ContinueWith(
                        ignored => Execute(write),
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        TaskScheduler.Default);
                    return _tail;
                }
            }

            public void WaitAndThrow(Task scheduled)
            {
                scheduled.GetAwaiter().GetResult();
                Exception failure;
                lock (_sync)
                {
                    failure = _failure;
                    if (_pending == 0) _failure = null;
                }
                if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
            }

            private void Execute(Action write)
            {
                try
                {
                    Exception failure;
                    lock (_sync) failure = _failure;
                    if (failure == null) write();
                }
                catch (Exception ex)
                {
                    lock (_sync)
                    {
                        if (_failure == null) _failure = ex;
                    }
                }
                finally
                {
                    lock (_sync) _pending -= 1;
                    _slots.Release();
                }
            }
        }
    }
}
