using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    internal sealed class ChatBlobStore
    {
        private readonly string _rootDirectory;

        public ChatBlobStore(AppDataPaths paths)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            _rootDirectory = paths.ChatBlobDirectory;
        }

        public ChatBlobReference StoreText(string value, string contentType)
        {
            return StoreBytes(new UTF8Encoding(false).GetBytes(value ?? string.Empty), contentType);
        }

        public ChatBlobReference StoreBytes(byte[] bytes, string contentType)
        {
            bytes = bytes ?? new byte[0];
            var hash = Sha256(bytes);
            var path = PathFor(hash);
            if (!Matches(path, hash, bytes.LongLength))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                try
                {
                    StorageFileSystem.WriteAtomic(path, tempPath => File.WriteAllBytes(tempPath, bytes));
                }
                catch (IOException)
                {
                    if (!Matches(path, hash, bytes.LongLength)) throw;
                }
            }
            if (!Matches(path, hash, bytes.LongLength))
            {
                throw new IOException("Content-addressed blob could not be verified after writing.");
            }

            return new ChatBlobReference
            {
                Sha256 = hash,
                ByteLength = bytes.LongLength,
                ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType
            };
        }

        public string ReadText(ChatBlobReference reference)
        {
            var bytes = ReadBytes(reference);
            return bytes == null ? null : new UTF8Encoding(false, true).GetString(bytes);
        }

        public byte[] ReadBytes(ChatBlobReference reference)
        {
            if (!ValidReference(reference)) return null;
            var path = PathFor(reference.Sha256);
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            return bytes.LongLength == reference.ByteLength &&
                string.Equals(Sha256(bytes), reference.Sha256, StringComparison.OrdinalIgnoreCase)
                    ? bytes
                    : null;
        }

        private string PathFor(string sha256)
        {
            var normalized = (sha256 ?? string.Empty).ToLowerInvariant();
            var prefix = normalized.Length >= 2 ? normalized.Substring(0, 2) : "00";
            return Path.Combine(_rootDirectory, prefix, normalized + ".blob");
        }

        private static bool ValidReference(ChatBlobReference reference)
        {
            if (reference == null || reference.ByteLength < 0 || string.IsNullOrWhiteSpace(reference.Sha256) || reference.Sha256.Length != 64)
            {
                return false;
            }
            for (var index = 0; index < reference.Sha256.Length; index++)
            {
                var character = reference.Sha256[index];
                if (!((character >= '0' && character <= '9') ||
                    (character >= 'a' && character <= 'f') ||
                    (character >= 'A' && character <= 'F')))
                {
                    return false;
                }
            }
            return true;
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

        private static bool Matches(string path, string expectedHash, long expectedLength)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length != expectedLength) return false;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var sha = SHA256.Create())
                {
                    var actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
                    return string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase);
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
    }
}
