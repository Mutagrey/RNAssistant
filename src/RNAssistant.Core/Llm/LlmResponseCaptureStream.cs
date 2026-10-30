using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RNAssistant.Core.Llm
{
    // Observes bytes consumed by the existing parser. Never drains/replays the response or claims unread bytes.
    internal sealed class LlmResponseCaptureStream : Stream
    {
        internal sealed class Capture
        {
            internal byte[] Bytes;
            internal long BytesRead;
            internal bool EndOfStream, Truncated;
        }
        private readonly Stream _inner;
        private readonly MemoryStream _buffer = new MemoryStream();
        private readonly object _sync = new object();
        private readonly int _limit;
        private long _read;
        private bool _ended, _disposed;

        internal LlmResponseCaptureStream(Stream inner, int limit = LlmHttpTransport.MaxResponseBodyBytes)
        { _inner = inner ?? throw new ArgumentNullException(nameof(inner)); _limit = Math.Max(0, limit); }

        internal Capture Snapshot()
        {
            lock (_sync) return new Capture { Bytes = _buffer.ToArray(), BytesRead = _read,
                EndOfStream = _ended, Truncated = _read > _buffer.Length };
        }
        private void Record(byte[] buffer, int offset, int count)
        {
            lock (_sync)
            {
                if (_disposed) return;
                if (count == 0) { _ended = true; return; }
                _read += count;
                var keep = Math.Min(count, _limit - (int)_buffer.Length);
                if (keep > 0) _buffer.Write(buffer, offset, keep);
            }
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            if (count > 0) Record(buffer, offset, read);
            return read;
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            var read = await _inner.ReadAsync(buffer, offset, count, token).ConfigureAwait(false);
            if (count > 0) Record(buffer, offset, read);
            return read;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { lock (_sync) _disposed = true; _inner.Dispose(); }
            base.Dispose(disposing);
        }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }
}
