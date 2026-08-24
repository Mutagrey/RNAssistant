using RNAssistant.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;

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
            settings.AttachmentModelPriority = NormalizeAttachmentModelPriority(settings);
            if (settings.TokenEstimateCalibrations == null)
            {
                settings.TokenEstimateCalibrations = new Dictionary<string, TokenEstimateCalibrationSettings>(StringComparer.OrdinalIgnoreCase);
            }
            if (settings.HtmlNetworkAllowedOrigins == null)
            {
                settings.HtmlNetworkAllowedOrigins = new List<string>();
            }
            if (string.IsNullOrWhiteSpace(settings.BaseUrl))
            {
                settings.BaseUrl = defaults.BaseUrl;
            }
            settings.BaseUrl = NormalizeBaseUrl(settings.BaseUrl);
            settings.ModelsConfigUrl = (settings.ModelsConfigUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(settings.Model))
            {
                settings.Model = defaults.Model;
            }
            if (string.IsNullOrWhiteSpace(settings.SystemPrompt))
            {
                settings.SystemPrompt = defaults.SystemPrompt;
            }
            if (string.IsNullOrWhiteSpace(settings.ChatSystemPrompt))
            {
                settings.ChatSystemPrompt = defaults.ChatSystemPrompt;
            }
            settings.SystemPromptRole = NormalizePromptRole(settings.SystemPromptRole, defaults.SystemPromptRole);
            settings.AgentResponseMode = AgentResponseModes.Normalize(settings.AgentResponseMode);
            settings.ToolResultRole = ToolResultRoles.Normalize(settings.ToolResultRole);
            settings.ReasoningRequestMode = ReasoningRequestModes.Normalize(settings.ReasoningRequestMode);
            settings.ReasoningCustomJson = string.IsNullOrWhiteSpace(settings.ReasoningCustomJson)
                ? defaults.ReasoningCustomJson
                : settings.ReasoningCustomJson.Trim();
            settings.UiTheme = UiThemes.Normalize(settings.UiTheme);
            foreach (var capability in settings.ModelCapabilities.Values)
            {
                if (capability != null)
                {
                    capability.ReasoningRequestMode = ReasoningRequestModes.NormalizeOverride(capability.ReasoningRequestMode);
                }
            }
            settings.ChatTitlePrompt = DefaultIfBlank(settings.ChatTitlePrompt, defaults.ChatTitlePrompt);
            settings.ContextCompactionPrompt = DefaultIfBlank(settings.ContextCompactionPrompt, defaults.ContextCompactionPrompt);
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
            if (settings.ContextWindowOverrideTokens < 0)
            {
                settings.ContextWindowOverrideTokens = 0;
            }
            if (settings.TokenEstimateMultiplier <= 0 ||
                double.IsNaN(settings.TokenEstimateMultiplier) ||
                double.IsInfinity(settings.TokenEstimateMultiplier))
            {
                settings.TokenEstimateMultiplier = defaults.TokenEstimateMultiplier;
            }
            settings.TokenEstimateMultiplier = Math.Max(
                AppSettings.MinimumTokenEstimateMultiplier,
                Math.Min(AppSettings.MaximumTokenEstimateMultiplier, settings.TokenEstimateMultiplier));
            foreach (var key in settings.TokenEstimateCalibrations.Keys.ToList())
            {
                var calibration = settings.TokenEstimateCalibrations[key];
                if (string.IsNullOrWhiteSpace(key) || calibration == null)
                {
                    settings.TokenEstimateCalibrations.Remove(key);
                    continue;
                }
                if (calibration.Multiplier <= 0 ||
                    double.IsNaN(calibration.Multiplier) ||
                    double.IsInfinity(calibration.Multiplier))
                {
                    calibration.Multiplier = 1.0;
                }
                calibration.Multiplier = Math.Max(
                    AppSettings.MinimumTokenEstimateMultiplier,
                    Math.Min(AppSettings.MaximumTokenEstimateMultiplier, calibration.Multiplier));
                if (calibration.InterceptTokens < 0 ||
                    double.IsNaN(calibration.InterceptTokens) ||
                    double.IsInfinity(calibration.InterceptTokens))
                {
                    calibration.InterceptTokens = 0;
                }
                calibration.InterceptTokens = Math.Min(
                    AppSettings.MaximumTokenEstimateInterceptTokens,
                    calibration.InterceptTokens);
                calibration.SampleCount = Math.Max(0, calibration.SampleCount);
                calibration.FitSampleCount = Math.Max(0, Math.Min(calibration.SampleCount, calibration.FitSampleCount));
                calibration.MeanBasePromptTokens = NormalizeCalibrationStatistic(calibration.MeanBasePromptTokens);
                calibration.MeanActualPromptTokens = NormalizeCalibrationStatistic(calibration.MeanActualPromptTokens);
                calibration.BasePromptTokenM2 = NormalizeCalibrationStatistic(calibration.BasePromptTokenM2);
                calibration.BaseActualPromptC2 = NormalizeCalibrationStatistic(calibration.BaseActualPromptC2, true);
                calibration.LastBaseEstimatedPromptTokens = Math.Max(0, calibration.LastBaseEstimatedPromptTokens);
                calibration.LastEstimatedPromptTokens = Math.Max(0, calibration.LastEstimatedPromptTokens);
                calibration.LastActualPromptTokens = Math.Max(0, calibration.LastActualPromptTokens);
            }
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

        private static List<string> NormalizeAttachmentModelPriority(AppSettings settings)
        {
            var result = (settings.AttachmentModelPriority ?? new List<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var pair in settings.ModelCapabilities ?? new Dictionary<string, ModelCapabilitySettings>())
            {
                if (pair.Value != null && (pair.Value.SupportsImages == true || pair.Value.SupportsAudio == true))
                {
                    AddUnique(result, pair.Key);
                }
            }
            foreach (var pair in settings.ModelImageSupportOverrides ?? new Dictionary<string, bool?>())
            {
                if (pair.Value == true) AddUnique(result, pair.Key);
            }
            foreach (var pair in settings.ModelAudioSupportOverrides ?? new Dictionary<string, bool?>())
            {
                if (pair.Value == true) AddUnique(result, pair.Key);
            }
            return result;
        }

        private static void AddUnique(ICollection<string> values, string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length > 0 && !values.Contains(value, StringComparer.OrdinalIgnoreCase)) values.Add(value);
        }

        private static string NormalizePromptRole(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }
            if (string.Equals(value, "system", StringComparison.OrdinalIgnoreCase)) return "system";
            if (string.Equals(value, "developer", StringComparison.OrdinalIgnoreCase)) return "developer";
            if (string.Equals(value, "user", StringComparison.OrdinalIgnoreCase)) return "user";
            return fallback;
        }


        private static string DefaultIfBlank(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static double NormalizeCalibrationStatistic(double value, bool allowNegative = false)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || !allowNegative && value < 0)
            {
                return 0;
            }
            return value;
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
