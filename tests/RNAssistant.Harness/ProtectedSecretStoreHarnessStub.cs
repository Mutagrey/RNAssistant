using System;
using System.IO;

namespace RNAssistant.Core.Storage
{
    // SettingsService is source-linked; DPAPI is not available in this harness.
    // Only absent fixture secrets are supported. Never simulate encryption or
    // silently consume a secret file: those paths require Windows validation.
    public sealed class ProtectedSecretStore
    {
        private readonly string _path;

        public ProtectedSecretStore(string path) { _path = path; }

        public string LoadApiKey() { return LoadSecret(); }

        public string LoadSecret()
        {
            if (File.Exists(_path)) throw new NotSupportedException("Secret reads require Windows DPAPI validation.");
            return string.Empty;
        }

        public void SaveSecret(string value)
        {
            throw new NotSupportedException("Secret writes require Windows DPAPI validation.");
        }
    }
}
