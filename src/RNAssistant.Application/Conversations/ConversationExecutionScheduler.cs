using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Storage;

namespace RNAssistant.Application.Conversations
{
    // Only live permits/waiters are shared. No second Run, queue journal or store.
    internal sealed class ConversationExecutionScheduler : IRunExecutionAdmission
    {
        private static readonly object SharedSync = new object();
        private static readonly Dictionary<string, SharedEntry> Shared = new Dictionary<string, SharedEntry>(
            Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        private sealed class SharedEntry
        {
            internal ConversationExecutionScheduler Scheduler;
            internal int Users;
        }

        internal static IRunExecutionAdmission ForStorage(AppDataPaths paths)
        { return new StorageAdmission(paths); }

        private sealed class StorageAdmission : IRunExecutionAdmission
        {
            private readonly string _root;
            private readonly SettingsService _settings;
            internal StorageAdmission(AppDataPaths paths)
            { _root = Path.GetFullPath(paths.Root); _settings = new SettingsService(paths); }

            public async Task<IDisposable> EnterAsync(CancellationToken token)
            {
                SharedEntry entry;
                lock (SharedSync)
                {
                    if (!Shared.TryGetValue(_root, out entry))
                    {
                        entry = new SharedEntry { Scheduler = new ConversationExecutionScheduler(
                            () => _settings.Load().MaxConcurrentAgentSteps) };
                        Shared.Add(_root, entry);
                    }
                    entry.Users++;
                }
                try
                {
                    var permit = await entry.Scheduler.EnterAsync(token).ConfigureAwait(false);
                    return new ReleaseOnce(() => { try { permit.Dispose(); } finally { Release(entry); } });
                }
                catch { Release(entry); throw; }
            }

            private void Release(SharedEntry entry)
            {
                lock (SharedSync)
                    if (--entry.Users == 0) Shared.Remove(_root);
            }
        }

        private readonly object _sync = new object();
        private readonly LinkedList<Waiter> _queue = new LinkedList<Waiter>();
        private readonly Func<int> _limit;
        private int _active;

        internal ConversationExecutionScheduler(Func<int> limit)
        { _limit = limit ?? throw new ArgumentNullException(nameof(limit)); }
        internal int ActiveCount { get { lock (_sync) return _active; } }
        internal int QueuedCount { get { lock (_sync) return _queue.Count; } }

        public Task<IDisposable> EnterAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var waiter = new Waiter();
            lock (_sync)
            {
                waiter.Node = _queue.AddLast(waiter);
                Pump();
            }
            return AwaitPermitAsync(waiter, token);
        }

        private async Task<IDisposable> AwaitPermitAsync(Waiter waiter, CancellationToken token)
        {
            using (token.Register(() =>
            {
                lock (_sync)
                {
                    if (waiter.Node == null) return;
                    _queue.Remove(waiter.Node); waiter.Node = null;
                    waiter.Result.TrySetCanceled();
                }
            })) return await waiter.Result.Task.ConfigureAwait(false);
        }

        // Called under _sync. FIFO includes agents asking for their next step.
        // Lowering the setting never interrupts a running model/tool/read-back.
        private void Pump()
        {
            int limit;
            try
            {
                limit = _limit();
                if (limit < 0 || limit > 64) throw new InvalidOperationException("Invalid concurrent step limit.");
            }
            catch (Exception error)
            {
                while (_queue.First != null)
                {
                    var refused = _queue.First.Value;
                    _queue.RemoveFirst(); refused.Node = null;
                    refused.Result.TrySetException(error);
                }
                return;
            }
            while (_queue.First != null && (limit == 0 || _active < limit))
            {
                var waiter = _queue.First.Value;
                _queue.RemoveFirst(); waiter.Node = null; _active++;
                waiter.Result.SetResult(new ReleaseOnce(() => { lock (_sync) { _active--; Pump(); } }));
            }
        }

        private sealed class Waiter
        {
            internal LinkedListNode<Waiter> Node;
            internal readonly TaskCompletionSource<IDisposable> Result = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private sealed class ReleaseOnce : IDisposable
        {
            private Action _release;
            internal ReleaseOnce(Action release) { _release = release; }
            public void Dispose() { Interlocked.Exchange(ref _release, null)?.Invoke(); }
        }
    }
}
