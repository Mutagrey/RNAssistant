using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace RNAssistant.Core.Storage
{
    // Transient random access over an already verified sequential CAS stream.
    // No manifest, reopening or recovery path: the sole owner deletes the scratch
    // handle on close. Each block preserves storage protection and its exact hash.
    internal sealed class SeekableBlobReadStream : Stream
    {
        internal const int BlockBytes = 256 * 1024;
        private readonly FileStream _scratch;
        private readonly StorageProtector _protector;
        private readonly CancellationToken _token;
        private readonly string _purpose;
        private readonly long _length, _storedBlockBytes;
        private readonly List<byte[]> _hashes = new List<byte[]>();
        private byte[] _block;
        private long _position, _blockIndex = -1;
        private bool _disposed;

        private SeekableBlobReadStream(FileStream scratch, StorageProtector protector,
            string purpose, long length, CancellationToken token)
        {
            _scratch = scratch; _protector = protector; _purpose = purpose;
            _length = length; _token = token;
            _storedBlockBytes = protector.StoredByteLength(BlockBytes);
        }

        // Consumes the input on both success and failure, even when no staging is needed.
        internal static Stream Create(Stream input, string directory, StorageProtector protector,
            CancellationToken token, Action<FileTransferProgress> progress)
        {
            if (input.CanSeek) return input;
            SeekableBlobReadStream staged = null;
            try
            {
                using (input)
                {
                    token.ThrowIfCancellationRequested();
                    var length = input.Length;
                    StorageFileSystem.EnsureRegularDirectory(directory);
                    var id = Guid.NewGuid().ToString("N");
                    var scratch = new FileStream(Path.Combine(directory, "read-" + id + ".tmp"),
                        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
                        FileOptions.RandomAccess | FileOptions.DeleteOnClose);
                    staged = new SeekableBlobReadStream(scratch, protector, "cas-seek/" + id, length, token);
                    var reporter = new FileTransferReporter(FileTransferStage.Staging, length, token, progress);
                    using (var sha = SHA256.Create())
                    {
                        for (long copied = 0; copied < length;)
                        {
                            var block = new byte[(int)Math.Min(BlockBytes, length - copied)];
                            try
                            {
                                ReadExactly(input, block, token);
                                staged._hashes.Add(sha.ComputeHash(block));
                                var stored = protector.Protect(block, staged.BlockPurpose(staged._hashes.Count - 1));
                                try { scratch.Write(stored, 0, stored.Length); }
                                finally { if (!ReferenceEquals(stored, block)) Array.Clear(stored, 0, stored.Length); }
                                copied += block.Length;
                                reporter.Advance(block.Length);
                            }
                            finally { Array.Clear(block, 0, block.Length); }
                        }
                    }
                    scratch.Flush();
                    reporter.Complete();
                }
                return staged;
            }
            catch { staged?.Dispose(); throw; }
        }

        public override bool CanRead { get { return !_disposed; } }
        public override bool CanSeek { get { return !_disposed; } }
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
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException();
            count = (int)Math.Min(Math.Min(count, BlockBytes), _length - _position);
            var copied = 0;
            while (copied < count)
            {
                RequireOpen();
                var index = _position / BlockBytes;
                if (_blockIndex != index) LoadBlock(index);
                var start = (int)(_position % BlockBytes);
                var take = Math.Min(count - copied, _block.Length - start);
                Buffer.BlockCopy(_block, start, buffer, offset + copied, take);
                copied += take; _position += take;
            }
            _token.ThrowIfCancellationRequested();
            return copied;
        }

        private void LoadBlock(long index)
        {
            ClearBlock();
            var length = (int)Math.Min(BlockBytes, _length - index * BlockBytes);
            var stored = new byte[(int)_protector.StoredByteLength(length)];
            try
            {
                _scratch.Position = checked(index * _storedBlockBytes);
                ReadExactly(_scratch, stored, _token);
                _block = _protector.Unprotect(stored, BlockPurpose(index));
                using (var sha = SHA256.Create())
                {
                    var expected = _hashes[checked((int)index)];
                    var actual = sha.ComputeHash(_block);
                    var mismatch = _block.Length != length;
                    for (var i = 0; i < expected.Length; i++) mismatch |= expected[i] != actual[i];
                    if (mismatch) throw new InvalidDataException("Source read staging does not match its verified block.");
                }
                _blockIndex = index;
            }
            catch { ClearBlock(); throw; }
            finally { if (!ReferenceEquals(stored, _block)) Array.Clear(stored, 0, stored.Length); }
        }

        private string BlockPurpose(long index)
        { return _purpose + "/" + index.ToString(CultureInfo.InvariantCulture); }

        private static void ReadExactly(Stream stream, byte[] buffer, CancellationToken token)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                token.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, offset, buffer.Length - offset);
                if (read == 0) throw new InvalidDataException("Source read staging was truncated.");
                offset += read;
            }
            token.ThrowIfCancellationRequested();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            RequireOpen();
            long target;
            switch (origin)
            {
                case SeekOrigin.Begin: target = offset; break;
                case SeekOrigin.Current: target = checked(_position + offset); break;
                case SeekOrigin.End: target = checked(_length + offset); break;
                default: throw new ArgumentOutOfRangeException(nameof(origin));
            }
            if (target < 0 || target > _length) throw new ArgumentOutOfRangeException(nameof(offset));
            return _position = target;
        }

        public override void Flush() { RequireOpen(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                ClearBlock();
                _hashes.Clear();
                _scratch.Dispose();
            }
            base.Dispose(disposing);
        }
        private void ClearBlock()
        { if (_block != null) Array.Clear(_block, 0, _block.Length); _block = null; _blockIndex = -1; }
        private void RequireOpen()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SeekableBlobReadStream));
            _token.ThrowIfCancellationRequested();
        }
    }
}
