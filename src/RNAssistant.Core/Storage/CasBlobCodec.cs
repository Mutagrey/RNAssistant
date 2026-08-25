using System;
using System.IO;
using System.IO.Compression;
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
            header = null;
            if (bytes == null || bytes.Length < HeaderLength || storedLength < HeaderLength) return false;
            for (var index = 0; index < Magic.Length; index++)
            {
                if (bytes[index] != Magic[index]) return false;
            }
            if (bytes[8] != GzipAlgorithm || bytes[9] != 0 || bytes[10] != 0 || bytes[11] != 0) return false;
            var plaintextLength = BitConverter.ToInt64(bytes, 12);
            var payloadLength = BitConverter.ToInt64(bytes, 20);
            if (plaintextLength < 0 || payloadLength < 0 || payloadLength > int.MaxValue ||
                HeaderLength + payloadLength != storedLength) return false;
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
    }
}
