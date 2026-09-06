using System.Collections.Generic;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Contracts
{
    // Bridge controls only. Prompt bodies belong to the exact resource transport.
    public sealed class SettingsControlsDto
    {
        public string BaseUrl { get; set; }
        public string ModelsConfigUrl { get; set; }
        public string Model { get; set; }
        public int AgentPromptSchemaVersion { get; set; }
        public string SystemPromptRole { get; set; }
        public string AgentResponseMode { get; set; }
        public string ToolResultRole { get; set; }
        public bool FallbackToJsonObject { get; set; }
        public string ReasoningRequestMode { get; set; }
        public string ReasoningCustomJson { get; set; }
        public int MaxTokens { get; set; }
        public int RequestTimeoutSeconds { get; set; }
        public double Temperature { get; set; }
        public double TopP { get; set; }
        public int ContextWindowOverrideTokens { get; set; }
        public double TokenEstimateMultiplier { get; set; }
        public bool AutoCalibrateTokenEstimate { get; set; }
        public bool StreamResponses { get; set; }
        public bool AutoConfirmToolActions { get; set; }
        public bool SmartChatTitles { get; set; }
        public int MaxAgentIterations { get; set; }
        public int MaxAgentFormatRetries { get; set; }
        public int MaxAgentToolSteps { get; set; }
        public bool AutoCompressContext { get; set; }
        public bool DebugModelTraffic { get; set; }
        public string HistoryIntegrityMode { get; set; }
        public string HistoryEncryptionMode { get; set; }
        public string HistoryKeySource { get; set; }
        public bool ScreenCaptureProtectionEnabled { get; set; }
        public double UiFontScale { get; set; }
        public string UiTheme { get; set; }
        public Dictionary<string, string> CustomHeaders { get; set; }
        public Dictionary<string, bool?> ModelImageSupportOverrides { get; set; }
        public Dictionary<string, bool?> ModelAudioSupportOverrides { get; set; }
        public Dictionary<string, ModelCapabilitySettings> ModelCapabilities { get; set; }
        public List<string> AttachmentModelPriority { get; set; }
        public int AttachmentHelperMaxTokens { get; set; }
        public int AttachmentEvidenceMaxTokens { get; set; }
        public Dictionary<string, TokenEstimateCalibrationSettings> TokenEstimateCalibrations { get; set; }
        public List<string> HtmlNetworkAllowedOrigins { get; set; }

        public static SettingsControlsDto From(AppSettings settings)
        {
            return new SettingsControlsDto
            {
                BaseUrl = settings.BaseUrl,
                ModelsConfigUrl = settings.ModelsConfigUrl,
                Model = settings.Model,
                AgentPromptSchemaVersion = settings.AgentPromptSchemaVersion,
                SystemPromptRole = settings.SystemPromptRole,
                AgentResponseMode = settings.AgentResponseMode,
                ToolResultRole = settings.ToolResultRole,
                FallbackToJsonObject = settings.FallbackToJsonObject,
                ReasoningRequestMode = settings.ReasoningRequestMode,
                ReasoningCustomJson = settings.ReasoningCustomJson,
                MaxTokens = settings.MaxTokens,
                RequestTimeoutSeconds = settings.RequestTimeoutSeconds,
                Temperature = settings.Temperature,
                TopP = settings.TopP,
                ContextWindowOverrideTokens = settings.ContextWindowOverrideTokens,
                TokenEstimateMultiplier = settings.TokenEstimateMultiplier,
                AutoCalibrateTokenEstimate = settings.AutoCalibrateTokenEstimate,
                StreamResponses = settings.StreamResponses,
                AutoConfirmToolActions = settings.AutoConfirmToolActions,
                SmartChatTitles = settings.SmartChatTitles,
                MaxAgentIterations = settings.MaxAgentIterations,
                MaxAgentFormatRetries = settings.MaxAgentFormatRetries,
                MaxAgentToolSteps = settings.MaxAgentToolSteps,
                AutoCompressContext = settings.AutoCompressContext,
                DebugModelTraffic = settings.DebugModelTraffic,
                HistoryIntegrityMode = settings.HistoryIntegrityMode,
                HistoryEncryptionMode = settings.HistoryEncryptionMode,
                HistoryKeySource = settings.HistoryKeySource,
                ScreenCaptureProtectionEnabled = settings.ScreenCaptureProtectionEnabled,
                UiFontScale = settings.UiFontScale,
                UiTheme = settings.UiTheme,
                CustomHeaders = settings.CustomHeaders,
                ModelImageSupportOverrides = settings.ModelImageSupportOverrides,
                ModelAudioSupportOverrides = settings.ModelAudioSupportOverrides,
                ModelCapabilities = settings.ModelCapabilities,
                AttachmentModelPriority = settings.AttachmentModelPriority,
                AttachmentHelperMaxTokens = settings.AttachmentHelperMaxTokens,
                AttachmentEvidenceMaxTokens = settings.AttachmentEvidenceMaxTokens,
                TokenEstimateCalibrations = settings.TokenEstimateCalibrations,
                HtmlNetworkAllowedOrigins = settings.HtmlNetworkAllowedOrigins,
            };
        }

        internal AppSettings ApplyTo(AppSettings source)
        {
            var result = source.Clone();
            result.BaseUrl = BaseUrl;
            result.ModelsConfigUrl = ModelsConfigUrl;
            result.Model = Model;
            result.AgentPromptSchemaVersion = AgentPromptSchemaVersion;
            result.SystemPromptRole = SystemPromptRole;
            result.AgentResponseMode = AgentResponseMode;
            result.ToolResultRole = ToolResultRole;
            result.FallbackToJsonObject = FallbackToJsonObject;
            result.ReasoningRequestMode = ReasoningRequestMode;
            result.ReasoningCustomJson = ReasoningCustomJson;
            result.MaxTokens = MaxTokens;
            result.RequestTimeoutSeconds = RequestTimeoutSeconds;
            result.Temperature = Temperature;
            result.TopP = TopP;
            result.ContextWindowOverrideTokens = ContextWindowOverrideTokens;
            result.TokenEstimateMultiplier = TokenEstimateMultiplier;
            result.AutoCalibrateTokenEstimate = AutoCalibrateTokenEstimate;
            result.StreamResponses = StreamResponses;
            result.AutoConfirmToolActions = AutoConfirmToolActions;
            result.SmartChatTitles = SmartChatTitles;
            result.MaxAgentIterations = MaxAgentIterations;
            result.MaxAgentFormatRetries = MaxAgentFormatRetries;
            result.MaxAgentToolSteps = MaxAgentToolSteps;
            result.AutoCompressContext = AutoCompressContext;
            result.DebugModelTraffic = DebugModelTraffic;
            result.HistoryIntegrityMode = HistoryIntegrityMode;
            result.HistoryEncryptionMode = HistoryEncryptionMode;
            result.HistoryKeySource = HistoryKeySource;
            result.ScreenCaptureProtectionEnabled = ScreenCaptureProtectionEnabled;
            result.UiFontScale = UiFontScale;
            result.UiTheme = UiTheme;
            result.CustomHeaders = CustomHeaders;
            result.ModelImageSupportOverrides = ModelImageSupportOverrides;
            result.ModelAudioSupportOverrides = ModelAudioSupportOverrides;
            result.ModelCapabilities = ModelCapabilities;
            result.AttachmentModelPriority = AttachmentModelPriority;
            result.AttachmentHelperMaxTokens = AttachmentHelperMaxTokens;
            result.AttachmentEvidenceMaxTokens = AttachmentEvidenceMaxTokens;
            result.TokenEstimateCalibrations = TokenEstimateCalibrations;
            result.HtmlNetworkAllowedOrigins = HtmlNetworkAllowedOrigins;
            return result;
        }
    }
}
