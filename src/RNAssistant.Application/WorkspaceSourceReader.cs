using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Storage;

namespace RNAssistant.Application
{
    // Runtime-only capability. It retains one accepted source pin and the storage
    // activity/reader slot until all I/O drains. Never accepts a path or ResourceRef
    // from a consumer. All expensive operations run outside application/UI locks.
    public sealed class WorkspaceSourceReader : IDisposable
    {
        public const int MaximumReadBytes = 256 * 1024;
        private readonly object _sync = new object();
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private readonly TaskCompletionSource<bool> _drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task _operation = Task.CompletedTask;
        private CancellationTokenRegistration _registration;
        private Stream _stream;
        private IDisposable _activity, _slot;
        private bool _closed, _ready;
        private long _requestedBytes;

        public long ByteLength { get; private set; }
        public string Label { get; private set; }
        public bool IsClosed { get { lock (_sync) return _closed; } }
        public Task Drained { get { return _drained.Task; } }

        internal WorkspaceSourceReader(long byteLength, string label)
        { ByteLength = byteLength; Label = label; }

        internal Task<WorkspaceSourceReader> OpenAsync(AppDataPaths paths,
            Func<CancellationToken, Stream> open, CancellationToken token)
        {
            lock (_sync)
            {
                var operation = Task.Run(() =>
                {
                    try
                    {
                        _activity = RuntimeStorageActivity.Acquire(paths, _cancel.Token);
                        _slot = RuntimeStorageActivity.AcquireSourceReader(paths, _cancel.Token);
                        _stream = open(_cancel.Token);
                        if (!_stream.CanSeek || _stream.Length != ByteLength)
                            throw new InvalidDataException("Prepared source does not match the seekable read contract.");
                        lock (_sync)
                        {
                            _cancel.Token.ThrowIfCancellationRequested();
                            _ready = true;
                        }
                        return this;
                    }
                    catch { CloseAsync(); throw; }
                });
                _operation = operation;
                // Register under the same lock as cleanup. Even an already cancelled
                // token cannot let cleanup run before this registration is assigned.
                _registration = token.Register(() => CloseAsync());
                return CompleteOpenAsync(operation);
            }
        }

        private async Task<WorkspaceSourceReader> CompleteOpenAsync(Task<WorkspaceSourceReader> operation)
        {
            try { return await operation.ConfigureAwait(false); }
            catch
            {
                // Failed admission gives the caller no reader to dispose. Settle
                // cleanup before exposing failure or allowing the next source.
                await CloseAsync().ConfigureAwait(false);
                throw;
            }
        }

        public Task<byte[]> ReadRangeAsync(long offset, int count)
        {
            lock (_sync)
            {
                if (_closed) throw new ObjectDisposedException(nameof(WorkspaceSourceReader));
                if (!_ready || !_operation.IsCompleted)
                    throw Invalid("source_read_pending", "Предыдущее чтение источника ещё не завершено.");
                if (offset < 0 || count < 1 || count > MaximumReadBytes || offset > ByteLength - count)
                    throw Invalid("source_range_invalid", "Запрошен недопустимый диапазон источника.");
                // Bound repeated small reads as well as single allocations. The
                // portable ZIP reader uses the same three-pass amplification budget.
                var maximumBytes = ByteLength > long.MaxValue / 3 ? long.MaxValue : ByteLength * 3;
                if (_requestedBytes > maximumBytes - count)
                    throw Invalid("source_read_limit", "Достигнут лимит повторного чтения источника.");
                _requestedBytes += count;
                var operation = Task.Run(() =>
                {
                    var buffer = new byte[count];
                    try
                    {
                        _cancel.Token.ThrowIfCancellationRequested();
                        _stream.Position = offset;
                        var read = 0;
                        while (read < count)
                        {
                            _cancel.Token.ThrowIfCancellationRequested();
                            var size = _stream.Read(buffer, read, count - read);
                            if (size == 0) throw new InvalidDataException("Prepared source was truncated.");
                            read += size;
                        }
                        _cancel.Token.ThrowIfCancellationRequested();
                        return buffer;
                    }
                    catch { Array.Clear(buffer, 0, buffer.Length); CloseAsync(); throw; }
                });
                _operation = operation;
                return operation;
            }
        }

        // Revocation is synchronous; closing file handles and releasing maintenance
        // happens after the currently running read/open finishes, outside the caller.
        public Task CloseAsync()
        {
            lock (_sync)
            {
                if (_closed) return Drained;
                _closed = true;
                _cancel.Cancel();
                var operation = _operation;
                Task.Run(async () =>
                {
                    try { await operation.ConfigureAwait(false); }
                    catch { /* Read/open failure is reported by that operation. */ }
                    try
                    {
                        CancellationTokenRegistration registration;
                        lock (_sync) registration = _registration;
                        try { _stream?.Dispose(); }
                        finally
                        {
                            try { _slot?.Dispose(); }
                            finally
                            {
                                try { _activity?.Dispose(); }
                                finally { registration.Dispose(); _cancel.Dispose(); }
                            }
                        }
                        _drained.TrySetResult(true);
                    }
                    catch (Exception error) { _drained.TrySetException(error); }
                });
                return Drained;
            }
        }

        public void Dispose() { CloseAsync(); }
        private static WorkspaceApplicationException Invalid(string code, string message)
        { return new WorkspaceApplicationException(code, message); }
    }
}
