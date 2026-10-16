using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RNAssistant.Application
{
    // A native browser may read its response streams on another thread. Retain only
    // unconsumed bodies, bounded independently of workbook size, and revoke on close.
    public sealed class TabularResponseBodyPool : IDisposable
    {
        public const int MaximumBytes = 2 * 1024 * 1024;
        private readonly object _sync = new object();
        private readonly HashSet<Body> _bodies = new HashSet<Body>();
        private long _bytes;
        private bool _closed;
        public long RetainedBytes { get { lock (_sync) return _bytes; } }

        // Ownership transfers only on success; the caller disposes refused responses.
        public Stream Take(TabularResourceResponse response)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            lock (_sync)
            {
                if (_closed) throw new ObjectDisposedException(nameof(TabularResponseBodyPool));
                if (response.Body.Length > MaximumBytes - _bytes || _bodies.Count >= 64)
                    throw new WorkspaceApplicationException("tabular_response_limit", "Unconsumed resource responses exceed the budget.");
                var body = new Body(response, Release);
                _bodies.Add(body); _bytes += body.Length;
                return body;
            }
        }
        private void Release(Body body)
        { lock (_sync) { if (_bodies.Remove(body)) _bytes -= body.Length; } }
        public void Dispose()
        {
            Body[] bodies;
            lock (_sync) { _closed = true; bodies = _bodies.ToArray(); }
            // Never hold the pool lock while taking a stream lock.
            foreach (var body in bodies) body.Dispose();
        }

        private sealed class Body : Stream
        {
            private readonly object _sync = new object();
            private TabularResourceResponse _response;
            private readonly Action<Body> _release;
            private readonly long _length;
            private long _position;
            public Body(TabularResourceResponse response, Action<Body> release)
            { _response = response; _release = release; _length = response.Body.Length; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (buffer == null) throw new ArgumentNullException(nameof(buffer));
                if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException();
                lock (_sync)
                {
                    if (_response == null) return 0;
                    var read = _response.Body.Read(buffer, offset, count); _position += read;
                    if (_position == _length) Dispose();
                    return read;
                }
            }
            protected override void Dispose(bool disposing)
            {
                lock (_sync)
                {
                    if (_response != null) { _response.Dispose(); _response = null; _release(this); }
                }
                base.Dispose(disposing);
            }
            public override bool CanRead { get { return true; } }
            public override bool CanWrite { get { return false; } }
            public override bool CanSeek { get { return false; } }
            public override long Length { get { return _length; } }
            public override long Position { get { lock (_sync) return _position; } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
    }
}
