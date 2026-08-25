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
        private readonly Dictionary<string, QueueEntry> _queues =
            new Dictionary<string, QueueEntry>(StringComparer.OrdinalIgnoreCase);
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
            var entry = Acquire(sessionId);
            try
            {
                entry.Queue.Schedule(write);
            }
            finally
            {
                Release(sessionId, entry);
            }
        }

        public void EnqueueAndDrain(string sessionId, Action write)
        {
            var entry = Acquire(sessionId);
            try
            {
                entry.Queue.WaitAndThrow(entry.Queue.Schedule(write));
            }
            finally
            {
                Release(sessionId, entry);
            }
        }

        public void Drain(string sessionId)
        {
            EnqueueAndDrain(sessionId, delegate { });
        }

        internal int PendingCount(string sessionId)
        {
            QueueEntry entry;
            lock (_sync)
            {
                if (!_queues.TryGetValue(sessionId ?? string.Empty, out entry)) return 0;
            }
            return entry.Queue.PendingCount;
        }

        internal int QueueCount
        {
            get
            {
                lock (_sync) return _queues.Count;
            }
        }

        private QueueEntry Acquire(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw new ArgumentException("Session id is required.", "sessionId");
            }
            lock (_sync)
            {
                QueueEntry entry;
                if (!_queues.TryGetValue(sessionId, out entry))
                {
                    entry = new QueueEntry();
                    entry.Queue = new SessionQueue(_maxPendingWrites, () => TryRemove(sessionId, entry));
                    _queues.Add(sessionId, entry);
                }
                entry.Leases += 1;
                return entry;
            }
        }

        private void Release(string sessionId, QueueEntry entry)
        {
            lock (_sync)
            {
                QueueEntry current;
                if (!_queues.TryGetValue(sessionId, out current) || !ReferenceEquals(current, entry)) return;
                entry.Leases = Math.Max(0, entry.Leases - 1);
                RemoveIfIdle(sessionId, entry);
            }
        }

        private void TryRemove(string sessionId, QueueEntry entry)
        {
            lock (_sync)
            {
                QueueEntry current;
                if (!_queues.TryGetValue(sessionId, out current) || !ReferenceEquals(current, entry)) return;
                RemoveIfIdle(sessionId, entry);
            }
        }

        private void RemoveIfIdle(string sessionId, QueueEntry entry)
        {
            if (entry.Leases == 0 && entry.Queue.CanEvict) _queues.Remove(sessionId);
        }

        private sealed class QueueEntry
        {
            public SessionQueue Queue { get; set; }
            public int Leases { get; set; }
        }

        private sealed class SessionQueue
        {
            private readonly object _sync = new object();
            private readonly SemaphoreSlim _slots;
            private readonly Action _idle;
            private Task _tail = Task.FromResult(0);
            private Exception _failure;
            private int _pending;

            public SessionQueue(int maxPendingWrites, Action idle)
            {
                _slots = new SemaphoreSlim(maxPendingWrites, maxPendingWrites);
                _idle = idle;
            }

            public int PendingCount
            {
                get
                {
                    lock (_sync) return _pending;
                }
            }

            public bool CanEvict
            {
                get
                {
                    lock (_sync) return _pending == 0 && _failure == null;
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
                    var idle = false;
                    lock (_sync)
                    {
                        _pending -= 1;
                        idle = _pending == 0 && _failure == null;
                    }
                    _slots.Release();
                    if (idle && _idle != null) _idle();
                }
            }
        }
    }
}
