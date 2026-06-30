using System;
using System.Collections.Generic;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    public sealed class SettingsService
    {
        private readonly AppDataPaths _paths;
        private readonly JsonFileStore _json;
        private static string _baseUrl = "http://127.0.0.1:5179";
        private static string _defaultModel = "mock-strict";

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
            _json.Save(_paths.SettingsFile, Normalize(settings ?? new AppSettings()));
        }

        public string LoadApiKey()
        {
            return string.Empty;
        }

        public void SaveApiKey(string apiKey)
        {
        }

        private static AppSettings Normalize(AppSettings settings)
        {
            var defaults = new AppSettings();
            if (settings.CustomHeaders == null)
            {
                settings.CustomHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            settings.BaseUrl = _baseUrl;
            if (string.IsNullOrWhiteSpace(settings.Model) ||
                string.Equals(settings.Model, defaults.Model, StringComparison.OrdinalIgnoreCase))
            {
                settings.Model = _defaultModel;
            }

            if (string.IsNullOrWhiteSpace(settings.SystemPrompt))
            {
                settings.SystemPrompt = defaults.SystemPrompt;
            }

            if (string.IsNullOrWhiteSpace(settings.AgentPrompt))
            {
                settings.AgentPrompt = defaults.AgentPrompt;
            }

            if (settings.AgentPrompts == null)
            {
                settings.AgentPrompts = new AgentPromptSettings();
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

            if (settings.ContextCharLimit <= 0)
            {
                settings.ContextCharLimit = defaults.ContextCharLimit;
            }

            if (!settings.AutoRunToolCalls.HasValue)
            {
                settings.AutoRunToolCalls = true;
            }

            if (!settings.AutoRetryToolErrors.HasValue)
            {
                settings.AutoRetryToolErrors = true;
            }

            settings.AutoConfirmToolActions = true;
            settings.SmartChatTitles = false;
            if (!settings.RequireVerificationForMutations.HasValue)
            {
                settings.RequireVerificationForMutations = true;
            }

            if (!settings.AutoContinueAfterConfirmation.HasValue)
            {
                settings.AutoContinueAfterConfirmation = true;
            }

            if (settings.VbaContextCharLimit <= 0)
            {
                settings.VbaContextCharLimit = defaults.VbaContextCharLimit;
            }

            if (settings.MaxAgentIterations <= 0)
            {
                settings.MaxAgentIterations = defaults.MaxAgentIterations;
            }

            if (settings.MaxAgentToolSteps <= 0)
            {
                settings.MaxAgentToolSteps = defaults.MaxAgentToolSteps;
            }

            return settings;
        }
    }
}
