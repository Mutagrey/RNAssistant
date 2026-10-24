using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RNAssistant.Core.Storage
{
    public sealed class ProtectedSecretStore
    {
        public static byte[] ProtectBytes(byte[] bytes)
        {
            return ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        }

        public static byte[] UnprotectBytes(byte[] bytes)
        {
            return ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
        }

        private readonly string _path;

        public ProtectedSecretStore(string path)
        {
            _path = path;
        }

        public string LoadApiKey()
        {
            return LoadSecret();
        }

        public string LoadSecret()
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return string.Empty;
                }

                var protectedBytes = File.ReadAllBytes(_path);
                var bytes = UnprotectBytes(protectedBytes);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (IOException)
            {
                return string.Empty;
            }
            catch (CryptographicException)
            {
                return string.Empty;
            }
        }

        public void SaveApiKey(string apiKey)
        {
            SaveSecret(apiKey);
        }

        public void SaveSecret(string value)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            var protectedBytes = ProtectBytes(bytes);
            StorageFileSystem.WriteAtomic(_path, tempPath => File.WriteAllBytes(tempPath, protectedBytes));
        }
    }
}
