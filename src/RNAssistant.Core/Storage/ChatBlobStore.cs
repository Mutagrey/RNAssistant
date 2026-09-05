using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    public sealed class ChatBlobStore
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private readonly string _rootDirectory;
        private readonly Func<StorageProtector> _protectionProvider;

        public ChatBlobStore(AppDataPaths paths)
            : this(paths, null)
        {
        }

        public ChatBlobStore(AppDataPaths paths, Func<StorageProtector> protectionProvider)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            _rootDirectory = paths.ChatBlobDirectory;
            _protectionProvider = protectionProvider ?? (() => StorageProtector.None);
        }

        public ChatBlobReference StoreText(string value, string contentType)
        {
            return StoreText(value, contentType, null);
        }

        public ChatBlobReference StoreText(string value, string contentType, ChatBlobReference knownReference)
        {
            var bytes = Utf8.GetBytes(value ?? string.Empty);
            var hash = Sha256(bytes);
            var protector = Protection();
            if (knownReference != null && knownReference.ByteLength == bytes.LongLength &&
                string.Equals(knownReference.Sha256, hash, StringComparison.OrdinalIgnoreCase) &&
                HasStoredReference(knownReference, protector))
            {
                return CreateReference(hash, bytes.LongLength, contentType, protector);
            }
            return StoreBytes(bytes, contentType, hash, protector);
        }

        public ChatBlobReference StoreBytes(byte[] bytes, string contentType)
        {
            bytes = bytes ?? new byte[0];
            var hash = Sha256(bytes);
            var protector = Protection();
            return StoreBytes(bytes, contentType, hash, protector);
        }

        public ChatBlobReference StoreFile(string sourcePath, string contentType)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("Source path is required.", "sourcePath");
            using (var source = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
            {
                long byteLength;
                var hash = Sha256Stream(source, out byteLength);
                var protector = Protection();
                var path = PathFor(hash);
                var verified = Matches(path, hash, byteLength, protector);
                if (!verified)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    try
                    {
                        StorageFileSystem.WriteAtomic(path, tempPath =>
                        {
                            CasBlobCodec.EncodeFile(
                                sourcePath,
                                byteLength,
                                contentType,
                                protector,
                                BlobPurpose(hash, byteLength),
                                tempPath);
                            if (!Matches(tempPath, hash, byteLength, protector))
                            {
                                throw new IOException("Streamed CAS payload does not match its source hash.");
                            }
                        });
                        verified = true;
                    }
                    catch (IOException)
                    {
                        verified = Matches(path, hash, byteLength, protector);
                        if (!verified) throw;
                    }
                }
                if (!verified)
                {
                    throw new IOException("Content-addressed blob could not be verified after writing.");
                }
                if (source.Length != byteLength)
                {
                    throw new IOException("CAS source file changed while it was being stored.");
                }
                return CreateReference(hash, byteLength, contentType, protector);
            }
        }

        internal bool HasStoredReference(ChatBlobReference reference)
        {
            return HasStoredReference(reference, Protection());
        }

        internal bool HasVerifiedReference(ChatBlobReference reference)
        {
            if (!ValidReference(reference)) return false;
            var protector = Protection();
            return ProtectionMatches(reference, protector) &&
                Matches(PathFor(reference.Sha256), reference.Sha256, reference.ByteLength, protector);
        }

        internal bool TryGetStoredByteLength(string sha256, out long byteLength)
        {
            byteLength = 0;
            if (!ValidSha256(sha256)) return false;
            try
            {
                var file = new FileInfo(PathFor(sha256));
                if (!file.Exists) return false;
                byteLength = file.Length;
                return true;
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

        private ChatBlobReference StoreBytes(
            byte[] bytes,
            string contentType,
            string hash,
            StorageProtector protector)
        {
            var path = PathFor(hash);
            var verified = Matches(path, hash, bytes.LongLength, protector);
            if (!verified)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var stored = CasBlobCodec.Encode(
                    bytes,
                    contentType,
                    protector,
                    BlobPurpose(hash, bytes.LongLength));
                try
                {
                    StorageFileSystem.WriteAtomic(path, tempPath => File.WriteAllBytes(tempPath, stored));
                    verified = Matches(path, hash, bytes.LongLength, protector);
                }
                catch (IOException)
                {
                    verified = Matches(path, hash, bytes.LongLength, protector);
                    if (!verified) throw;
                }
            }
            if (!verified)
            {
                throw new IOException("Content-addressed blob could not be verified after writing.");
            }
            return CreateReference(hash, bytes.LongLength, contentType, protector);
        }

        public string ReadText(ChatBlobReference reference)
        {
            var bytes = ReadBytes(reference);
            return bytes == null ? null : StrictUtf8.GetString(bytes);
        }

        public byte[] ReadBytes(ChatBlobReference reference)
        {
            if (!ValidReference(reference)) return null;
            var path = PathFor(reference.Sha256);
            if (!File.Exists(path)) return null;
            var protector = Protection();
            if (!ProtectionMatches(reference, protector)) return null;
            try
            {
                var stored = File.ReadAllBytes(path);
                var bytes = CasBlobCodec.Decode(
                    stored,
                    reference.ByteLength,
                    protector,
                    BlobPurpose(reference.Sha256, reference.ByteLength));
                return bytes.LongLength == reference.ByteLength &&
                    string.Equals(Sha256(bytes), reference.Sha256, StringComparison.OrdinalIgnoreCase)
                        ? bytes
                        : null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            catch (CryptographicException)
            {
                return null;
            }
        }

        // Returns only a bounded prefix, but authenticates/decompresses/hashes the
        // whole exact source through the same codec before exposing any bytes.
        public byte[] ReadPrefix(ChatBlobReference reference, int maximumBytes, CancellationToken token = default(CancellationToken))
        {
            if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            token.ThrowIfCancellationRequested();
            if (!ValidReference(reference) || reference.ByteLength > int.MaxValue) return null;
            var protector = Protection();
            if (!ProtectionMatches(reference, protector)) return null;
            return CasBlobCodec.ReadPrefixFile(PathFor(reference.Sha256), reference.ByteLength, reference.Sha256,
                protector, BlobPurpose(reference.Sha256, reference.ByteLength), maximumBytes, token);
        }

        internal string PathFor(string sha256)
        {
            var normalized = (sha256 ?? string.Empty).ToLowerInvariant();
            var prefix = normalized.Length >= 2 ? normalized.Substring(0, 2) : "00";
            return Path.Combine(_rootDirectory, prefix, normalized + ".blob");
        }

        internal static bool ValidReference(ChatBlobReference reference)
        {
            return reference != null && reference.ByteLength >= 0 && ValidSha256(reference.Sha256);
        }

        internal static bool ValidSha256(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
            {
                return false;
            }
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (!((character >= '0' && character <= '9') ||
                    (character >= 'a' && character <= 'f') ||
                    (character >= 'A' && character <= 'F')))
                {
                    return false;
                }
            }
            return true;
        }

        internal bool IsCanonicalPath(string path, string sha256)
        {
            if (!ValidSha256(sha256) || string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                return string.Equals(
                    Path.GetFullPath(path),
                    Path.GetFullPath(PathFor(sha256)),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return false;
            }
        }

        private static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(bytes ?? new byte[0]))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }

        private static string Sha256Stream(FileStream stream, out long byteLength)
        {
            using (var sha = SHA256.Create())
            {
                if (stream == null) throw new ArgumentNullException("stream");
                stream.Position = 0;
                byteLength = stream.Length;
                var hash = sha.ComputeHash(stream);
                if (stream.Position != byteLength)
                {
                    throw new IOException("CAS source file changed while it was being hashed.");
                }
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private bool Matches(string path, string expectedHash, long expectedLength, StorageProtector protector)
        {
            return CasBlobCodec.VerifyFile(
                path,
                expectedLength,
                expectedHash,
                protector,
                BlobPurpose(expectedHash, expectedLength));
        }

        private bool HasStoredReference(ChatBlobReference reference, StorageProtector protector)
        {
            if (!ValidReference(reference)) return false;
            if (!ProtectionMatches(reference, protector)) return false;
            try
            {
                var file = new FileInfo(PathFor(reference.Sha256));
                if (!file.Exists) return false;
                return CasBlobCodec.HasValidEnvelope(file.FullName, reference.ByteLength) ||
                    file.Length == protector.StoredByteLength(reference.ByteLength) ||
                    protector.HasStoredPayloadEnvelope(file.FullName);
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

        private static bool ProtectionMatches(ChatBlobReference reference, StorageProtector protector)
        {
            return reference != null && protector != null &&
                (string.IsNullOrWhiteSpace(reference.Encryption) ||
                    string.Equals(
                        HistoryEncryptionModes.Normalize(reference.Encryption),
                        protector.EncryptionMode,
                        StringComparison.Ordinal)) &&
                (string.IsNullOrWhiteSpace(reference.ProtectionKeyId) ||
                    string.Equals(reference.ProtectionKeyId, protector.KeyId, StringComparison.OrdinalIgnoreCase));
        }

        private static ChatBlobReference CreateReference(
            string hash,
            long byteLength,
            string contentType,
            StorageProtector protector)
        {
            return new ChatBlobReference
            {
                Sha256 = hash,
                ByteLength = byteLength,
                ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
                Encryption = protector.EncryptionMode,
                ProtectionKeyId = protector.Encrypts ? protector.KeyId : null
            };
        }

        private StorageProtector Protection()
        {
            return _protectionProvider() ?? StorageProtector.None;
        }

        private static string BlobPurpose(string hash, long length)
        {
            return "blob|" + (hash ?? string.Empty).ToLowerInvariant() + "|" + length;
        }
    }
}
