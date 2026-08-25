using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace RNAssistant.Core.Storage
{
    internal static class CasBlobCodec
    {
        private const int MinimumCompressionBytes = 1024;
        private const int MinimumSavingsBytes = 128;
        private const int HeaderLength = 28;
        private const byte GzipAlgorithm = 1;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("RNACAS01");

        public static byte[] Encode(
            byte[] plaintext,
            string contentType,
            StorageProtector protector,
            string purpose)
        {
            plaintext = plaintext ?? new byte[0];
            protector = protector ?? StorageProtector.None;
            byte[] compressed;
            if (!TryCompress(plaintext, contentType, out compressed))
            {
                return protector.Protect(plaintext, purpose);
            }

            var envelope = Wrap(plaintext.LongLength, compressed);
            return protector.Protect(envelope, purpose);
        }

        public static byte[] Decode(
            byte[] stored,
            long expectedPlaintextLength,
            StorageProtector protector,
            string purpose)
        {
            stored = stored ?? new byte[0];
            protector = protector ?? StorageProtector.None;
            var plaintext = protector.Unprotect(stored, purpose);
            Envelope envelope;
            if (TryParse(plaintext, out envelope) && envelope.PlaintextLength == expectedPlaintextLength)
            {
                try
                {
                    return Decompress(envelope.Payload, expectedPlaintextLength);
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException)
                {
                    // A legacy plaintext blob can coincidentally begin with the envelope magic.
                    // Its canonical plaintext hash is verified by the caller after this fallback.
                }
            }
            return plaintext;
        }

        public static void EncodeFile(
            string sourcePath,
            long plaintextLength,
            string contentType,
            StorageProtector protector,
            string purpose,
            string destinationPath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("Source path is required.", "sourcePath");
            if (plaintextLength < 0) throw new ArgumentOutOfRangeException("plaintextLength");
            if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("Destination path is required.", "destinationPath");
            protector = protector ?? StorageProtector.None;

            string compressedPath = null;
            try
            {
                var encodedSource = TryCreateCompressedEnvelope(
                    sourcePath,
                    plaintextLength,
                    contentType,
                    out compressedPath)
                        ? compressedPath
                        : sourcePath;
                using (var input = new FileStream(
                    encodedSource, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
                using (var output = new FileStream(
                    destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan))
                {
                    protector.ProtectStream(input, output, purpose);
                    output.Flush(true);
                }
            }
            finally
            {
                TryDeleteFile(compressedPath);
            }
        }

        public static bool VerifyFile(
            string path,
            long expectedPlaintextLength,
            string expectedSha256,
            StorageProtector protector,
            string purpose)
        {
            if (string.IsNullOrWhiteSpace(path) || expectedPlaintextLength < 0 ||
                string.IsNullOrWhiteSpace(expectedSha256)) return false;
            protector = protector ?? StorageProtector.None;
            try
            {
                VerificationResult result;
                using (var stored = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
                using (var plaintext = protector.OpenUnprotectedReadStream(stored, purpose))
                {
                    result = VerifyPlaintextStream(plaintext, expectedPlaintextLength, expectedSha256);
                }
                if (result != VerificationResult.RetryAsRaw)
                {
                    return result == VerificationResult.Valid;
                }
                using (var stored = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
                using (var plaintext = protector.OpenUnprotectedReadStream(stored, purpose))
                {
                    return VerifyRawStream(null, 0, plaintext, expectedPlaintextLength, expectedSha256);
                }
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (CryptographicException)
            {
                return false;
            }
        }

        public static bool HasValidEnvelope(string path, long expectedPlaintextLength)
        {
            if (string.IsNullOrWhiteSpace(path) || expectedPlaintextLength < 0) return false;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (stream.Length < HeaderLength) return false;
                    var header = new byte[HeaderLength];
                    var offset = 0;
                    while (offset < header.Length)
                    {
                        var read = stream.Read(header, offset, header.Length - offset);
                        if (read <= 0) return false;
                        offset += read;
                    }
                    EnvelopeHeader parsed;
                    return TryParseHeader(header, stream.Length, out parsed) &&
                        parsed.PlaintextLength == expectedPlaintextLength;
                }
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static bool TryCompress(byte[] plaintext, string contentType, out byte[] compressed)
        {
            compressed = null;
            if (plaintext.Length < MinimumCompressionBytes || !Compressible(contentType)) return false;
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true))
                {
                    gzip.Write(plaintext, 0, plaintext.Length);
                }
                compressed = output.ToArray();
            }
            var requiredSavings = Math.Max(MinimumSavingsBytes, plaintext.Length / 20);
            if (compressed.LongLength + HeaderLength > plaintext.LongLength - requiredSavings)
            {
                compressed = null;
                return false;
            }
            return true;
        }

        private static bool TryCreateCompressedEnvelope(
            string sourcePath,
            long plaintextLength,
            string contentType,
            out string compressedPath)
        {
            compressedPath = null;
            if (plaintextLength < MinimumCompressionBytes || !Compressible(contentType)) return false;
            var sourceDirectory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
            compressedPath = Path.Combine(
                sourceDirectory,
                "." + Path.GetFileName(sourcePath) + "." + Guid.NewGuid().ToString("N") + ".cas-gzip.tmp");
            using (var output = new FileStream(
                compressedPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.SequentialScan))
            {
                WriteEnvelopeHeader(output, plaintextLength, 0);
                using (var input = new FileStream(
                    sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
                using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true))
                {
                    input.CopyTo(gzip, 81920);
                }
                var payloadLength = output.Length - HeaderLength;
                if (payloadLength > int.MaxValue)
                {
                    throw new IOException("CAS compression envelope exceeds the supported size.");
                }
                var requiredSavings = Math.Max(MinimumSavingsBytes, plaintextLength / 20);
                if (output.Length > plaintextLength - requiredSavings) return false;
                output.Position = 20;
                using (var writer = new BinaryWriter(output, Encoding.UTF8, true))
                {
                    writer.Write(payloadLength);
                    writer.Flush();
                }
                output.Flush(true);
                return true;
            }
        }

        private static void WriteEnvelopeHeader(Stream output, long plaintextLength, long payloadLength)
        {
            using (var writer = new BinaryWriter(output, Encoding.UTF8, true))
            {
                writer.Write(Magic);
                writer.Write(GzipAlgorithm);
                writer.Write((byte)0);
                writer.Write((ushort)0);
                writer.Write(plaintextLength);
                writer.Write(payloadLength);
                writer.Flush();
            }
        }

        private static VerificationResult VerifyPlaintextStream(
            Stream plaintext,
            long expectedLength,
            string expectedSha256)
        {
            var prefix = new byte[HeaderLength];
            var prefixLength = ReadUpTo(plaintext, prefix, 0, prefix.Length);
            EnvelopeHeader header;
            if (prefixLength != HeaderLength || !TryParseHeaderPrefix(prefix, out header) ||
                header.PlaintextLength != expectedLength)
            {
                return VerifyRawStream(prefix, prefixLength, plaintext, expectedLength, expectedSha256)
                    ? VerificationResult.Valid
                    : VerificationResult.Invalid;
            }

            try
            {
                if (expectedLength > int.MaxValue)
                {
                    throw new InvalidDataException("CAS blob plaintext length is unsupported.");
                }
                using (var payload = new LimitedReadStream(plaintext, header.PayloadLength))
                {
                    bool hashMatches;
                    using (var gzip = new GZipStream(payload, CompressionMode.Decompress, true))
                    {
                        hashMatches = HashStream(gzip, expectedLength, expectedSha256);
                    }
                    Drain(payload);
                    if (payload.Remaining != 0)
                    {
                        throw new InvalidDataException("CAS compression envelope is truncated.");
                    }
                    if (plaintext.ReadByte() >= 0)
                    {
                        throw new InvalidDataException("CAS compression envelope has trailing bytes.");
                    }
                    return hashMatches ? VerificationResult.Valid : VerificationResult.Invalid;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException)
            {
                // Preserve legacy raw blobs that coincidentally start with RNACAS01.
                return VerificationResult.RetryAsRaw;
            }
        }

        private static bool VerifyRawStream(
            byte[] prefix,
            int prefixLength,
            Stream remainder,
            long expectedLength,
            string expectedSha256)
        {
            using (var sha = SHA256.Create())
            {
                var total = 0L;
                if (prefixLength > 0)
                {
                    if (prefixLength > expectedLength) return false;
                    sha.TransformBlock(prefix, 0, prefixLength, prefix, 0);
                    total = prefixLength;
                }
                var buffer = new byte[81920];
                while (true)
                {
                    var read = remainder.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    if (total > expectedLength - read) return false;
                    sha.TransformBlock(buffer, 0, read, buffer, 0);
                    total += read;
                }
                if (total != expectedLength) return false;
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return string.Equals(Hex(sha.Hash), expectedSha256, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static bool HashStream(
            Stream stream,
            long expectedLength,
            string expectedSha256)
        {
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[81920];
                var total = 0L;
                while (true)
                {
                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    if (total > expectedLength - read)
                    {
                        throw new InvalidDataException("CAS blob expands beyond its declared plaintext length.");
                    }
                    sha.TransformBlock(buffer, 0, read, buffer, 0);
                    total += read;
                }
                if (total != expectedLength)
                {
                    throw new InvalidDataException("CAS blob plaintext length does not match its reference.");
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return string.Equals(Hex(sha.Hash), expectedSha256, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static int ReadUpTo(Stream stream, byte[] buffer, int offset, int count)
        {
            var total = 0;
            while (total < count)
            {
                var read = stream.Read(buffer, offset + total, count - total);
                if (read <= 0) break;
                total += read;
            }
            return total;
        }

        private static void Drain(Stream stream)
        {
            var buffer = new byte[81920];
            while (stream.Read(buffer, 0, buffer.Length) > 0)
            {
            }
        }

        private static string Hex(byte[] value)
        {
            return BitConverter.ToString(value ?? new byte[0]).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static void TryDeleteFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static bool Compressible(string contentType)
        {
            var value = (contentType ?? string.Empty).Trim().ToLowerInvariant();
            return value.StartsWith("text/", StringComparison.Ordinal) ||
                value.IndexOf("json", StringComparison.Ordinal) >= 0 ||
                value.IndexOf("xml", StringComparison.Ordinal) >= 0 ||
                value.IndexOf("javascript", StringComparison.Ordinal) >= 0 ||
                value.IndexOf("yaml", StringComparison.Ordinal) >= 0 ||
                value.IndexOf("csv", StringComparison.Ordinal) >= 0 ||
                value.IndexOf("vba", StringComparison.Ordinal) >= 0;
        }

        private static byte[] Wrap(long plaintextLength, byte[] payload)
        {
            payload = payload ?? new byte[0];
            if (payload.Length > int.MaxValue - HeaderLength)
            {
                throw new IOException("CAS compression envelope exceeds the supported size.");
            }
            using (var output = new MemoryStream(HeaderLength + payload.Length))
            using (var writer = new BinaryWriter(output, Encoding.UTF8, true))
            {
                writer.Write(Magic);
                writer.Write(GzipAlgorithm);
                writer.Write((byte)0);
                writer.Write((ushort)0);
                writer.Write(plaintextLength);
                writer.Write((long)payload.Length);
                writer.Write(payload);
                writer.Flush();
                return output.ToArray();
            }
        }

        private static bool TryParse(byte[] stored, out Envelope envelope)
        {
            envelope = null;
            EnvelopeHeader header;
            if (!TryParseHeader(stored, stored == null ? 0 : stored.LongLength, out header)) return false;
            var payload = new byte[header.PayloadLength];
            Buffer.BlockCopy(stored, HeaderLength, payload, 0, payload.Length);
            envelope = new Envelope
            {
                PlaintextLength = header.PlaintextLength,
                Payload = payload
            };
            return true;
        }

        private static bool TryParseHeader(byte[] bytes, long storedLength, out EnvelopeHeader header)
        {
            if (!TryParseHeaderPrefix(bytes, out header) || storedLength < HeaderLength) return false;
            return HeaderLength + header.PayloadLength == storedLength;
        }

        private static bool TryParseHeaderPrefix(byte[] bytes, out EnvelopeHeader header)
        {
            header = null;
            if (bytes == null || bytes.Length < HeaderLength) return false;
            for (var index = 0; index < Magic.Length; index++)
            {
                if (bytes[index] != Magic[index]) return false;
            }
            if (bytes[8] != GzipAlgorithm || bytes[9] != 0 || bytes[10] != 0 || bytes[11] != 0) return false;
            var plaintextLength = BitConverter.ToInt64(bytes, 12);
            var payloadLength = BitConverter.ToInt64(bytes, 20);
            if (plaintextLength < 0 || payloadLength < 0 || payloadLength > int.MaxValue) return false;
            header = new EnvelopeHeader
            {
                PlaintextLength = plaintextLength,
                PayloadLength = (int)payloadLength
            };
            return true;
        }

        private static byte[] Decompress(byte[] compressed, long expectedLength)
        {
            if (expectedLength < 0 || expectedLength > int.MaxValue)
            {
                throw new InvalidDataException("CAS blob plaintext length is unsupported.");
            }
            using (var input = new MemoryStream(compressed ?? new byte[0], false))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress, false))
            using (var output = new MemoryStream((int)Math.Min(expectedLength, 1024L * 1024)))
            {
                var buffer = new byte[8192];
                while (true)
                {
                    var read = gzip.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    if (output.Length + read > expectedLength)
                    {
                        throw new InvalidDataException("CAS blob expands beyond its declared plaintext length.");
                    }
                    output.Write(buffer, 0, read);
                }
                if (output.Length != expectedLength)
                {
                    throw new InvalidDataException("CAS blob plaintext length does not match its reference.");
                }
                return output.ToArray();
            }
        }

        private sealed class EnvelopeHeader
        {
            public long PlaintextLength { get; set; }
            public int PayloadLength { get; set; }
        }

        private sealed class Envelope
        {
            public long PlaintextLength { get; set; }
            public byte[] Payload { get; set; }
        }

        private enum VerificationResult
        {
            Invalid,
            Valid,
            RetryAsRaw
        }

        private sealed class LimitedReadStream : Stream
        {
            private readonly Stream _inner;
            private readonly long _length;
            private long _position;

            public LimitedReadStream(Stream inner, long length)
            {
                _inner = inner;
                _length = length;
            }

            public long Remaining { get { return _length - _position; } }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return _length; } }
            public override long Position
            {
                get { return _position; }
                set { throw new NotSupportedException(); }
            }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var remaining = Remaining;
                if (remaining <= 0) return 0;
                var read = _inner.Read(buffer, offset, (int)Math.Min(count, remaining));
                if (read > 0) _position += read;
                return read;
            }

            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
    }
}
