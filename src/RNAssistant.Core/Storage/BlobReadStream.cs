using System;
using System.IO;
using System.Threading;

namespace RNAssistant.Core.Storage
{
    // Owns the verified decoding chain and its pinned CAS handle. Never writes or
    // spools plaintext. Compressed/encrypted bodies remain sequential streams.
    internal sealed class BlobReadStream : Stream
    {
        private Stream _inner;
        private readonly Stream[] _owners;
        private readonly CancellationToken _token;
        private readonly long _length;
        private long _position;

        internal BlobReadStream(Stream inner, long length, CancellationToken token, params Stream[] owners)
        { _inner = inner; _length = length; _token = token; _owners = owners; }

        public override bool CanRead { get { return _inner != null; } }
        public override bool CanSeek { get { return _inner != null && _inner.CanSeek; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { RequireOpen(); return _length; } }
        public override long Position
        {
            get { RequireOpen(); return _position; }
            set { Seek(value, SeekOrigin.Begin); }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            RequireOpen();
            _token.ThrowIfCancellationRequested();
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException();
            if (count == 0 || _position == _length) return 0;
            var read = _inner.Read(buffer, offset, (int)Math.Min(Math.Min(count, 81920), _length - _position));
            _token.ThrowIfCancellationRequested();
            if (read == 0) throw new InvalidDataException("Retained CAS stream was truncated during reading.");
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            RequireOpen();
            _token.ThrowIfCancellationRequested();
            if (!_inner.CanSeek) throw new NotSupportedException("Encoded CAS bodies support sequential reading.");
            long target;
            switch (origin)
            {
                case SeekOrigin.Begin: target = offset; break;
                case SeekOrigin.Current: target = checked(_position + offset); break;
                case SeekOrigin.End: target = checked(_length + offset); break;
                default: throw new ArgumentOutOfRangeException(nameof(origin));
            }
            if (target < 0 || target > _length) throw new ArgumentOutOfRangeException(nameof(offset));
            _inner.Position = target;
            return _position = target;
        }

        public override void Flush() { RequireOpen(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _inner != null)
            {
                var inner = _inner;
                _inner = null;
                try { inner.Dispose(); }
                finally { foreach (var owner in _owners) owner.Dispose(); }
            }
            base.Dispose(disposing);
        }

        private void RequireOpen()
        { if (_inner == null) throw new ObjectDisposedException(nameof(BlobReadStream)); }
    }
}
