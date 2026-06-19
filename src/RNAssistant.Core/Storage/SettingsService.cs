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
            settings.BaseUrl = NormalizeBaseUrl(settings.BaseUrl);
            if (string.IsNullOrWhiteSpace(settings.Model))
            {
                settings.Model = defaults.Model;
            }
            if (string.IsNullOrWhiteSpace(settings.SystemPrompt))
            {
                settings.SystemPrompt = defaults.SystemPrompt;
            }
            if (string.IsNullOrWhiteSpace(settings.AgentPrompt))
            {
                settings.AgentPrompt = defaults.AgentPrompt;
            }
            if (settings.MaxTokens <= 0)
            {
                settings.MaxTokens = defaults.MaxTokens;
            }
            if (settings.TopP <= 0)
            {
                settings.TopP = defaults.TopP;
            }
            if (settings.TopP > 1)
            {
                settings.TopP = 1;
            }
            if (settings.RequestTimeoutSeconds <= 0)
            {
                settings.RequestTimeoutSeconds = defaults.RequestTimeoutSeconds;
            }
            if (settings.RequestTimeoutSeconds < 30)
            {
                settings.RequestTimeoutSeconds = 30;
            }
            if (settings.ContextCharLimit <= 0)
            {
                settings.ContextCharLimit = defaults.ContextCharLimit;
            }
            if (!settings.AgentModeEnabled.HasValue)
            {
                settings.AgentModeEnabled = defaults.AgentModeEnabled;
            }
            if (!settings.AutoRunToolCalls.HasValue)
            {
                settings.AutoRunToolCalls = defaults.AutoRunToolCalls;
            }
            if (!settings.AutoRetryToolErrors.HasValue)
            {
                settings.AutoRetryToolErrors = defaults.AutoRetryToolErrors;
            }
            if (!settings.SmartChatTitles.HasValue)
            {
                settings.SmartChatTitles = defaults.SmartChatTitles;
            }
            if (settings.VbaContextCharLimit <= 0)
            {
                settings.VbaContextCharLimit = defaults.VbaContextCharLimit;
            }
            return settings;
        }

        private static string NormalizeBaseUrl(string baseUrl)
        {
            var value = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (value.Length == 0)
            {
                return new AppSettings().BaseUrl;
            }

            var completionsIndex = value.IndexOf("/chat/completions", StringComparison.OrdinalIgnoreCase);
            if (completionsIndex >= 0)
            {
                value = value.Substring(0, completionsIndex).TrimEnd('/');
            }

            if (value.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(0, value.Length - 3).TrimEnd('/');
            }

            return value;
        }
    }
}
