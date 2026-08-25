using RNAssistant.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RNAssistant.Core.Storage
{
    public sealed class SettingsService
    {
        private readonly AppDataPaths _paths;
        private readonly JsonFileStore _json;
        private readonly ProtectedSecretStore _secretStore;
        private readonly ProtectedSecretStore _historySecretStore;
        private readonly object _protectionSync = new object();
        private StorageProtector _cachedProtector;
        private string _cachedProtectionStamp;

        public SettingsService(AppDataPaths paths)
        {
            _paths = paths;
            _json = new JsonFileStore();
            _secretStore = new ProtectedSecretStore(paths.SecretFile);
            _historySecretStore = new ProtectedSecretStore(paths.HistorySecretFile);
        }

        public AppSettings Load()
        {
            return Normalize(_json.Load(_paths.SettingsFile, new AppSettings()));
        }

        public void Save(AppSettings settings)
        {
            Save(settings, null, null);
        }

        public void Save(AppSettings settings, string apiKey, string historySecret)
        {
            var normalized = Normalize(settings ?? new AppSettings());
            var current = Load();
            var currentApiKey = LoadApiKey();
            var currentHistorySecret = LoadHistorySecret();
            var effectiveApiKey = apiKey == null ? currentApiKey : apiKey;
            var effectiveHistorySecret = historySecret == null ? currentHistorySecret : historySecret;
            ValidateProtectionSecret(normalized, effectiveApiKey, effectiveHistorySecret);
            if (HasCanonicalHistory() && !SameProtection(
                current,
                normalized,
                currentApiKey,
                effectiveApiKey,
                currentHistorySecret,
                effectiveHistorySecret))
            {
                throw new InvalidOperationException(
                    "History protection mode or key cannot change while chat events or CAS blobs exist. " +
                    "Clear Chats/Data first, then save the new protection settings.");
            }

            if (apiKey != null) _secretStore.SaveSecret(apiKey);
            if (historySecret != null) _historySecretStore.SaveSecret(historySecret);
            _json.Save(_paths.SettingsFile, normalized);
            InvalidateProtectionCache();
        }

        public string LoadApiKey()
        {
            return _secretStore.LoadApiKey();
        }

        public void SaveApiKey(string apiKey)
        {
            Save(Load(), apiKey, null);
        }

        public string LoadHistorySecret()
        {
            return _historySecretStore.LoadSecret();
        }

        public void SaveHistorySecret(string secret)
        {
            Save(Load(), null, secret);
        }

        public StorageProtector LoadStorageProtector()
        {
            lock (_protectionSync)
            {
                var stamp = ProtectionStamp();
                if (_cachedProtector != null && string.Equals(stamp, _cachedProtectionStamp, StringComparison.Ordinal))
                {
                    return _cachedProtector;
                }

                var settings = Load();
                if (!StorageProtector.RequiresSecret(settings.HistoryIntegrityMode, settings.HistoryEncryptionMode))
                {
                    _cachedProtector = StorageProtector.None;
                    _cachedProtectionStamp = stamp;
                    return _cachedProtector;
                }
                var secret = string.Equals(settings.HistoryKeySource, HistoryKeySources.CustomSecret, StringComparison.Ordinal)
                    ? LoadHistorySecret()
                    : LoadApiKey();
                _cachedProtector = new StorageProtector(
                    settings.HistoryIntegrityMode,
                    settings.HistoryEncryptionMode,
                    secret,
                    LoadOrCreateProtectionSalt());
                _cachedProtectionStamp = ProtectionStamp();
                return _cachedProtector;
            }
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
            settings.AttachmentHelperMaxTokens = Math.Max(0, settings.AttachmentHelperMaxTokens);
            settings.AttachmentEvidenceMaxTokens = Math.Max(0, settings.AttachmentEvidenceMaxTokens);
            if (settings.TokenEstimateCalibrations == null)
            {
                settings.TokenEstimateCalibrations = new Dictionary<string, TokenEstimateCalibrationSettings>(StringComparer.OrdinalIgnoreCase);
            }
            if (settings.HtmlNetworkAllowedOrigins == null)
            {
                settings.HtmlNetworkAllowedOrigins = new List<string>();
            }
            settings.BaseUrl = NormalizeBaseUrl(settings.BaseUrl);
            settings.ModelsConfigUrl = string.IsNullOrWhiteSpace(settings.ModelsConfigUrl)
                ? defaults.ModelsConfigUrl
                : settings.ModelsConfigUrl.Trim();
            settings.Model = (settings.Model ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(settings.SystemPrompt))
            {
                settings.SystemPrompt = defaults.SystemPrompt;
            }
            settings.SystemPrompt = AgentSkillPromptPolicy.Upgrade(settings.SystemPrompt);
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

        private static void ValidateProtectionSecret(AppSettings settings, string apiKey, string historySecret)
        {
            if (!StorageProtector.RequiresSecret(settings.HistoryIntegrityMode, settings.HistoryEncryptionMode)) return;
            if (string.Equals(settings.HistoryKeySource, HistoryKeySources.CustomSecret, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(historySecret) || historySecret.Length < 12)
                {
                    throw new InvalidOperationException("A custom history secret of at least 12 characters is required.");
                }
                return;
            }
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("An API key is required when it is selected as the history protection key.");
            }
        }

        private static bool SameProtection(
            AppSettings current,
            AppSettings proposed,
            string currentApiKey,
            string proposedApiKey,
            string currentHistorySecret,
            string proposedHistorySecret)
        {
            if (!string.Equals(current.HistoryIntegrityMode, proposed.HistoryIntegrityMode, StringComparison.Ordinal) ||
                !string.Equals(current.HistoryEncryptionMode, proposed.HistoryEncryptionMode, StringComparison.Ordinal) ||
                !string.Equals(current.HistoryKeySource, proposed.HistoryKeySource, StringComparison.Ordinal)) return false;
            if (!StorageProtector.RequiresSecret(current.HistoryIntegrityMode, current.HistoryEncryptionMode)) return true;
            return string.Equals(current.HistoryKeySource, HistoryKeySources.CustomSecret, StringComparison.Ordinal)
                ? string.Equals(currentHistorySecret ?? string.Empty, proposedHistorySecret ?? string.Empty, StringComparison.Ordinal)
                : string.Equals(currentApiKey ?? string.Empty, proposedApiKey ?? string.Empty, StringComparison.Ordinal);
        }

        private bool HasCanonicalHistory()
        {
            try
            {
                return Directory.Exists(_paths.ChatDirectory) &&
                        Directory.EnumerateFiles(_paths.ChatDirectory, "*.events.jsonl", SearchOption.AllDirectories).Any() ||
                    Directory.Exists(_paths.VbaJournalDirectory) &&
                        Directory.EnumerateFiles(_paths.VbaJournalDirectory, "*.events.jsonl", SearchOption.AllDirectories).Any() ||
                    Directory.Exists(_paths.ChatBlobDirectory) &&
                        Directory.EnumerateFiles(_paths.ChatBlobDirectory, "*.blob", SearchOption.AllDirectories).Any();
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        private byte[] LoadOrCreateProtectionSalt()
        {
            if (File.Exists(_paths.HistoryProtectionSaltFile))
            {
                var existing = File.ReadAllBytes(_paths.HistoryProtectionSaltFile);
                if (existing.Length < 16) throw new InvalidOperationException("History protection salt is invalid.");
                return existing;
            }
            var created = StorageProtector.NewSalt();
            try
            {
                StorageFileSystem.WriteAtomic(
                    _paths.HistoryProtectionSaltFile,
                    tempPath => File.WriteAllBytes(tempPath, created));
            }
            catch (IOException)
            {
                if (!File.Exists(_paths.HistoryProtectionSaltFile)) throw;
            }
            var stored = File.ReadAllBytes(_paths.HistoryProtectionSaltFile);
            if (stored.Length < 16) throw new InvalidOperationException("History protection salt is invalid.");
            return stored;
        }

        private string ProtectionStamp()
        {
            return FileStamp(_paths.SettingsFile) + "|" +
                FileStamp(_paths.SecretFile) + "|" +
                FileStamp(_paths.HistorySecretFile) + "|" +
                FileStamp(_paths.HistoryProtectionSaltFile);
        }

        private static string FileStamp(string path)
        {
            try
            {
                if (!File.Exists(path)) return "missing";
                var info = new FileInfo(path);
                return info.Length + ":" + info.LastWriteTimeUtc.Ticks;
            }
            catch (IOException)
            {
                return "unavailable";
            }
            catch (UnauthorizedAccessException)
            {
                return "unavailable";
            }
        }

        private void InvalidateProtectionCache()
        {
            lock (_protectionSync)
            {
                _cachedProtector = null;
                _cachedProtectionStamp = null;
            }
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
                return string.Empty;
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
