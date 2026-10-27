using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using RNAssistant.Core.Models;

namespace RNAssistant.Application.Conversations
{
    internal sealed class ConversationRunReservation : IDisposable
    {
        private readonly ConversationRunOwner _owner;
        private readonly IDisposable _lease;
        private readonly object _sync = new object();
        private ConversationRunHandle _active;
        private bool _disposed;

        internal ConversationRunReservation(ConversationRunOwner owner, string chatId, IDisposable lease)
        { _owner = owner; ChatId = chatId; _lease = lease; }

        internal string ChatId { get; private set; }
        internal ConversationRunOwner Owner { get { return _owner; } }

        internal ConversationRunHandle Activate(string runId, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                if (_disposed || _active != null)
                    throw new InvalidOperationException("This conversation reservation is no longer available.");
                _active = _owner.AttachReserved(this, runId, cancellationToken);
                return _active;
            }
        }

        public void Dispose()
        {
            ConversationRunHandle active;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                active = _active;
            }
            try { active?.Dispose(); }
            finally { _lease?.Dispose(); }
        }
    }

    internal sealed class ConversationRunHandle : IDisposable
    {
        private readonly ConversationRunOwner _owner;
        private int _completed;

        internal ConversationRunHandle(ConversationRunOwner owner, string chatId, string runId, CancellationToken token)
        {
            _owner = owner;
            ChatId = chatId;
            RunId = runId;
            Token = token;
        }

        public string ChatId { get; private set; }
        public string RunId { get; private set; }
        public CancellationToken Token { get; private set; }
        public bool IsActive { get { return _owner.IsRunActive(ChatId, RunId); } }
        public bool Cancel() { return _owner.Cancel(ChatId, RunId); }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                _owner.Complete(ChatId, RunId);
            }
        }
    }

    internal sealed class ConversationActiveSnapshot
    {
        public string ChatId { get; set; }
        public string RunId { get; set; }
        public string Status { get; set; }
        public string Phase { get; set; }
        public string CurrentAction { get; set; }
        public DateTime StartedUtc { get; set; }
        public ChatSession Session { get; set; }
        internal CancellationTokenSource Cancellation { get; set; }
        internal IDisposable RunLock { get; set; }
    }

    // Local live ownership and cancellation. Durable lifecycle/outcomes remain
    // exclusively in the conversation journal and AgentKernel.
    internal sealed class ConversationRunOwner
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, ConversationActiveSnapshot> _runs =
            new Dictionary<string, ConversationActiveSnapshot>(StringComparer.OrdinalIgnoreCase);
        private readonly ConversationLeaseService _leases;
        private readonly Func<ChatSession, ChatSession> _snapshot;

        public ConversationRunOwner(ConversationLeaseService leases, Func<ChatSession, ChatSession> snapshot = null)
        {
            _leases = leases ?? throw new ArgumentNullException(nameof(leases));
            _snapshot = snapshot;
        }

        public ConversationRunHandle Start(string chatId, string runId, ChatSession session,
            CancellationTokenSource cancellation = null)
        { return Open(chatId, runId, session, cancellation, acquireLease: true); }

        // Reserve before domain preparation. The same handle keeps the exact
        // lease through kernel execution and read-back.
        public ConversationRunReservation Reserve(string chatId, bool wait = true,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return new ConversationRunReservation(this, chatId,
                _leases.Reserve(chatId, wait, cancellationToken));
        }

        internal ConversationRunHandle AttachReserved(ConversationRunReservation reservation, string runId,
            CancellationToken cancellationToken)
        {
            if (reservation == null || !ReferenceEquals(reservation.Owner, this))
                throw new InvalidOperationException("The run reservation belongs to another owner.");
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try { return Open(reservation.ChatId, runId, null, cancellation, acquireLease: false); }
            catch { cancellation.Dispose(); throw; }
        }

        private ConversationRunHandle Open(string chatId, string runId, ChatSession session,
            CancellationTokenSource cancellation, bool acquireLease)
        {
            if (string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(runId))
            {
                throw new InvalidOperationException("Chat and run ids are required.");
            }
            if (session != null && _snapshot == null)
                throw new InvalidOperationException("A session snapshot adapter is required for this run.");
            using (_leases.AcquireMaintenance())
            {
                lock (_sync)
                {
                    ConversationActiveSnapshot existing;
                    if (_runs.TryGetValue(chatId, out existing))
                    {
                        throw new InvalidOperationException("В этом чате уже выполняется запрос.");
                    }
                    var runLock = acquireLease ? AcquireRunLock(chatId) : null;
                    try
                    {
                        var run = new ConversationActiveSnapshot
                        {
                            ChatId = chatId,
                            RunId = runId,
                            Status = "running",
                            Phase = "starting",
                            StartedUtc = DateTime.UtcNow,
                            Session = session == null ? null : _snapshot(session),
                            Cancellation = cancellation,
                            RunLock = runLock
                        };
                        _runs[chatId] = run;
                        return new ConversationRunHandle(this, chatId, runId,
                            cancellation == null ? CancellationToken.None : cancellation.Token);
                    }
                    catch
                    {
                        if (runLock != null) runLock.Dispose();
                        throw;
                    }
                }
            }
        }

        public void Update(string chatId, string runId, string phase, string currentAction = null)
        {
            lock (_sync)
            {
                ConversationActiveSnapshot run;
                if (_runs.TryGetValue(chatId ?? string.Empty, out run) &&
                    string.Equals(run.RunId, runId, StringComparison.OrdinalIgnoreCase))
                {
                    run.Phase = string.IsNullOrWhiteSpace(phase) ? run.Phase : phase;
                    run.CurrentAction = string.IsNullOrWhiteSpace(currentAction) ? run.CurrentAction : currentAction;
                }
            }
        }

        public void UpdateSessionSnapshot(string chatId, string runId, ChatSession session)
        {
            if (_snapshot == null) throw new InvalidOperationException("A session snapshot adapter is required.");
            var snapshot = _snapshot(session);
            lock (_sync)
            {
                ConversationActiveSnapshot run;
                if (_runs.TryGetValue(chatId ?? string.Empty, out run) &&
                    string.Equals(run.RunId, runId, StringComparison.OrdinalIgnoreCase))
                {
                    run.Session = snapshot;
                }
            }
        }

        public bool Cancel(string chatId, string runId)
        {
            CancellationTokenSource cancellation;
            lock (_sync)
            {
                ConversationActiveSnapshot run;
                if (!_runs.TryGetValue(chatId ?? string.Empty, out run) ||
                    !string.Equals(run.RunId, runId, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                run.Status = "cancelling";
                run.Phase = "cancelling";
                cancellation = run.Cancellation;
            }
            try
            {
                if (cancellation != null && !cancellation.IsCancellationRequested)
                {
                    cancellation.Cancel();
                }
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (AggregateException)
            {
                // The token is already signalled; a failing observer cannot
                // turn an accepted Stop into an unhandled transport failure.
            }
            return cancellation != null;
        }

        public ConversationActiveSnapshot Get(string chatId)
        {
            ConversationActiveSnapshot snapshot;
            lock (_sync)
            {
                ConversationActiveSnapshot run;
                snapshot = _runs.TryGetValue(chatId ?? string.Empty, out run) ? ShallowClone(run) : null;
            }
            if (snapshot?.Session != null) snapshot.Session = _snapshot(snapshot.Session);
            return snapshot;
        }

        public bool IsRunning(string chatId)
        {
            lock (_sync) return _runs.ContainsKey(chatId ?? string.Empty);
        }

        internal bool IsRunActive(string chatId, string runId)
        {
            lock (_sync)
            {
                ConversationActiveSnapshot run;
                return _runs.TryGetValue(chatId ?? string.Empty, out run) &&
                    string.Equals(run.RunId, runId, StringComparison.OrdinalIgnoreCase);
            }
        }

        public bool HasRuns()
        {
            lock (_sync) return _runs.Count > 0;
        }

        public bool IsConversationReserved(string chatId)
        {
            if (string.IsNullOrWhiteSpace(chatId)) return false;
            lock (_sync)
            {
                if (_runs.ContainsKey(chatId)) return true;
            }
            return _leases.IsHeld(chatId);
        }

        public IReadOnlyList<ChatSession> Sessions()
        {
            lock (_sync)
            {
                // Session snapshots are immutable after assignment; callers only read them to
                // build chat summaries. Avoid cloning the complete transcript on every UI poll.
                return _runs.Values
                    .Where(item => item.Session != null)
                    .Select(item => item.Session)
                    .ToList();
            }
        }

        public void Complete(string chatId, string runId)
        {
            lock (_sync)
            {
                ConversationActiveSnapshot run;
                if (_runs.TryGetValue(chatId ?? string.Empty, out run) &&
                    string.Equals(run.RunId, runId, StringComparison.OrdinalIgnoreCase))
                {
                    _runs.Remove(chatId);
                    if (run.Cancellation != null) run.Cancellation.Dispose();
                    if (run.RunLock != null) run.RunLock.Dispose();
                }
            }
        }

        public void CancelAll()
        {
            List<CancellationTokenSource> cancellations;
            lock (_sync)
            {
                foreach (var run in _runs.Values)
                {
                    run.Status = "cancelling";
                    run.Phase = "cancelling";
                }
                cancellations = _runs.Values
                    .Select(run => run.Cancellation)
                    .Where(source => source != null)
                    .ToList();
            }

            foreach (var cancellation in cancellations)
            {
                try
                {
                    if (!cancellation.IsCancellationRequested) cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                catch (AggregateException)
                {
                }
            }
        }

        private IDisposable AcquireRunLock(string chatId)
        {
            try
            {
                return _leases.Reserve(chatId);
            }
            catch (IOException)
            {
                throw new InvalidOperationException("В этом чате уже выполняется запрос в другом окне RNAssistant.");
            }
            catch (UnauthorizedAccessException)
            {
                throw new InvalidOperationException("Не удалось получить блокировку чата.");
            }
        }

        private static ConversationActiveSnapshot ShallowClone(ConversationActiveSnapshot source)
        {
            return source == null ? null : new ConversationActiveSnapshot
            {
                ChatId = source.ChatId,
                RunId = source.RunId,
                Status = source.Status,
                Phase = source.Phase,
                CurrentAction = source.CurrentAction,
                StartedUtc = source.StartedUtc,
                Session = source.Session
            };
        }
    }
}
