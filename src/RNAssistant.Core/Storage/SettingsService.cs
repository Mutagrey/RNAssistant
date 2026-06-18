using RNAssistant.Core.Models;
using System;
using System.Collections.Generic;

namespace RNAssistant.Core.Storage
{
    public sealed class SettingsService
    {
        private readonly AppDataPaths _paths;
        private readonly JsonFileStore _json;
        private readonly ProtectedSecretStore _secretStore;

        public SettingsService(AppDataPaths paths)
        {
            _paths = paths;
            _json = new JsonFileStore();
            _secretStore = new ProtectedSecretStore(paths.SecretFile);
        }

        public AppSettings Load()
        {
            return Normalize(_json.Load(_paths.SettingsFile, new AppSettings()));
        }

        public void Save(AppSettings settings)
        {
            _json.Save(_paths.SettingsFile, Normalize(settings ?? new AppSettings()));
        }

        public string LoadApiKey()
        {
            return _secretStore.LoadApiKey();
        }

        public void SaveApiKey(string apiKey)
        {
            _secretStore.SaveApiKey(apiKey);
        }

        private static AppSettings Normalize(AppSettings settings)
        {
            var defaults = new AppSettings();
            if (settings.CustomHeaders == null)
            {
                settings.CustomHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            if (string.IsNullOrWhiteSpace(settings.BaseUrl))
            {
                settings.BaseUrl = defaults.BaseUrl;
            }
            if (string.IsNullOrWhiteSpace(settings.Model))
            {
                settings.Model = defaults.Model;
            }
            if (string.IsNullOrWhiteSpace(settings.SystemPrompt))
            {
                settings.SystemPrompt = defaults.SystemPrompt;
            }
            if (settings.MaxTokens <= 0)
            {
                settings.MaxTokens = defaults.MaxTokens;
            }
            if (settings.ContextCharLimit <= 0)
            {
                settings.ContextCharLimit = defaults.ContextCharLimit;
            }
            if (!settings.AutoRunToolCalls.HasValue)
            {
                settings.AutoRunToolCalls = defaults.AutoRunToolCalls;
            }
            if (!settings.AutoRetryToolErrors.HasValue)
            {
                settings.AutoRetryToolErrors = defaults.AutoRetryToolErrors;
            }
            if (settings.VbaContextCharLimit <= 0)
            {
                settings.VbaContextCharLimit = defaults.VbaContextCharLimit;
            }
            return settings;
        }
    }
}
