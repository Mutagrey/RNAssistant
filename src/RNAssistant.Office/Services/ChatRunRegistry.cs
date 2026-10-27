using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using RNAssistant.Application.Conversations;
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
        internal IDisposable RunLock { get; set; }
    }

    internal sealed class ChatRunRegistry
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, ChatRunSnapshot> _runs =
            new Dictionary<string, ChatRunSnapshot>(StringComparer.OrdinalIgnoreCase);
        private readonly ConversationLeaseService _leases;

        public ChatRunRegistry(AppDataPaths paths = null, ConversationLeaseService leases = null)
        {
            _leases = leases ?? ConversationLeaseService.ForOffice(paths);
        }

        public ChatRunLease Start(string chatId, string runId, ChatSession session, CancellationTokenSource cancellation = null)
        {
            if (string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(runId))
            {
                throw new InvalidOperationException("Chat and run ids are required.");
            }
            using (_leases.AcquireMaintenance())
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
            return _leases.AnyHeld("run_*.lck");
        }

        public bool IsExternallyRunning(string chatId)
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
