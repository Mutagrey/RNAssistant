using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RNAssistant.Core.Storage
{
    public sealed class ProtectedSecretStore
    {
        private readonly string _path;

        public ProtectedSecretStore(string path)
        {
            _path = path;
        }

        public string LoadApiKey()
        {
            if (!File.Exists(_path))
            {
                return string.Empty;
            }

            var protectedBytes = File.ReadAllBytes(_path);
            var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }

        public void SaveApiKey(string apiKey)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var bytes = Encoding.UTF8.GetBytes(apiKey ?? string.Empty);
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_path, protectedBytes);
        }
    }
}

