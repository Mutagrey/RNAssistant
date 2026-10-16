using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace RNAssistant.Core.Storage
{
    public enum FileTransferStage { Hashing, Compressing, Retaining, Verifying, Staging }

    // Counters belong to one pass, not to a fabricated overall percentage.
    public sealed class FileTransferProgress
    {
        public FileTransferStage Stage { get; private set; }
        public long CompletedBytes { get; private set; }
        public long TotalBytes { get; private set; }

        internal FileTransferProgress(FileTransferStage stage, long completedBytes, long totalBytes)
        { Stage = stage; CompletedBytes = completedBytes; TotalBytes = totalBytes; }
    }

    internal sealed class FileTransferReporter
    {
        private readonly Action<FileTransferProgress> _report;
        private readonly CancellationToken _token;
        private readonly FileTransferStage _stage;
        private readonly long _total;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _lastReport = -100;
        private long _completed;

        internal FileTransferReporter(FileTransferStage stage, long total, CancellationToken token,
            Action<FileTransferProgress> report)
        { _stage = stage; _total = total; _token = token; _report = report; Report(true); }

        internal void Advance(int count) { _completed += count; Report(false); }
        internal void Reset() { _completed = 0; Report(true); }
        internal void Complete() { Report(true); }

        internal static void CopyTo(Stream input, Stream output, CancellationToken token, Action<int> advance)
        {
            var buffer = new byte[81920];
            var remaining = input.CanSeek ? input.Length - input.Position : long.MaxValue;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var count = input.Read(buffer, 0, buffer.Length);
                if (count == 0) break;
                if (count > remaining) throw new IOException("Source grew during streamed retention.");
                remaining -= count;
                output.Write(buffer, 0, count);
                advance?.Invoke(count);
            }
            token.ThrowIfCancellationRequested();
        }

        private void Report(bool force)
        {
            _token.ThrowIfCancellationRequested();
            if (_report != null && (force || _clock.ElapsedMilliseconds - _lastReport >= 100))
            {
                _lastReport = _clock.ElapsedMilliseconds;
                _report(new FileTransferProgress(_stage, _completed, _total));
            }
            _token.ThrowIfCancellationRequested();
        }
    }
}
