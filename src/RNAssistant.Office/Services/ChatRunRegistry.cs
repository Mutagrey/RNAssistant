using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RNAssistant.Core.Models;

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
    }

    internal sealed class ChatRunRegistry
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, ChatRunSnapshot> _runs =
            new Dictionary<string, ChatRunSnapshot>(StringComparer.OrdinalIgnoreCase);

        public ChatRunLease Start(string chatId, string runId, ChatSession session, CancellationTokenSource cancellation = null)
        {
            if (string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(runId))
            {
                throw new InvalidOperationException("Chat and run ids are required.");
            }
            lock (_sync)
            {
                ChatRunSnapshot existing;
                if (_runs.TryGetValue(chatId, out existing))
                {
                    throw new InvalidOperationException("В этом чате уже выполняется запрос.");
                }
                var run = new ChatRunSnapshot
                {
                    ChatId = chatId,
                    RunId = runId,
                    Status = "running",
                    Phase = "starting",
                    StartedUtc = DateTime.UtcNow,
                    Session = session,
                    Cancellation = cancellation
                };
                _runs[chatId] = run;
                return new ChatRunLease(this, chatId, runId);
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
            lock (_sync)
            {
                ChatRunSnapshot run;
                return _runs.TryGetValue(chatId ?? string.Empty, out run) ? Clone(run) : null;
            }
        }

        public bool IsRunning(string chatId)
        {
            return Get(chatId) != null;
        }

        public bool HasRuns()
        {
            lock (_sync) return _runs.Count > 0;
        }

        public bool IsDocumentRunning(string host, string documentKey)
        {
            lock (_sync)
            {
                return _runs.Values.Any(item => item.Session != null &&
                    string.Equals(item.Session.Host, host, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Session.DocumentKey, documentKey, StringComparison.OrdinalIgnoreCase));
            }
        }

        public IReadOnlyList<ChatSession> Sessions()
        {
            lock (_sync)
            {
                return _runs.Values.Where(item => item.Session != null).Select(item => item.Session).ToList();
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
                }
            }
        }

        public void Clear()
        {
            List<CancellationTokenSource> cancellations;
            lock (_sync)
            {
                cancellations = _runs.Values
                    .Where(run => run.Cancellation != null)
                    .Select(run => run.Cancellation)
                    .ToList();
                _runs.Clear();
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
                finally
                {
                    cancellation.Dispose();
                }
            }
        }

        private static ChatRunSnapshot Clone(ChatRunSnapshot source)
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
