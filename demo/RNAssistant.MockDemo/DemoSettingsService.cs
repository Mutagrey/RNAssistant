using System;
using System.Collections.Generic;
using System.IO;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    public sealed class SettingsService
    {
        private readonly AppDataPaths _paths;
        private readonly JsonFileStore _json;
        private static string _baseUrl = "http://127.0.0.1:5179";
        private static string _defaultModel = "mock-strict";
        private static string _apiKey = string.Empty;
        private static string _historySecret = string.Empty;

        public SettingsService(AppDataPaths paths)
        {
            _paths = paths;
            _json = new JsonFileStore();
        }

        public static void ConfigureDemoDefaults(string baseUrl, string defaultModel)
        {
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                _baseUrl = baseUrl.TrimEnd('/');
            }

            if (!string.IsNullOrWhiteSpace(defaultModel))
            {
                _defaultModel = defaultModel;
            }
        }

        public AppSettings Load()
        {
            return Normalize(_json.Load(_paths.SettingsFile, new AppSettings()));
        }

        public void Save(AppSettings settings)
        {
            Save(settings, null, null);
        }

        public void Save(AppSettings settings, string apiKey, string historySecret, bool reviewAgentPrompts = false)
        {
            var normalized = Normalize((settings ?? new AppSettings()).Clone());
            var current = Load();
            if (reviewAgentPrompts)
            {
                normalized.AgentPromptSchemaVersion = AppSettings.CurrentAgentPromptSchemaVersion;
            }
            else if (current.AgentPromptSchemaVersion != AppSettings.CurrentAgentPromptSchemaVersion)
            {
                normalized.AgentPromptSchemaVersion = current.AgentPromptSchemaVersion;
            }
            if (apiKey != null) _apiKey = apiKey;
            if (historySecret != null) _historySecret = historySecret;
            _json.Save(_paths.SettingsFile, normalized);
        }

        public string LoadApiKey()
        {
            return _apiKey;
        }

        public void SaveApiKey(string apiKey)
        {
            _apiKey = apiKey ?? string.Empty;
        }

        public string LoadHistorySecret()
        {
            return _historySecret;
        }

        public StorageProtector LoadStorageProtector()
        {
            var settings = Load();
            if (!StorageProtector.RequiresSecret(settings.HistoryIntegrityMode, settings.HistoryEncryptionMode))
            {
                return StorageProtector.None;
            }
            var secret = string.Equals(settings.HistoryKeySource, HistoryKeySources.CustomSecret, StringComparison.Ordinal)
                ? _historySecret
                : _apiKey;
            byte[] salt;
            if (File.Exists(_paths.HistoryProtectionSaltFile))
            {
                salt = File.ReadAllBytes(_paths.HistoryProtectionSaltFile);
            }
            else
            {
                salt = StorageProtector.NewSalt();
                File.WriteAllBytes(_paths.HistoryProtectionSaltFile, salt);
            }
            return new StorageProtector(settings.HistoryIntegrityMode, settings.HistoryEncryptionMode, secret, salt);
        }

        private static AppSettings Normalize(AppSettings settings)
        {
            var defaults = new AppSettings();
            if (settings.CustomHeaders == null)
            {
                settings.CustomHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            if (settings.ModelImageSupportOverrides == null)
            {
                settings.ModelImageSupportOverrides = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
            }
            if (settings.ModelAudioSupportOverrides == null)
            {
                settings.ModelAudioSupportOverrides = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
            }
            if (settings.ModelCapabilities == null)
            {
                settings.ModelCapabilities = new Dictionary<string, ModelCapabilitySettings>(StringComparer.OrdinalIgnoreCase);
            }
            if (settings.HtmlNetworkAllowedOrigins == null)
            {
                settings.HtmlNetworkAllowedOrigins = new List<string>();
            }

            settings.BaseUrl = _baseUrl;
            settings.ModelsConfigUrl = (settings.ModelsConfigUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(settings.Model) ||
                string.Equals(settings.Model, defaults.Model, StringComparison.OrdinalIgnoreCase))
            {
                settings.Model = _defaultModel;
            }

            if (string.IsNullOrWhiteSpace(settings.SystemPrompt))
            {
                settings.SystemPrompt = defaults.SystemPrompt;
            }
            if (string.IsNullOrWhiteSpace(settings.ChatSystemPrompt))
            {
                settings.ChatSystemPrompt = defaults.ChatSystemPrompt;
            }
            settings.ChatTitlePrompt = DefaultIfBlank(settings.ChatTitlePrompt, defaults.ChatTitlePrompt);
            settings.ContextCompactionPrompt = DefaultIfBlank(settings.ContextCompactionPrompt, defaults.ContextCompactionPrompt);
            settings.SystemPromptRole = NormalizePromptRole(settings.SystemPromptRole, defaults.SystemPromptRole);
            settings.AgentResponseMode = AgentResponseModes.Normalize(settings.AgentResponseMode);
            settings.ToolResultRole = ToolResultRoles.Normalize(settings.ToolResultRole);
            settings.ReasoningRequestMode = ReasoningRequestModes.Normalize(settings.ReasoningRequestMode);
            settings.ReasoningCustomJson = DefaultIfBlank(settings.ReasoningCustomJson, defaults.ReasoningCustomJson).Trim();
            settings.UiTheme = UiThemes.Normalize(settings.UiTheme);
            settings.HistoryIntegrityMode = HistoryIntegrityModes.Normalize(settings.HistoryIntegrityMode);
            settings.HistoryEncryptionMode = HistoryEncryptionModes.Normalize(settings.HistoryEncryptionMode);
            settings.HistoryKeySource = HistoryKeySources.Normalize(settings.HistoryKeySource);
            foreach (var capability in settings.ModelCapabilities.Values)
            {
                if (capability != null)
                {
                    capability.ReasoningRequestMode = ReasoningRequestModes.NormalizeOverride(capability.ReasoningRequestMode);
                }
            }

            if (settings.MaxTokens <= 0)
            {
                settings.MaxTokens = defaults.MaxTokens;
            }

            if (settings.TopP <= 0 || settings.TopP > 1)
            {
                settings.TopP = 1.0;
            }

            if (settings.RequestTimeoutSeconds < 30)
            {
                settings.RequestTimeoutSeconds = 30;
            }

            if (settings.ContextWindowOverrideTokens < 0)
            {
                settings.ContextWindowOverrideTokens = 0;
            }

            settings.AutoConfirmToolActions = true;
            settings.SmartChatTitles = false;

            if (settings.MaxAgentIterations <= 0)
            {
                settings.MaxAgentIterations = defaults.MaxAgentIterations;
            }
            if (settings.MaxAgentFormatRetries <= 0)
            {
                settings.MaxAgentFormatRetries = defaults.MaxAgentFormatRetries;
            }
            settings.MaxAgentFormatRetries = Math.Max(
                1,
                Math.Min(AppSettings.MaximumAgentFormatRetries, settings.MaxAgentFormatRetries));
            if (settings.MaxAgentToolSteps <= 0)
            {
                settings.MaxAgentToolSteps = defaults.MaxAgentToolSteps;
            }

            return settings;
        }

        private static string NormalizePromptRole(string value, string fallback)
        {
            if (string.Equals(value, "system", StringComparison.OrdinalIgnoreCase)) return "system";
            if (string.Equals(value, "developer", StringComparison.OrdinalIgnoreCase)) return "developer";
            if (string.Equals(value, "user", StringComparison.OrdinalIgnoreCase)) return "user";
            return fallback;
        }

        private static string DefaultIfBlank(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
    }
}
