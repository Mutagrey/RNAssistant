using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    public sealed partial class ChatBlobStore
    {
        // One shared entry replaces the former per-instance encoding fingerprint.
        // It never substitutes file existence, current stored-byte verification or
        // exact protection identity. Limits are defensive caps, not latency claims.
        internal const int MaximumCachedBodyBytes = 1024 * 1024;
        private const int MaximumCachedEncodingBytes = MaximumCachedBodyBytes + 65536;
        private static readonly object ReadCacheSync = new object();
        private static volatile VerifiedReadState _verifiedRead;
        internal static long CachedBodyDecodes, CachedBodyHits, CachedStoredBytes, CachedTextIndexes;

        private VerifiedReadState ReadCachedBody(ChatBlobReference reference, StorageProtector protector, CancellationToken token)
        {
            if (reference.ByteLength > MaximumCachedBodyBytes) return null;
            while (!Monitor.TryEnter(ReadCacheSync, 25)) token.ThrowIfCancellationRequested();
            try
            {
                token.ThrowIfCancellationRequested();
                var path = PathFor(reference.Sha256);
                using (var stored = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
                {
                    if (stored.Length > MaximumCachedEncodingBytes) return null;
                    var cached = _verifiedRead;
                    if (cached != null && cached.Body != null && cached.Path == path && cached.PlaintextHash == reference.Sha256 &&
                        cached.PlaintextLength == reference.ByteLength && ReferenceEquals(cached.Protector, protector))
                    {
                        Interlocked.Add(ref CachedStoredBytes, stored.Length);
                        if (CasBlobCodec.VerifyStoredBytes(stored, stored.Length, cached.StoredHash, token))
                        { Interlocked.Increment(ref CachedBodyHits); return cached; }
                        stored.Position = 0;
                    }
                    var encoded = new byte[checked((int)stored.Length)];
                    var offset = 0;
                    while (offset < encoded.Length)
                    {
                        token.ThrowIfCancellationRequested();
                        var count = stored.Read(encoded, offset, Math.Min(81920, encoded.Length - offset));
                        if (count == 0) throw new InvalidDataException("CAS encoding changed during verification.");
                        offset += count;
                    }
                    if (stored.ReadByte() >= 0) throw new InvalidDataException("CAS encoding grew during verification.");
                    Interlocked.Add(ref CachedStoredBytes, offset);
                    var storedHash = Sha256(encoded);
                    using (var source = new MemoryStream(encoded, false))
                    using (var body = new MemoryStream(checked((int)reference.ByteLength)))
                    {
                        Interlocked.Increment(ref CachedBodyDecodes);
                        if (!CasBlobCodec.VerifyStream(source, reference.ByteLength, reference.Sha256, protector,
                            BlobPurpose(reference.Sha256, reference.ByteLength), body, checked((int)reference.ByteLength), token))
                            throw new InvalidDataException("Retained CAS body does not match its exact reference.");
                        cached = new VerifiedReadState(path, reference.Sha256, reference.ByteLength, protector, storedHash, body.ToArray());
                        _verifiedRead = cached;
                        return cached;
                    }
                }
            }
            finally { Monitor.Exit(ReadCacheSync); }
        }

        internal sealed class VerifiedReadState
        {
            internal readonly string Path, PlaintextHash, StoredHash;
            internal readonly long PlaintextLength;
            internal readonly StorageProtector Protector;
            internal readonly byte[] Body;
            private string _text;
            private int[] _lines;
            private readonly object _textSync = new object();
            internal VerifiedReadState(string path, string hash, long length, StorageProtector protector, string storedHash, byte[] body)
            { Path = path; PlaintextHash = hash; PlaintextLength = length; Protector = protector; StoredHash = storedHash; Body = body; }

            internal string ReadText()
            { lock (_textSync) return _text ?? (_text = StrictUtf8.GetString(Body)); }

            internal void ReadWindow(ResourceTextWindow window, CancellationToken token)
            {
                lock (_textSync)
                {
                    token.ThrowIfCancellationRequested();
                    if (_lines == null)
                    {
                        var text = ReadText();
                        var lines = new List<int> { 0 };
                        for (var index = 0; index < text.Length; index++)
                        {
                            if ((index & 8191) == 0) token.ThrowIfCancellationRequested();
                            if (text[index] == '\n' || text[index] == '\r' && (index + 1 == text.Length || text[index + 1] != '\n')) lines.Add(index + 1);
                        }
                        _lines = lines.ToArray(); _text = text;
                        Interlocked.Increment(ref CachedTextIndexes);
                    }
                    window.ReadIndexed(_text, _lines, token);
                }
            }
        }

        // A live lease pins its immutable entry even if the one cache slot changes.
        // It exposes no writable array and releases its pin on Dispose.
        internal sealed class CachedReadStream : Stream
        {
            private VerifiedReadState _entry;
            private readonly CancellationToken _token;
            private long _position;
            internal CachedReadStream(VerifiedReadState entry, CancellationToken token) { _entry = entry; _token = token; }
            private VerifiedReadState Entry { get { _token.ThrowIfCancellationRequested(); return _entry ?? throw new ObjectDisposedException(nameof(CachedReadStream)); } }
            public override bool CanRead { get { return _entry != null; } }
            public override bool CanSeek { get { return _entry != null; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return Entry.Body.LongLength; } }
            public override long Position { get { var entry = Entry; return _position; } set { Seek(value, SeekOrigin.Begin); } }
            public override int Read(byte[] buffer, int offset, int count)
            {
                var entry = Entry;
                if (buffer == null || offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentException("Invalid read buffer.");
                var length = (int)Math.Min(count, Math.Max(0, entry.Body.LongLength - _position));
                if (length != 0) Buffer.BlockCopy(entry.Body, (int)_position, buffer, offset, length);
                _position += length; return length;
            }
            public override long Seek(long offset, SeekOrigin origin)
            {
                var entry = Entry;
                if (origin != SeekOrigin.Begin && origin != SeekOrigin.Current && origin != SeekOrigin.End) throw new ArgumentOutOfRangeException(nameof(origin));
                var next = checked((origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? _position : entry.Body.LongLength) + offset);
                if (next < 0) throw new IOException("Cannot seek before the source.");
                return _position = next;
            }
            internal void ReadWindow(ResourceTextWindow window, CancellationToken token)
            { var entry = Entry; entry.ReadWindow(window, token); _position = entry.Body.LongLength; }
            public override void Flush() { }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            protected override void Dispose(bool disposing) { Interlocked.Exchange(ref _entry, null); base.Dispose(disposing); }
        }
    }
}
