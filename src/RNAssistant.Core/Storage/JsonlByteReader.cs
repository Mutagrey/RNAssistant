using System;
using System.IO;
using System.Text;

namespace RNAssistant.Core.Storage
{
    internal sealed class JsonlByteReader : IDisposable
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private readonly FileStream _stream;
        private readonly byte[] _buffer;
        private readonly long _length;
        private int _bufferIndex;
        private int _bufferCount;
        private long _position;

        public JsonlByteReader(string path)
            : this(path, 0)
        {
        }

        public JsonlByteReader(string path, long startOffset)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("JSONL path is required.", "path");
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _length = _stream.Length;
            if (startOffset < 0 || startOffset > _length)
            {
                _stream.Dispose();
                throw new ArgumentOutOfRangeException("startOffset");
            }
            _buffer = new byte[8192];
            _stream.Position = startOffset;
            _position = startOffset;
        }

        public long Length { get { return _length; } }

        public long Position { get { return _position; } }

        public JsonlByteLine ReadLine()
        {
            if (_position >= _length) return null;
            var startOffset = _position;
            using (var content = new MemoryStream())
            {
                while (true)
                {
                    if (_bufferIndex >= _bufferCount && !FillBuffer())
                    {
                        if (_position == startOffset) return null;
                        return Line(startOffset, false, content);
                    }

                    var segmentStart = _bufferIndex;
                    var terminatorIndex = -1;
                    for (var index = _bufferIndex; index < _bufferCount; index++)
                    {
                        if (_buffer[index] == (byte)'\n' || _buffer[index] == (byte)'\r')
                        {
                            terminatorIndex = index;
                            break;
                        }
                    }

                    if (terminatorIndex < 0)
                    {
                        var count = _bufferCount - segmentStart;
                        content.Write(_buffer, segmentStart, count);
                        _bufferIndex = _bufferCount;
                        _position += count;
                        continue;
                    }

                    var contentCount = terminatorIndex - segmentStart;
                    if (contentCount > 0) content.Write(_buffer, segmentStart, contentCount);
                    var terminator = _buffer[terminatorIndex];
                    _bufferIndex = terminatorIndex + 1;
                    _position += contentCount + 1;

                    if (terminator == (byte)'\r')
                    {
                        byte next;
                        if (TryPeekByte(out next) && next == (byte)'\n')
                        {
                            _bufferIndex += 1;
                            _position += 1;
                        }
                    }
                    return Line(startOffset, true, content);
                }
            }
        }

        public void Dispose()
        {
            _stream.Dispose();
        }

        private bool FillBuffer()
        {
            if (_position >= _length) return false;
            var requested = (int)Math.Min(_buffer.Length, _length - _position);
            var read = 0;
            while (read < requested)
            {
                var count = _stream.Read(_buffer, read, requested - read);
                if (count <= 0) break;
                read += count;
            }
            _bufferIndex = 0;
            _bufferCount = read;
            return read > 0;
        }

        private bool TryPeekByte(out byte value)
        {
            value = 0;
            if (_bufferIndex >= _bufferCount && !FillBuffer()) return false;
            value = _buffer[_bufferIndex];
            return true;
        }

        private JsonlByteLine Line(long startOffset, bool terminated, MemoryStream content)
        {
            var bytes = content.GetBuffer();
            return new JsonlByteLine
            {
                Offset = startOffset,
                NextOffset = _position,
                Terminated = terminated,
                Text = StrictUtf8.GetString(bytes, 0, checked((int)content.Length))
            };
        }
    }

    internal sealed class JsonlByteLine
    {
        public long Offset { get; set; }
        public long NextOffset { get; set; }
        public bool Terminated { get; set; }
        public string Text { get; set; }
    }
}
