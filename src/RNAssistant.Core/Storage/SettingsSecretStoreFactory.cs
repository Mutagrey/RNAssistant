using System;

namespace RNAssistant.Core.Storage
{
    internal static class SettingsSecretStoreFactory
    {
#if RNA_DPAPI_SECRETS
        internal static ISecretStore CreateApiKeyStore(AppDataPaths paths)
        {
            return new ProtectedSecretStore(paths.SecretFile);
        }

        internal static ISecretStore CreateHistorySecretStore(AppDataPaths paths)
        {
            return new ProtectedSecretStore(paths.HistorySecretFile);
        }
#else
        internal static ISecretStore CreateApiKeyStore(AppDataPaths paths)
        {
            return new EnvironmentSecretStore("RNA_API_KEY", "OPENAI_API_KEY");
        }

        internal static ISecretStore CreateHistorySecretStore(AppDataPaths paths)
        {
            return new EnvironmentSecretStore("RNA_HISTORY_SECRET");
        }

        private sealed class EnvironmentSecretStore : ISecretStore
        {
            private readonly string[] _variableNames;

            internal EnvironmentSecretStore(params string[] variableNames)
            {
                _variableNames = variableNames ?? new string[0];
            }

            public string LoadSecret()
            {
                foreach (var name in _variableNames)
                {
                    var value = Environment.GetEnvironmentVariable(name);
                    if (value != null) return value;
                }
                return string.Empty;
            }

            public void SaveSecret(string value)
            {
                throw new InvalidOperationException(
                    "The net8 CLI profile reads secrets from environment variables and cannot save DPAPI secrets.");
            }
        }
#endif
    }
}
