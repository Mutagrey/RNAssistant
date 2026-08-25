using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    internal sealed class ChatBlobStore
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
            return StoreBytes(Utf8.GetBytes(value ?? string.Empty), contentType);
        }

        public ChatBlobReference StoreBytes(byte[] bytes, string contentType)
        {
            bytes = bytes ?? new byte[0];
            var hash = Sha256(bytes);
            var protector = Protection();
            var path = PathFor(hash);
            var verified = Matches(path, hash, bytes.LongLength, protector);
            if (!verified)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var stored = protector.Protect(bytes, BlobPurpose(hash, bytes.LongLength));
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

            return new ChatBlobReference
            {
                Sha256 = hash,
                ByteLength = bytes.LongLength,
                ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
                Encryption = protector.EncryptionMode,
                ProtectionKeyId = protector.Encrypts ? protector.KeyId : null
            };
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
            if (!string.IsNullOrWhiteSpace(reference.Encryption) &&
                !string.Equals(HistoryEncryptionModes.Normalize(reference.Encryption), protector.EncryptionMode, StringComparison.Ordinal)) return null;
            if (!string.IsNullOrWhiteSpace(reference.ProtectionKeyId) &&
                !string.Equals(reference.ProtectionKeyId, protector.KeyId, StringComparison.OrdinalIgnoreCase)) return null;
            try
            {
                var stored = File.ReadAllBytes(path);
                var bytes = protector.Unprotect(stored, BlobPurpose(reference.Sha256, reference.ByteLength));
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

        private bool Matches(string path, string expectedHash, long expectedLength, StorageProtector protector)
        {
            try
            {
                if (!File.Exists(path)) return false;
                var stored = File.ReadAllBytes(path);
                var bytes = protector.Unprotect(stored, BlobPurpose(expectedHash, expectedLength));
                return bytes.LongLength == expectedLength &&
                    string.Equals(Sha256(bytes), expectedHash, StringComparison.OrdinalIgnoreCase);
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
