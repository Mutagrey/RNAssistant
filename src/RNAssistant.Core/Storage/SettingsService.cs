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
            settings.AttachmentModelPriority = (settings.AttachmentModelPriority ?? new List<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
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
            settings.ToolResultRole = NormalizeToolResultRole(settings.ToolResultRole, defaults.ToolResultRole);
            settings.AgentResponseMode = NormalizeResponseMode(settings.AgentResponseMode, defaults.AgentResponseMode);
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
            NormalizeAgentPrompts(settings);
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
            if (settings.ContextWindowOverrideTokens > 4000000)
            {
                settings.ContextWindowOverrideTokens = 4000000;
            }
            if (settings.VbaContextCharLimit <= 0)
            {
                settings.VbaContextCharLimit = defaults.VbaContextCharLimit;
            }
            if (settings.MaxAgentIterations <= 0)
            {
                settings.MaxAgentIterations = defaults.MaxAgentIterations;
            }
            if (settings.MaxAgentIterations < 1)
            {
                settings.MaxAgentIterations = 1;
            }
            if (settings.MaxAgentIterations > 50)
            {
                settings.MaxAgentIterations = 50;
            }
            if (settings.MaxAgentFormatRetries <= 0)
            {
                settings.MaxAgentFormatRetries = defaults.MaxAgentFormatRetries;
            }
            settings.MaxAgentFormatRetries = Math.Max(1, Math.Min(5, settings.MaxAgentFormatRetries));
            if (settings.MaxAgentToolSteps <= 0)
            {
                settings.MaxAgentToolSteps = defaults.MaxAgentToolSteps;
            }
            if (settings.MaxAgentToolSteps < 1)
            {
                settings.MaxAgentToolSteps = 1;
            }
            if (settings.MaxAgentToolSteps > 200)
            {
                settings.MaxAgentToolSteps = 200;
            }
            if (settings.MaxAgentToolsPerRequest <= 0)
            {
                settings.MaxAgentToolsPerRequest = defaults.MaxAgentToolsPerRequest;
            }
            settings.MaxAgentToolsPerRequest = Math.Max(8, Math.Min(64, settings.MaxAgentToolsPerRequest));
            return settings;
        }

        private static void NormalizeAgentPrompts(AppSettings settings)
        {
            var defaults = new AgentPromptSettings();
            if (settings.AgentPrompts == null)
            {
                settings.AgentPrompts = new AgentPromptSettings();
            }

            settings.AgentPrompts.ForceToolUsePrompt = DefaultIfBlank(settings.AgentPrompts.ForceToolUsePrompt, defaults.ForceToolUsePrompt);
            settings.AgentPrompts.RepairDecisionPrompt = DefaultIfBlank(settings.AgentPrompts.RepairDecisionPrompt, defaults.RepairDecisionPrompt);
            settings.AgentPrompts.PlanContinuationPrompt = DefaultIfBlank(settings.AgentPrompts.PlanContinuationPrompt, defaults.PlanContinuationPrompt);
            settings.AgentPrompts.ChatTitlePrompt = DefaultIfBlank(settings.AgentPrompts.ChatTitlePrompt, defaults.ChatTitlePrompt);
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

        private static string NormalizeToolResultRole(string value, string fallback)
        {
            value = string.IsNullOrWhiteSpace(value) ? fallback : value;
            if (string.Equals(value, "developer", StringComparison.OrdinalIgnoreCase)) return "developer";
            if (string.Equals(value, "user", StringComparison.OrdinalIgnoreCase)) return "user";
            return "tool";
        }

        private static string NormalizeResponseMode(string value, string fallback)
        {
            value = string.IsNullOrWhiteSpace(value) ? fallback : value;
            if (string.Equals(value, AgentResponseModes.NativeToolCalls, StringComparison.OrdinalIgnoreCase))
            {
                return AgentResponseModes.NativeToolCalls;
            }
            return string.Equals(value, AgentResponseModes.JsonObject, StringComparison.OrdinalIgnoreCase)
                ? AgentResponseModes.JsonObject
                : AgentResponseModes.JsonSchema;
        }

        private static string DefaultIfBlank(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
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
