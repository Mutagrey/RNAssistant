using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Llm
{
    public static class ModelContextBudget
    {
        public const int UnknownModelContextTokens = 32768;
        public const int EstimatedImageTokens = 4096;
        public const int MinimumInputTokens = 1024;
        public const int MaximumSafetyReserveTokens = 16384;

        public static ModelCapabilitySettings Capability(AppSettings settings, string model = null)
        {
            if (settings == null || settings.ModelCapabilities == null)
            {
                return null;
            }
            var key = string.IsNullOrWhiteSpace(model) ? settings.Model : model;
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }
            ModelCapabilitySettings capability;
            return settings.ModelCapabilities.TryGetValue(key, out capability) ? capability : null;
        }

        public static int ContextWindowTokens(AppSettings settings, string model = null)
        {
            if (settings != null && settings.ContextWindowOverrideTokens > 0)
            {
                return settings.ContextWindowOverrideTokens;
            }
            var capability = Capability(settings, model);
            return capability != null && capability.MaxContextTokens.GetValueOrDefault() > 0
                ? capability.MaxContextTokens.Value
                : UnknownModelContextTokens;
        }

        public static int InputBudgetTokens(AppSettings settings, string model = null)
        {
            var window = Math.Max(4096, ContextWindowTokens(settings, model));
            var safety = SafetyReserveTokens(window);
            var output = Math.Min(
                RequestedOutputTokens(settings, model),
                Math.Max(1, window - safety - MinimumInputTokens));
            return Math.Max(MinimumInputTokens, window - output - safety);
        }

        public static int EffectiveOutputTokens(AppSettings settings, IEnumerable<ChatMessage> messages, string model = null)
        {
            return EffectiveOutputTokens(settings, EstimateMessagesTokens(messages, settings, model), model);
        }

        public static int EffectiveOutputTokens(AppSettings settings, int estimatedPromptTokens, string model = null)
        {
            settings = settings ?? new AppSettings();
            var window = Math.Max(4096, ContextWindowTokens(settings, model));
            var prompt = Math.Max(0, estimatedPromptTokens);
            var safety = SafetyReserveTokens(window);
            var remaining = window - prompt - safety;
            if (remaining < 1)
            {
                throw new InvalidOperationException("Prompt exceeds the available model context window. Reduce chat context or attachments.");
            }
            return Math.Min(RequestedOutputTokens(settings, model), remaining);
        }

        public static int RequestedOutputTokens(AppSettings settings, string model = null)
        {
            var requested = Math.Max(1, settings == null ? AppSettings.DefaultMaxTokens : settings.MaxTokens);
            var capability = Capability(settings, model);
            var modelLimit = capability == null ? 0 : capability.MaxOutputTokens.GetValueOrDefault();
            return modelLimit > 0 ? Math.Min(requested, modelLimit) : requested;
        }

        public static int SafetyReserveTokens(int contextWindowTokens)
        {
            var window = Math.Max(4096, contextWindowTokens);
            return Math.Max(1024, Math.Min(MaximumSafetyReserveTokens, (int)Math.Ceiling(window * 0.02)));
        }

        public static bool SupportsImages(AppSettings settings, string model = null)
        {
            return ImageSupport(settings, model) == true;
        }

        public static bool? ImageSupport(AppSettings settings, string model = null)
        {
            var key = string.IsNullOrWhiteSpace(model) ? (settings == null ? null : settings.Model) : model;
            if (settings != null && settings.ModelImageSupportOverrides != null && !string.IsNullOrWhiteSpace(key))
            {
                bool? value;
                if (settings.ModelImageSupportOverrides.TryGetValue(key, out value) && value.HasValue)
                {
                    return value.Value;
                }
            }
            var capability = Capability(settings, key);
            return capability == null ? null : capability.SupportsImages;
        }

        public static bool SupportsAudio(AppSettings settings, string model = null)
        {
            return AudioSupport(settings, model) == true;
        }

        public static bool? ReasoningSupport(AppSettings settings, string model = null)
        {
            var capability = Capability(settings, model);
            return capability == null ? null : capability.SupportsReasoning;
        }

        public static bool? AudioSupport(AppSettings settings, string model = null)
        {
            var key = string.IsNullOrWhiteSpace(model) ? (settings == null ? null : settings.Model) : model;
            if (settings != null && settings.ModelAudioSupportOverrides != null && !string.IsNullOrWhiteSpace(key))
            {
                bool? value;
                if (settings.ModelAudioSupportOverrides.TryGetValue(key, out value) && value.HasValue)
                {
                    return value.Value;
                }
            }
            var capability = Capability(settings, key);
            return capability == null ? null : capability.SupportsAudio;
        }

        public static int MaxImagesPerPrompt(AppSettings settings, string model = null)
        {
            var capability = Capability(settings, model);
            return Math.Max(1, capability == null
                ? AppSettings.DefaultMaxImagesPerPrompt
                : capability.MaxImagesPerPrompt.GetValueOrDefault(AppSettings.DefaultMaxImagesPerPrompt));
        }

        public static int EstimateTextTokens(string text)
        {
            return EstimateTextTokens(text, null, null);
        }

        public static int EstimateTextTokens(string text, AppSettings settings, string model = null)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var raw = Math.Max(1, (int)Math.Ceiling(Encoding.UTF8.GetByteCount(text) / 4.0));
            return ApplyTextMultiplier(raw, settings, model);
        }

        public static int EstimateCharacterCountTokens(int characters, AppSettings settings, string model = null)
        {
            if (characters <= 0) return 0;
            return ApplyTextMultiplier(Math.Max(1, (int)Math.Ceiling(characters / 2.0)), settings, model);
        }

        public static int ApproximateTextCharacterCapacity(int tokens, AppSettings settings, string model = null)
        {
            if (tokens <= 0) return 0;
            var multiplier = TokenEstimateCalibration.EffectiveMultiplier(settings, model);
            return Math.Max(1, (int)Math.Floor(tokens * 4.0 / Math.Max(0.01, multiplier)));
        }

        public static string TruncateText(string text, int maxTokens)
        {
            return TruncateText(text, maxTokens, null, null);
        }

        public static string TruncateText(string text, int maxTokens, AppSettings settings, string model = null)
        {
            if (string.IsNullOrEmpty(text) || maxTokens <= 0)
            {
                return string.Empty;
            }
            if (EstimateTextTokens(text, settings, model) <= maxTokens)
            {
                return text;
            }

            var low = 0;
            var high = text.Length;
            var characters = text.ToCharArray();
            while (low < high)
            {
                var middle = low + (high - low + 1) / 2;
                var raw = Math.Max(1, (int)Math.Ceiling(Encoding.UTF8.GetByteCount(characters, 0, middle) / 4.0));
                var tokens = ApplyTextMultiplier(raw, settings, model);
                if (tokens <= maxTokens) low = middle;
                else high = middle - 1;
            }
            return text.Substring(0, low);
        }

        public static int EstimateMessagesTokens(IEnumerable<ChatMessage> messages)
        {
            return EstimateMessagesTokens(messages, true, null, null);
        }

        public static int EstimateMessagesTokens(IEnumerable<ChatMessage> messages, bool includeExtractedAttachments)
        {
            return EstimateMessagesTokens(messages, includeExtractedAttachments, null, null);
        }

        public static int EstimateMessagesTokens(
            IEnumerable<ChatMessage> messages,
            AppSettings settings,
            string model = null)
        {
            return EstimateMessagesTokens(messages, true, settings, model);
        }

        public static int EstimateMessagesTokens(
            IEnumerable<ChatMessage> messages,
            bool includeExtractedAttachments,
            AppSettings settings,
            string model = null)
        {
            var source = (messages ?? new ChatMessage[0]).ToList();
            var hasMedia = source.Any(message => message != null &&
                (message.Attachments ?? new List<ChatAttachment>()).Any(attachment => attachment != null &&
                    (string.Equals(attachment.Kind, "image", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(attachment.Kind, "audio", StringComparison.OrdinalIgnoreCase))));
            if (!hasMedia)
            {
                var baseTotal = source.Sum(message =>
                    EstimateMessageTokens(message, includeExtractedAttachments, null, model));
                return TokenEstimateCalibration.PredictPromptTokens(settings, baseTotal, model);
            }

            var total = 0;
            foreach (var message in source)
            {
                total += EstimateMessageTokens(message, includeExtractedAttachments, settings, model);
            }
            return TokenEstimateCalibration.AddPromptIntercept(settings, total, model);
        }

        public static int EstimateMessageTokens(ChatMessage message, bool includeExtractedAttachments = true)
        {
            return EstimateMessageTokens(message, includeExtractedAttachments, null, null);
        }

        public static int EstimateMessageTokens(
            ChatMessage message,
            AppSettings settings,
            string model = null,
            bool includeExtractedAttachments = true)
        {
            return EstimateMessageTokens(message, includeExtractedAttachments, settings, model);
        }

        private static int EstimateMessageTokens(
            ChatMessage message,
            bool includeExtractedAttachments,
            AppSettings settings,
            string model)
        {
            if (message == null || message.ExcludeFromModelContext) return 0;
            var total = 4 + EstimateTextTokens(message.Role, settings, model) + EstimateTextTokens(message.Content, settings, model);
            if (message.ToolCalls != null && message.ToolCalls.Count > 0)
            {
                total += 8;
                total += message.ToolCalls.Sum(call => call == null
                    ? 0
                    : 4 + EstimateTextTokens(call.Id, settings, model) + EstimateTextTokens(call.Name, settings, model) +
                        EstimateTextTokens(call.ArgumentsJson, settings, model));
            }
            if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                total += 2 + EstimateTextTokens(message.ToolCallId, settings, model) + EstimateTextTokens(message.ToolName, settings, model);
            }
            foreach (var attachment in message.Attachments ?? new List<ChatAttachment>())
            {
                if (attachment == null) continue;
                if (string.Equals(attachment.Kind, "image", StringComparison.OrdinalIgnoreCase)) total += EstimatedImageTokens;
                if (string.Equals(attachment.Kind, "audio", StringComparison.OrdinalIgnoreCase)) total += EstimateAudioTokens(attachment.Size);
                if (includeExtractedAttachments)
                {
                    total += EstimateCharacterCountTokens(
                        Math.Max(attachment.ExtractedCharCount, (attachment.ExtractedText ?? string.Empty).Length),
                        settings,
                        model);
                }
            }
            return total;
        }

        public static int EstimateRequestOptionsTokens(LlmRequestOptions options)
        {
            return EstimateRequestOptionsTokens(options, null, null);
        }

        public static int EstimateRequestOptionsTokens(
            LlmRequestOptions options,
            AppSettings settings,
            string model = null)
        {
            if (options == null) return 0;
            var total = 8 + EstimateTextTokens(options.ResponseFormat, settings, model);
            if (string.Equals(options.ResponseFormat, LlmResponseFormats.JsonSchema, StringComparison.OrdinalIgnoreCase))
            {
                total += EstimateTextTokens(options.ResponseSchemaName, settings, model) +
                    EstimateTextTokens(options.ResponseSchemaJson, settings, model);
            }
            return total;
        }

        private static int ApplyTextMultiplier(int rawTokens, AppSettings settings, string model)
        {
            if (rawTokens <= 0) return 0;
            return Math.Max(1, (int)Math.Ceiling(rawTokens * TokenEstimateCalibration.EffectiveMultiplier(settings, model)));
        }

        public static int EstimateAudioTokens(long bytes)
        {
            return bytes <= 0 ? 0 : Math.Max(1, (int)Math.Ceiling(bytes / 512.0));
        }
    }
}
