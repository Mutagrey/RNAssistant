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
            if (settings.ModelImageSupportOverrides == null)
            {
                settings.ModelImageSupportOverrides = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
            }
            if (settings.ModelCapabilities == null)
            {
                settings.ModelCapabilities = new Dictionary<string, ModelCapabilitySettings>(StringComparer.OrdinalIgnoreCase);
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
            if (settings.ContextCharLimit <= 0)
            {
                settings.ContextCharLimit = defaults.ContextCharLimit;
            }
            if (settings.ContextWindowOverrideTokens < 0)
            {
                settings.ContextWindowOverrideTokens = 0;
            }
            if (settings.ContextWindowOverrideTokens > 1000000)
            {
                settings.ContextWindowOverrideTokens = 1000000;
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
            if (!settings.RequireVerificationForMutations.HasValue)
            {
                settings.RequireVerificationForMutations = defaults.RequireVerificationForMutations;
            }
            if (!settings.AutoContinueAfterConfirmation.HasValue)
            {
                settings.AutoContinueAfterConfirmation = defaults.AutoContinueAfterConfirmation;
            }
            if (!settings.AllowAgentToolAuthoring.HasValue)
            {
                settings.AllowAgentToolAuthoring = defaults.AllowAgentToolAuthoring;
            }
            if (!settings.AutoCompressContext.HasValue)
            {
                settings.AutoCompressContext = defaults.AutoCompressContext;
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
            if (settings.MaxAgentPlanSteps <= 0)
            {
                settings.MaxAgentPlanSteps = defaults.MaxAgentPlanSteps;
            }
            settings.MaxAgentPlanSteps = Math.Max(1, Math.Min(8, settings.MaxAgentPlanSteps));
            if (settings.MaxAgentReadOnlyPlanSteps <= 0)
            {
                settings.MaxAgentReadOnlyPlanSteps = defaults.MaxAgentReadOnlyPlanSteps;
            }
            settings.MaxAgentReadOnlyPlanSteps = Math.Max(1, Math.Min(16, settings.MaxAgentReadOnlyPlanSteps));
            return settings;
        }

        private static void NormalizeAgentPrompts(AppSettings settings)
        {
            var defaults = new AgentPromptSettings();
            if (settings.AgentPrompts == null)
            {
                settings.AgentPrompts = new AgentPromptSettings();
            }

            settings.AgentPrompts.ToolProtocolPrompt = DefaultIfBlank(settings.AgentPrompts.ToolProtocolPrompt, defaults.ToolProtocolPrompt);
            settings.AgentPrompts.ToolRoutingPrompt = DefaultIfBlank(settings.AgentPrompts.ToolRoutingPrompt, defaults.ToolRoutingPrompt);
            settings.AgentPrompts.ForceToolUsePrompt = DefaultIfBlank(settings.AgentPrompts.ForceToolUsePrompt, defaults.ForceToolUsePrompt);
            settings.AgentPrompts.RepairMalformedToolBlockPrompt = DefaultIfBlank(settings.AgentPrompts.RepairMalformedToolBlockPrompt, defaults.RepairMalformedToolBlockPrompt);
            settings.AgentPrompts.AfterToolResultsPrompt = DefaultIfBlank(settings.AgentPrompts.AfterToolResultsPrompt, defaults.AfterToolResultsPrompt);
            settings.AgentPrompts.VerifyMutationPrompt = DefaultIfBlank(settings.AgentPrompts.VerifyMutationPrompt, defaults.VerifyMutationPrompt);
            settings.AgentPrompts.ConfirmedToolContinuationPrompt = DefaultIfBlank(settings.AgentPrompts.ConfirmedToolContinuationPrompt, defaults.ConfirmedToolContinuationPrompt);
        }

        private static string NormalizePromptRole(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }
            return string.Equals(value, "system", StringComparison.OrdinalIgnoreCase)
                ? "system"
                : "user";
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
