using System;
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
            var purposeBytes = Utf8.GetBytes(purpose ?? string.Empty);
            var separator = new byte[1];
            using (var hmac = new HMACSHA256(_authenticationKey))
            {
                if (purposeBytes.Length > 0)
                {
                    hmac.TransformBlock(purposeBytes, 0, purposeBytes.Length, purposeBytes, 0);
                }
                hmac.TransformBlock(separator, 0, separator.Length, separator, 0);
                if (length > 0) hmac.TransformBlock(body, offset, length, body, offset);
                hmac.TransformFinalBlock(EmptyBytes, 0, 0);
                return hmac.Hash;
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
    }
}
