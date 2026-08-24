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
            return EffectiveOutputTokens(settings, EstimateMessagesTokens(messages), model);
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
            return Math.Max(1, capability == null ? 3 : capability.MaxImagesPerPrompt.GetValueOrDefault(3));
        }

        public static int EstimateTextTokens(string text)
        {
            return string.IsNullOrEmpty(text)
                ? 0
                : Math.Max(1, (int)Math.Ceiling(Encoding.UTF8.GetByteCount(text) / 3.0));
        }

        public static string TruncateText(string text, int maxTokens)
        {
            if (string.IsNullOrEmpty(text) || maxTokens <= 0)
            {
                return string.Empty;
            }
            if (EstimateTextTokens(text) <= maxTokens)
            {
                return text;
            }

            var low = 0;
            var high = text.Length;
            var characters = text.ToCharArray();
            while (low < high)
            {
                var middle = low + (high - low + 1) / 2;
                var tokens = Math.Max(1, (int)Math.Ceiling(Encoding.UTF8.GetByteCount(characters, 0, middle) / 3.0));
                if (tokens <= maxTokens) low = middle;
                else high = middle - 1;
            }
            return text.Substring(0, low);
        }

        public static int EstimateMessagesTokens(IEnumerable<ChatMessage> messages)
        {
            return EstimateMessagesTokens(messages, true);
        }

        public static int EstimateMessagesTokens(IEnumerable<ChatMessage> messages, bool includeExtractedAttachments)
        {
            var total = 0;
            foreach (var message in messages ?? new ChatMessage[0])
            {
                total += EstimateMessageTokens(message, includeExtractedAttachments);
            }
            return total;
        }

        public static int EstimateMessageTokens(ChatMessage message, bool includeExtractedAttachments = true)
        {
            if (message == null || message.ExcludeFromModelContext) return 0;
            var total = 4 + EstimateTextTokens(message.Role) + EstimateTextTokens(message.Content);
            if (message.ToolCalls != null && message.ToolCalls.Count > 0)
            {
                total += 8;
                total += message.ToolCalls.Sum(call => call == null
                    ? 0
                    : 4 + EstimateTextTokens(call.Id) + EstimateTextTokens(call.Name) + EstimateTextTokens(call.ArgumentsJson));
            }
            if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                total += 2 + EstimateTextTokens(message.ToolCallId) + EstimateTextTokens(message.ToolName);
            }
            foreach (var attachment in message.Attachments ?? new List<ChatAttachment>())
            {
                if (attachment == null) continue;
                if (string.Equals(attachment.Kind, "image", StringComparison.OrdinalIgnoreCase)) total += EstimatedImageTokens;
                if (string.Equals(attachment.Kind, "audio", StringComparison.OrdinalIgnoreCase)) total += EstimateAudioTokens(attachment.Size);
                if (includeExtractedAttachments)
                {
                    total += Math.Max(attachment.ExtractedCharCount, (attachment.ExtractedText ?? string.Empty).Length) / 2;
                }
            }
            return total;
        }

        public static int EstimateRequestOptionsTokens(LlmRequestOptions options)
        {
            if (options == null) return 0;
            var total = 8 + EstimateTextTokens(options.ResponseFormat);
            if (string.Equals(options.ResponseFormat, LlmResponseFormats.JsonSchema, StringComparison.OrdinalIgnoreCase))
            {
                total += EstimateTextTokens(options.ResponseSchemaName) + EstimateTextTokens(options.ResponseSchemaJson);
            }
            return total;
        }

        public static int EstimateAudioTokens(long bytes)
        {
            return bytes <= 0 ? 0 : Math.Max(1, (int)Math.Ceiling(bytes / 512.0));
        }
    }
}
