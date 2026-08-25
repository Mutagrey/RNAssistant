using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    public sealed class StorageProtector
    {
        private const int Pbkdf2Iterations = 100000;
        private const int KeyIdLength = 16;
        private const int IvLength = 16;
        private const int TagLength = 32;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("RNAENC01");
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly byte[] EmptyBytes = new byte[0];

        private readonly byte[] _encryptionKey;
        private readonly byte[] _authenticationKey;
        private readonly byte[] _eventChainKey;

        public static readonly StorageProtector None = new StorageProtector(
            HistoryIntegrityModes.Sha256,
            HistoryEncryptionModes.None,
            null,
            null);

        public string IntegrityMode { get; private set; }
        public string EncryptionMode { get; private set; }
        public string KeyId { get; private set; }

        public bool UsesHmac
        {
            get { return string.Equals(IntegrityMode, HistoryIntegrityModes.HmacSha256, StringComparison.Ordinal); }
        }

        public bool Encrypts
        {
            get { return !string.Equals(EncryptionMode, HistoryEncryptionModes.None, StringComparison.Ordinal); }
        }

        public string CurrentHashAlgorithm
        {
            get { return UsesHmac ? HistoryIntegrityModes.HmacSha256 : HistoryIntegrityModes.Sha256; }
        }

        public StorageProtector(
            string integrityMode,
            string encryptionMode,
            string secret,
            byte[] salt)
        {
            IntegrityMode = HistoryIntegrityModes.Normalize(integrityMode);
            EncryptionMode = HistoryEncryptionModes.Normalize(encryptionMode);
            if (!RequiresSecret(IntegrityMode, EncryptionMode))
            {
                KeyId = null;
                return;
            }
            if (string.IsNullOrWhiteSpace(secret))
            {
                throw new InvalidOperationException("History protection is enabled, but its key secret is unavailable.");
            }
            if (salt == null || salt.Length < 16)
            {
                throw new InvalidOperationException("History protection salt is unavailable or invalid.");
            }

            byte[] master;
            using (var derive = new Rfc2898DeriveBytes(secret, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256))
            {
                master = derive.GetBytes(32);
            }
            _encryptionKey = DeriveKey(master, "rnassistant/history/encryption/v1");
            _authenticationKey = DeriveKey(master, "rnassistant/history/authentication/v1");
            _eventChainKey = DeriveKey(master, "rnassistant/history/event-chain/v1");
            var idBytes = DeriveKey(master, "rnassistant/history/key-id/v1");
            KeyId = Hex(idBytes).Substring(0, KeyIdLength);
            Array.Clear(master, 0, master.Length);
        }

        public static bool RequiresSecret(string integrityMode, string encryptionMode)
        {
            return string.Equals(HistoryIntegrityModes.Normalize(integrityMode), HistoryIntegrityModes.HmacSha256, StringComparison.Ordinal) ||
                !string.Equals(HistoryEncryptionModes.Normalize(encryptionMode), HistoryEncryptionModes.None, StringComparison.Ordinal);
        }

        public byte[] Protect(byte[] plaintext, string purpose)
        {
            plaintext = plaintext ?? new byte[0];
            if (!Encrypts) return plaintext;

            var iv = new byte[IvLength];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(iv);
            }

            var storedLength = StoredByteLength(plaintext.LongLength);
            if (storedLength > int.MaxValue)
            {
                throw new CryptographicException("Encrypted history payload exceeds the supported size.");
            }
            var result = new byte[(int)storedLength];
            var offset = 0;
            Buffer.BlockCopy(Magic, 0, result, offset, Magic.Length);
            offset += Magic.Length;
            var keyIdBytes = Encoding.ASCII.GetBytes(KeyId);
            Buffer.BlockCopy(keyIdBytes, 0, result, offset, keyIdBytes.Length);
            offset += keyIdBytes.Length;
            Buffer.BlockCopy(iv, 0, result, offset, iv.Length);
            offset += iv.Length;

            var ciphertextLength = result.Length - offset - TagLength;
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.BlockSize = 128;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = _encryptionKey;
                aes.IV = iv;
                using (var transform = aes.CreateEncryptor())
                {
                    var remainder = plaintext.Length % transform.InputBlockSize;
                    var blockLength = plaintext.Length - remainder;
                    var written = blockLength == 0
                        ? 0
                        : transform.TransformBlock(plaintext, 0, blockLength, result, offset);
                    var final = transform.TransformFinalBlock(plaintext, blockLength, remainder);
                    Buffer.BlockCopy(final, 0, result, offset + written, final.Length);
                    if (written + final.Length != ciphertextLength)
                    {
                        throw new CryptographicException("Encrypted history payload length is invalid.");
                    }
                }
            }

            var bodyLength = result.Length - TagLength;
            var tag = AuthenticationTag(purpose, result, 0, bodyLength);
            Buffer.BlockCopy(tag, 0, result, bodyLength, tag.Length);
            return result;
        }

        internal void ProtectStream(Stream plaintext, Stream stored, string purpose)
        {
            if (plaintext == null) throw new ArgumentNullException("plaintext");
            if (stored == null) throw new ArgumentNullException("stored");
            if (!plaintext.CanRead) throw new ArgumentException("Plaintext stream must be readable.", "plaintext");
            if (!stored.CanWrite) throw new ArgumentException("Stored stream must be writable.", "stored");
            if (!Encrypts)
            {
                plaintext.CopyTo(stored, 81920);
                return;
            }

            var iv = new byte[IvLength];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(iv);
            using (var hmac = new HMACSHA256(_authenticationKey))
            {
                InitializeAuthentication(hmac, purpose);
                var authenticated = new HmacWriteStream(stored, hmac);
                authenticated.Write(Magic, 0, Magic.Length);
                var keyIdBytes = Encoding.ASCII.GetBytes(KeyId);
                authenticated.Write(keyIdBytes, 0, keyIdBytes.Length);
                authenticated.Write(iv, 0, iv.Length);

                using (var aes = Aes.Create())
                {
                    aes.KeySize = 256;
                    aes.BlockSize = 128;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.Key = _encryptionKey;
                    aes.IV = iv;
                    using (var transform = aes.CreateEncryptor())
                    using (var crypto = new CryptoStream(authenticated, transform, CryptoStreamMode.Write, true))
                    {
                        plaintext.CopyTo(crypto, 81920);
                        crypto.FlushFinalBlock();
                    }
                }

                hmac.TransformFinalBlock(EmptyBytes, 0, 0);
                var tag = hmac.Hash;
                stored.Write(tag, 0, tag.Length);
            }
        }

        internal long StoredByteLength(long plaintextByteLength)
        {
            if (plaintextByteLength < 0) throw new ArgumentOutOfRangeException("plaintextByteLength");
            if (!Encrypts) return plaintextByteLength;
            var cipherBlocks = checked(plaintextByteLength / IvLength + 1);
            return checked(Magic.Length + KeyIdLength + IvLength + TagLength + cipherBlocks * IvLength);
        }

        internal bool HasStoredPayloadEnvelope(string path)
        {
            if (!Encrypts || string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                using (var stream = new System.IO.FileStream(
                    path,
                    System.IO.FileMode.Open,
                    System.IO.FileAccess.Read,
                    System.IO.FileShare.ReadWrite))
                {
                    var fixedLength = Magic.Length + KeyIdLength + IvLength + TagLength;
                    var ciphertextLength = stream.Length - fixedLength;
                    if (ciphertextLength < IvLength || ciphertextLength % IvLength != 0) return false;
                    var header = new byte[Magic.Length + KeyIdLength];
                    var offset = 0;
                    while (offset < header.Length)
                    {
                        var read = stream.Read(header, offset, header.Length - offset);
                        if (read <= 0) return false;
                        offset += read;
                    }
                    for (var index = 0; index < Magic.Length; index++)
                    {
                        if (header[index] != Magic[index]) return false;
                    }
                    var storedKeyId = Encoding.ASCII.GetString(header, Magic.Length, KeyIdLength);
                    return string.Equals(storedKeyId, KeyId, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (System.IO.IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        public byte[] Unprotect(byte[] stored, string purpose)
        {
            stored = stored ?? new byte[0];
            if (!Encrypts) return stored;
            if (!IsProtectedPayload(stored))
            {
                throw new CryptographicException("Encrypted history contains an unprotected payload.");
            }
            var minimum = Magic.Length + KeyIdLength + IvLength + 16 + TagLength;
            if (stored.Length < minimum)
            {
                throw new CryptographicException("Encrypted history payload is truncated.");
            }

            var offset = Magic.Length;
            var storedKeyId = Encoding.ASCII.GetString(stored, offset, KeyIdLength);
            offset += KeyIdLength;
            if (!string.Equals(storedKeyId, KeyId, StringComparison.OrdinalIgnoreCase))
            {
                throw new CryptographicException("History payload was encrypted with another key.");
            }

            var bodyLength = stored.Length - TagLength;
            var expectedTag = AuthenticationTag(purpose, stored, 0, bodyLength);
            if (!SecureEquals(expectedTag, 0, stored, bodyLength, TagLength))
            {
                throw new CryptographicException("Encrypted history payload authentication failed.");
            }

            var iv = new byte[IvLength];
            Buffer.BlockCopy(stored, offset, iv, 0, iv.Length);
            offset += IvLength;
            var ciphertextLength = bodyLength - offset;
            if (ciphertextLength < IvLength || ciphertextLength % IvLength != 0)
            {
                throw new CryptographicException("Encrypted history payload ciphertext is invalid.");
            }
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.BlockSize = 128;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = _encryptionKey;
                aes.IV = iv;
                using (var transform = aes.CreateDecryptor())
                {
                    return transform.TransformFinalBlock(stored, offset, ciphertextLength);
                }
            }
        }

        internal void UnprotectToStream(Stream stored, Stream plaintext, string purpose)
        {
            if (stored == null) throw new ArgumentNullException("stored");
            if (plaintext == null) throw new ArgumentNullException("plaintext");
            if (!stored.CanRead) throw new ArgumentException("Stored stream must be readable.", "stored");
            if (!plaintext.CanWrite) throw new ArgumentException("Plaintext stream must be writable.", "plaintext");
            if (!Encrypts)
            {
                stored.CopyTo(plaintext, 81920);
                return;
            }
            using (var unprotected = OpenUnprotectedReadStream(stored, purpose))
            {
                unprotected.CopyTo(plaintext, 81920);
            }
        }

        internal Stream OpenUnprotectedReadStream(Stream stored, string purpose)
        {
            if (stored == null) throw new ArgumentNullException("stored");
            if (!stored.CanRead) throw new ArgumentException("Stored stream must be readable.", "stored");
            if (!Encrypts)
            {
                if (!stored.CanSeek) throw new ArgumentException("Stored stream must be seekable.", "stored");
                return new BoundedReadStream(stored, stored.Length - stored.Position);
            }
            if (!stored.CanSeek)
            {
                throw new CryptographicException("Encrypted history stream must be seekable for authentication.");
            }

            var envelope = AuthenticateStream(stored, purpose);
            stored.Position = envelope.CiphertextOffset;
            return new DecryptingReadStream(
                stored,
                envelope.CiphertextLength,
                _encryptionKey,
                envelope.Iv);
        }

        private EncryptedStreamEnvelope AuthenticateStream(Stream stored, string purpose)
        {
            var start = stored.Position;
            var totalLength = stored.Length - start;
            var headerLength = Magic.Length + KeyIdLength + IvLength;
            var minimum = headerLength + IvLength + TagLength;
            if (totalLength < minimum)
            {
                throw new CryptographicException("Encrypted history payload is truncated.");
            }

            var header = new byte[headerLength];
            ReadExactly(stored, header, 0, header.Length);
            for (var index = 0; index < Magic.Length; index++)
            {
                if (header[index] != Magic[index])
                {
                    throw new CryptographicException("Encrypted history contains an unprotected payload.");
                }
            }
            var storedKeyId = Encoding.ASCII.GetString(header, Magic.Length, KeyIdLength);
            if (!string.Equals(storedKeyId, KeyId, StringComparison.OrdinalIgnoreCase))
            {
                throw new CryptographicException("History payload was encrypted with another key.");
            }

            var bodyLength = totalLength - TagLength;
            var ciphertextLength = bodyLength - headerLength;
            if (ciphertextLength < IvLength || ciphertextLength % IvLength != 0)
            {
                throw new CryptographicException("Encrypted history payload ciphertext is invalid.");
            }

            var buffer = new byte[81920];
            using (var hmac = new HMACSHA256(_authenticationKey))
            {
                InitializeAuthentication(hmac, purpose);
                stored.Position = start;
                var remaining = bodyLength;
                while (remaining > 0)
                {
                    var read = stored.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read <= 0) throw new CryptographicException("Encrypted history payload is truncated.");
                    hmac.TransformBlock(buffer, 0, read, buffer, 0);
                    remaining -= read;
                }
                hmac.TransformFinalBlock(EmptyBytes, 0, 0);
                var actualTag = new byte[TagLength];
                ReadExactly(stored, actualTag, 0, actualTag.Length);
                if (!SecureEquals(hmac.Hash, 0, actualTag, 0, TagLength))
                {
                    throw new CryptographicException("Encrypted history payload authentication failed.");
                }
            }

            var iv = new byte[IvLength];
            Buffer.BlockCopy(header, Magic.Length + KeyIdLength, iv, 0, iv.Length);
            return new EncryptedStreamEnvelope
            {
                CiphertextOffset = start + headerLength,
                CiphertextLength = ciphertextLength,
                Iv = iv
            };
        }

        public string ComputeEventHash(byte[] canonicalBytes, string algorithm, string keyId)
        {
            canonicalBytes = canonicalBytes ?? new byte[0];
            algorithm = HistoryIntegrityModes.Normalize(algorithm);
            if (string.Equals(algorithm, HistoryIntegrityModes.HmacSha256, StringComparison.Ordinal))
            {
                if (_eventChainKey == null || string.IsNullOrWhiteSpace(KeyId) ||
                    !string.Equals(keyId, KeyId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new CryptographicException("History event HMAC key is unavailable or does not match.");
                }
                using (var hmac = new HMACSHA256(_eventChainKey))
                {
                    return Hex(hmac.ComputeHash(canonicalBytes));
                }
            }
            using (var sha = SHA256.Create())
            {
                return Hex(sha.ComputeHash(canonicalBytes));
            }
        }

        public static bool IsProtectedPayload(byte[] value)
        {
            if (value == null || value.Length < Magic.Length) return false;
            for (var index = 0; index < Magic.Length; index++)
            {
                if (value[index] != Magic[index]) return false;
            }
            return true;
        }

        public static byte[] NewSalt()
        {
            var salt = new byte[32];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(salt);
            }
            return salt;
        }

        private byte[] AuthenticationTag(string purpose, byte[] body, int offset, int length)
        {
            using (var hmac = new HMACSHA256(_authenticationKey))
            {
                InitializeAuthentication(hmac, purpose);
                if (length > 0) hmac.TransformBlock(body, offset, length, body, offset);
                hmac.TransformFinalBlock(EmptyBytes, 0, 0);
                return hmac.Hash;
            }
        }

        private static void InitializeAuthentication(HMAC hmac, string purpose)
        {
            var purposeBytes = Utf8.GetBytes(purpose ?? string.Empty);
            var separator = new byte[1];
            if (purposeBytes.Length > 0)
            {
                hmac.TransformBlock(purposeBytes, 0, purposeBytes.Length, purposeBytes, 0);
            }
            hmac.TransformBlock(separator, 0, separator.Length, separator, 0);
        }

        private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                var read = stream.Read(buffer, offset, count);
                if (read <= 0) throw new CryptographicException("Encrypted history payload is truncated.");
                offset += read;
                count -= read;
            }
        }

        private static byte[] DeriveKey(byte[] master, string purpose)
        {
            using (var hmac = new HMACSHA256(master))
            {
                return hmac.ComputeHash(Utf8.GetBytes(purpose ?? string.Empty));
            }
        }

        private static bool SecureEquals(
            byte[] left,
            int leftOffset,
            byte[] right,
            int rightOffset,
            int length)
        {
            if (left == null || right == null || length < 0 ||
                leftOffset < 0 || rightOffset < 0 ||
                leftOffset + length > left.Length || rightOffset + length > right.Length) return false;
            var difference = 0;
            for (var index = 0; index < length; index++)
            {
                difference |= left[leftOffset + index] ^ right[rightOffset + index];
            }
            return difference == 0;
        }

        private static string Hex(byte[] value)
        {
            return BitConverter.ToString(value ?? new byte[0]).Replace("-", string.Empty).ToLowerInvariant();
        }

        private sealed class HmacWriteStream : Stream
        {
            private readonly Stream _inner;
            private readonly HMAC _hmac;

            public HmacWriteStream(Stream inner, HMAC hmac)
            {
                _inner = inner;
                _hmac = hmac;
            }

            public override bool CanRead { get { return false; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { return _inner.Length; } }
            public override long Position
            {
                get { return _inner.Position; }
                set { throw new NotSupportedException(); }
            }

            public override void Flush() { _inner.Flush(); }
            public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (count <= 0) return;
                _hmac.TransformBlock(buffer, offset, count, buffer, offset);
                _inner.Write(buffer, offset, count);
            }
        }

        private sealed class EncryptedStreamEnvelope
        {
            public long CiphertextOffset { get; set; }
            public long CiphertextLength { get; set; }
            public byte[] Iv { get; set; }
        }

        private sealed class DecryptingReadStream : Stream
        {
            private readonly BoundedReadStream _bounded;
            private readonly Aes _aes;
            private readonly ICryptoTransform _transform;
            private readonly CryptoStream _crypto;

            public DecryptingReadStream(Stream stored, long ciphertextLength, byte[] key, byte[] iv)
            {
                _bounded = new BoundedReadStream(stored, ciphertextLength);
                _aes = Aes.Create();
                _aes.KeySize = 256;
                _aes.BlockSize = 128;
                _aes.Mode = CipherMode.CBC;
                _aes.Padding = PaddingMode.PKCS7;
                _aes.Key = key;
                _aes.IV = iv;
                _transform = _aes.CreateDecryptor();
                _crypto = new CryptoStream(_bounded, _transform, CryptoStreamMode.Read);
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position
            {
                get { throw new NotSupportedException(); }
                set { throw new NotSupportedException(); }
            }

            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) { return _crypto.Read(buffer, offset, count); }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _crypto.Dispose();
                    _transform.Dispose();
                    _aes.Dispose();
                    _bounded.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        private sealed class BoundedReadStream : Stream
        {
            private readonly Stream _inner;
            private readonly long _length;
            private long _position;

            public BoundedReadStream(Stream inner, long length)
            {
                _inner = inner;
                _length = length;
            }

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
                var remaining = _length - _position;
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
