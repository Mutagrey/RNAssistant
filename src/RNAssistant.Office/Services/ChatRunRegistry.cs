using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Services
{
    internal sealed class ChatRunLease : IDisposable
    {
        private readonly ChatRunRegistry _registry;
        private int _completed;

        internal ChatRunLease(ChatRunRegistry registry, string chatId, string runId)
        {
            _registry = registry;
            ChatId = chatId;
            RunId = runId;
        }

        public string ChatId { get; private set; }
        public string RunId { get; private set; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                _registry.Complete(ChatId, RunId);
            }
        }
    }

    internal sealed class ChatRunSnapshot
    {
        public string ChatId { get; set; }
        public string RunId { get; set; }
        public string Status { get; set; }
        public string Phase { get; set; }
        public string CurrentAction { get; set; }
        public DateTime StartedUtc { get; set; }
        public ChatSession Session { get; set; }
        internal CancellationTokenSource Cancellation { get; set; }
        internal FileStream RunLock { get; set; }
    }

    internal sealed class ChatRunRegistry
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, ChatRunSnapshot> _runs =
            new Dictionary<string, ChatRunSnapshot>(StringComparer.OrdinalIgnoreCase);
        private readonly string _lockDirectory;
        private readonly ThreadLocal<CoordinationLockState> _coordinationState =
            new ThreadLocal<CoordinationLockState>();

        public ChatRunRegistry(AppDataPaths paths = null)
        {
            _lockDirectory = paths == null ? null : Path.Combine(paths.Root, "locks");
        }

        public ChatRunLease Start(string chatId, string runId, ChatSession session, CancellationTokenSource cancellation = null)
        {
            if (string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(runId))
            {
                throw new InvalidOperationException("Chat and run ids are required.");
            }
            using (AcquireCoordinationLock())
            {
                lock (_sync)
                {
                    ChatRunSnapshot existing;
                    if (_runs.TryGetValue(chatId, out existing))
                    {
                        throw new InvalidOperationException("В этом чате уже выполняется запрос.");
                    }
                    var runLock = AcquireRunLock(chatId);
                    try
                    {
                        var run = new ChatRunSnapshot
                        {
                            ChatId = chatId,
                            RunId = runId,
                            Status = "running",
                            Phase = "starting",
                            StartedUtc = DateTime.UtcNow,
                            Session = ChatCloneService.CloneSessionSnapshot(session),
                            Cancellation = cancellation,
                            RunLock = runLock
                        };
                        _runs[chatId] = run;
                        return new ChatRunLease(this, chatId, runId);
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
                ChatRunSnapshot run;
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
            var snapshot = ChatCloneService.CloneSessionSnapshot(session);
            lock (_sync)
            {
                ChatRunSnapshot run;
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
                ChatRunSnapshot run;
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
            return cancellation != null;
        }

        public ChatRunSnapshot Get(string chatId)
        {
            ChatRunSnapshot snapshot;
            lock (_sync)
            {
                ChatRunSnapshot run;
                snapshot = _runs.TryGetValue(chatId ?? string.Empty, out run) ? ShallowClone(run) : null;
            }
            if (snapshot != null) snapshot.Session = ChatCloneService.CloneSessionSnapshot(snapshot.Session);
            return snapshot;
        }

        public ChatRunSnapshot GetStatus(string chatId)
        {
            lock (_sync)
            {
                ChatRunSnapshot run;
                if (!_runs.TryGetValue(chatId ?? string.Empty, out run)) return null;
                var status = ShallowClone(run);
                status.Session = null;
                return status;
            }
        }

        public bool IsRunning(string chatId)
        {
            lock (_sync) return _runs.ContainsKey(chatId ?? string.Empty);
        }

        public bool HasRuns()
        {
            lock (_sync) return _runs.Count > 0;
        }

        public bool HasExternalRuns()
        {
            if (string.IsNullOrWhiteSpace(_lockDirectory) || !Directory.Exists(_lockDirectory)) return false;
            string[] paths;
            try
            {
                paths = Directory.GetFiles(_lockDirectory, "run_*.lck");
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }

            foreach (var path in paths)
            {
                try
                {
                    using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    {
                    }
                }
                catch (IOException)
                {
                    return true;
                }
                catch (UnauthorizedAccessException)
                {
                    return true;
                }
            }
            return false;
        }

        public IDisposable ReserveMaintenance()
        {
            return AcquireCoordinationLock();
        }

        public bool IsExternallyRunning(string chatId)
        {
            if (string.IsNullOrWhiteSpace(chatId)) return false;
            lock (_sync)
            {
                if (_runs.ContainsKey(chatId)) return true;
            }
            if (string.IsNullOrWhiteSpace(_lockDirectory)) return false;
            try
            {
                using (OpenRunLock(chatId))
                {
                    return false;
                }
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
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
                ChatRunSnapshot run;
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

        private FileStream AcquireRunLock(string chatId)
        {
            if (string.IsNullOrWhiteSpace(_lockDirectory)) return null;
            try
            {
                return OpenRunLock(chatId);
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

        private FileStream OpenRunLock(string chatId)
        {
            Directory.CreateDirectory(_lockDirectory);
            var path = Path.Combine(_lockDirectory, "run_" + AppDataPaths.SafeFileName(chatId) + ".lck");
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        private IDisposable AcquireCoordinationLock()
        {
            if (string.IsNullOrWhiteSpace(_lockDirectory)) return null;
            var current = _coordinationState.Value;
            if (current != null)
            {
                current.Depth += 1;
                return new CoordinationLockLease(this, current);
            }

            Directory.CreateDirectory(_lockDirectory);
            var path = Path.Combine(_lockDirectory, "run-registry.lck");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                try
                {
                    var state = new CoordinationLockState
                    {
                        Depth = 1,
                        Stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
                    };
                    _coordinationState.Value = state;
                    return new CoordinationLockLease(this, state);
                }
                catch (IOException)
                {
                    if (DateTime.UtcNow >= deadline)
                    {
                        throw new InvalidOperationException("Не удалось начать операцию: другое окно RNAssistant обновляет состояние запусков.");
                    }
                    Thread.Sleep(25);
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw new InvalidOperationException("Не удалось получить системную блокировку RNAssistant.", ex);
                }
            }
        }

        private void ReleaseCoordinationLock(CoordinationLockState state)
        {
            if (state == null || state.Depth <= 0) return;
            state.Depth -= 1;
            if (state.Depth > 0) return;
            if (ReferenceEquals(_coordinationState.Value, state)) _coordinationState.Value = null;
            state.Stream.Dispose();
        }

        private sealed class CoordinationLockState
        {
            public int Depth { get; set; }
            public FileStream Stream { get; set; }
        }

        private sealed class CoordinationLockLease : IDisposable
        {
            private readonly ChatRunRegistry _registry;
            private readonly CoordinationLockState _state;
            private int _disposed;

            public CoordinationLockLease(ChatRunRegistry registry, CoordinationLockState state)
            {
                _registry = registry;
                _state = state;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _registry.ReleaseCoordinationLock(_state);
                }
            }
        }

        private static ChatRunSnapshot ShallowClone(ChatRunSnapshot source)
        {
            return source == null ? null : new ChatRunSnapshot
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
