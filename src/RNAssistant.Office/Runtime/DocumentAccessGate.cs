using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RNAssistant.Office.Runtime
{
    // Synchronous operation ownership is explicit: ambient async execution context is
    // not permission to reenter a document. Only Invoke carries ownership to an STA.
    internal static class DocumentAccessGate
    {
        private const int WaitTimeoutMilliseconds = 10000;
        private const string BusyMessage =
            "Another RNAssistant action is still changing the same state. Retry after it finishes.";
        private static readonly object RegistryLock = new object();
        private static readonly Dictionary<string, GateEntry> Entries =
            new Dictionary<string, GateEntry>(StringComparer.Ordinal);

        [ThreadStatic]
        private static OperationScope _current;

        internal static IDisposable BeginOperation()
        {
            var previous = _current;
            return new OperationScope(previous, new object(), previous == null ? null : previous.Held);
        }

        internal static IDisposable Enter(
            string key,
            object boundGateToken,
            string lockDirectory,
            string lockName,
            bool mayWait,
            CancellationToken token,
            bool isDocumentAccess = true)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A gate key is required.", "key");
            token.ThrowIfCancellationRequested();
            IDisposable implicitOperation = null;
            GateEntry entry = null;
            var semaphoreHeld = false;
            FileStream file = null;
            try
            {
                if (_current == null || _current.Operation == null || _current.TaskId != Task.CurrentId)
                    implicitOperation = BeginOperation();
                var scope = _current;
                var existing = FindHeld(scope.Held, key);
                if (existing != null)
                {
                    if (!ReferenceEquals(existing.Operation, scope.Operation)) throw Busy();
                    ValidateEntry(existing.Entry, boundGateToken, isDocumentAccess);
                    existing.AddReference();
                    return new AccessLease(scope, existing, implicitOperation);
                }
                ValidateOrder(scope, isDocumentAccess);

                var elapsed = Stopwatch.StartNew();
                entry = RetainEntry(key, boundGateToken, isDocumentAccess);
                if (!entry.Semaphore.Wait(mayWait ? RemainingWait(elapsed) : 0, token)) throw Busy();
                semaphoreHeld = true;
                token.ThrowIfCancellationRequested();
                file = AcquireFileLock(lockDirectory, lockName, mayWait, elapsed, token);
                token.ThrowIfCancellationRequested();
                var owner = new HeldGate(key, scope.Operation, entry, file);
                var lease = new AccessLease(scope, owner, implicitOperation);
                entry = null;
                file = null;
                semaphoreHeld = false;
                return lease;
            }
            catch
            {
                try { if (file != null) file.Dispose(); }
                finally
                {
                    try { if (semaphoreHeld) entry.Semaphore.Release(); }
                    finally
                    {
                        try { if (entry != null) ReleaseEntry(entry); }
                        finally { if (implicitOperation != null) implicitOperation.Dispose(); }
                    }
                }
                throw;
            }
        }

        internal static T Invoke<T>(IOfficeStaDispatcher dispatcher, Func<T> action)
        {
            if (dispatcher == null) throw new ArgumentNullException("dispatcher");
            if (action == null) throw new ArgumentNullException("action");
            var source = _current;
            var operation = source != null && source.TaskId == Task.CurrentId ? source.Operation : null;
            var held = source == null ? null : source.Held;
            for (var frame = held; frame != null; frame = frame.Previous)
                if (!frame.Disposed && !ReferenceEquals(frame.Owner.Operation, operation)) throw Busy();
            return dispatcher.Invoke(delegate
            {
                var previous = _current;
                // A reentrant UI callback must not hide an unrelated operation already
                // executing on this STA, even when the caller acquired its own gate.
                for (var frame = previous == null ? null : previous.Held; frame != null; frame = frame.Previous)
                {
                    if (!frame.Disposed && !ContainsOwner(held, frame.Owner)) throw Busy();
                }
                using (new OperationScope(previous, operation, held)) return action();
            });
        }

        private static HeldGate FindHeld(HeldFrame held, string key)
        {
            for (var frame = held; frame != null; frame = frame.Previous)
                if (!frame.Disposed && string.Equals(frame.Owner.Key, key, StringComparison.Ordinal)) return frame.Owner;
            return null;
        }

        private static bool ContainsOwner(HeldFrame held, HeldGate owner)
        {
            for (var frame = held; frame != null; frame = frame.Previous)
                if (!frame.Disposed && ReferenceEquals(frame.Owner, owner)) return true;
            return false;
        }

        private static void ValidateOrder(OperationScope scope, bool isDocumentAccess)
        {
            for (var frame = scope.Held; frame != null; frame = frame.Previous)
            {
                if (frame.Disposed) continue;
                if (!ReferenceEquals(frame.Owner.Operation, scope.Operation) ||
                    isDocumentAccess || !frame.Owner.Entry.IsDocumentAccess) throw Busy();
            }
        }

        private static GateEntry RetainEntry(string key, object boundGateToken, bool isDocumentAccess)
        {
            lock (RegistryLock)
            {
                GateEntry entry;
                if (!Entries.TryGetValue(key, out entry))
                {
                    entry = new GateEntry(key, boundGateToken, isDocumentAccess);
                    Entries.Add(key, entry);
                }
                else ValidateEntryUnderLock(entry, boundGateToken, isDocumentAccess);
                entry.References++;
                return entry;
            }
        }

        private static void ValidateEntry(GateEntry entry, object boundGateToken, bool isDocumentAccess)
        {
            lock (RegistryLock) ValidateEntryUnderLock(entry, boundGateToken, isDocumentAccess);
        }

        private static void ValidateEntryUnderLock(GateEntry entry, object boundGateToken, bool isDocumentAccess)
        {
            if (entry.IsDocumentAccess != isDocumentAccess ||
                entry.BoundToken != null && boundGateToken != null && !ReferenceEquals(entry.BoundToken, boundGateToken))
                throw new HostRuntime.MutationLockException(
                    "RNAssistant received inconsistent gate ownership for the same document or shared state.", false);
            if (entry.BoundToken == null && boundGateToken != null) entry.BoundToken = boundGateToken;
        }

        private static void ReleaseEntry(GateEntry entry)
        {
            lock (RegistryLock)
            {
                if (--entry.References != 0) return;
                Entries.Remove(entry.Key);
                entry.Semaphore.Dispose();
            }
        }

        private static FileStream AcquireFileLock(
            string lockDirectory, string lockName, bool mayWait, Stopwatch elapsed, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(lockDirectory) || string.IsNullOrWhiteSpace(lockName)) return null;
            try { Directory.CreateDirectory(lockDirectory); }
            catch (UnauthorizedAccessException ex)
            {
                throw new HostRuntime.MutationLockException("RNAssistant cannot access its mutation lock directory.", false, ex);
            }
            catch (IOException ex)
            {
                throw new HostRuntime.MutationLockException("RNAssistant cannot access its mutation lock directory.", false, ex);
            }
            var path = Path.Combine(lockDirectory, lockName + ".lck");
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException ex)
                {
                    var wait = mayWait ? Math.Min(100, RemainingWait(elapsed)) : 0;
                    if (wait == 0) throw Busy(ex);
                    if (token.WaitHandle.WaitOne(wait)) token.ThrowIfCancellationRequested();
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw new HostRuntime.MutationLockException("RNAssistant cannot acquire its mutation lock.", false, ex);
                }
            }
        }

        private static int RemainingWait(Stopwatch elapsed)
        {
            return (int)Math.Max(0L, WaitTimeoutMilliseconds - elapsed.ElapsedMilliseconds);
        }

        private static HostRuntime.MutationLockException Busy(Exception cause = null)
        {
            return new HostRuntime.MutationLockException(BusyMessage, true, cause);
        }

        private sealed class GateEntry
        {
            internal readonly string Key;
            internal readonly bool IsDocumentAccess;
            internal readonly SemaphoreSlim Semaphore = new SemaphoreSlim(1, 1);
            internal object BoundToken;
            internal int References;

            internal GateEntry(string key, object boundToken, bool isDocumentAccess)
            {
                Key = key;
                BoundToken = boundToken;
                IsDocumentAccess = isDocumentAccess;
            }
        }

        private sealed class OperationScope : IDisposable
        {
            internal readonly object Operation;
            internal readonly int? TaskId;
            internal HeldFrame Held;
            private readonly OperationScope _previous;
            private int _disposed;

            internal OperationScope(OperationScope previous, object operation, HeldFrame held)
            {
                _previous = previous;
                Operation = operation;
                TaskId = Task.CurrentId;
                Held = held;
                _current = this;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0 || !ReferenceEquals(_current, this)) return;
                var previous = _previous;
                while (previous != null && previous._disposed != 0) previous = previous._previous;
                _current = previous;
            }
        }

        private sealed class HeldGate
        {
            internal readonly string Key;
            internal readonly object Operation;
            internal readonly GateEntry Entry;
            private readonly FileStream _file;
            private int _references = 1;

            internal HeldGate(string key, object operation, GateEntry entry, FileStream file)
            {
                Key = key;
                Operation = operation;
                Entry = entry;
                _file = file;
            }

            internal void AddReference() { Interlocked.Increment(ref _references); }

            internal void Release()
            {
                if (Interlocked.Decrement(ref _references) != 0) return;
                try { if (_file != null) _file.Dispose(); }
                finally
                {
                    try { Entry.Semaphore.Release(); }
                    finally { ReleaseEntry(Entry); }
                }
            }
        }

        private sealed class HeldFrame
        {
            internal readonly HeldGate Owner;
            internal readonly HeldFrame Previous;
            internal bool Disposed;

            internal HeldFrame(HeldGate owner, HeldFrame previous)
            {
                Owner = owner;
                Previous = previous;
            }
        }

        private sealed class AccessLease : IDisposable
        {
            private readonly OperationScope _scope;
            private readonly HeldFrame _frame;
            private readonly IDisposable _implicitOperation;
            private int _disposed;

            internal AccessLease(OperationScope scope, HeldGate owner, IDisposable implicitOperation)
            {
                _scope = scope;
                _frame = new HeldFrame(owner, scope.Held);
                _implicitOperation = implicitOperation;
                scope.Held = _frame;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                _frame.Disposed = true;
                if (ReferenceEquals(_scope.Held, _frame))
                {
                    var previous = _frame.Previous;
                    while (previous != null && previous.Disposed) previous = previous.Previous;
                    _scope.Held = previous;
                }
                try { _frame.Owner.Release(); }
                finally { if (_implicitOperation != null) _implicitOperation.Dispose(); }
            }
        }
    }
}
