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
            var output = Math.Max(1, settings == null ? 2048 : settings.MaxTokens);
            var capability = Capability(settings, model);
            if (capability != null && capability.MaxOutputTokens.GetValueOrDefault() > 0)
            {
                output = Math.Min(output, capability.MaxOutputTokens.Value);
            }
            output = Math.Min(output, Math.Max(2048, window / 2));
            var safety = Math.Max(1024, (int)Math.Ceiling(window * 0.05));
            return Math.Max(1024, window - output - safety);
        }

        public static int EffectiveOutputTokens(AppSettings settings, IEnumerable<ChatMessage> messages, string model = null)
        {
            settings = settings ?? new AppSettings();
            var window = Math.Max(4096, ContextWindowTokens(settings, model));
            var prompt = EstimateMessagesTokens(messages);
            var safety = Math.Max(1024, (int)Math.Ceiling(window * 0.05));
            var remaining = window - prompt - safety;
            if (remaining < 128)
            {
                throw new InvalidOperationException("Prompt exceeds the available model context window. Reduce chat context or attachments.");
            }
            var requested = Math.Max(1, settings.MaxTokens);
            var capability = Capability(settings, model);
            var modelLimit = capability == null ? 0 : capability.MaxOutputTokens.GetValueOrDefault();
            if (modelLimit > 0) requested = Math.Min(requested, modelLimit);
            return Math.Max(128, Math.Min(requested, remaining));
        }

        public static bool SupportsImages(AppSettings settings, string model = null)
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
            return capability != null && capability.SupportsImages == true;
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

        public static int EstimateMessagesTokens(IEnumerable<ChatMessage> messages)
        {
            var total = 0;
            foreach (var message in messages ?? new ChatMessage[0])
            {
                if (message == null)
                {
                    continue;
                }
                total += 4 + EstimateTextTokens(message.Content);
                total += (message.Attachments ?? new List<ChatAttachment>())
                    .Where(attachment => attachment != null && attachment.Kind == "image")
                    .Count() * EstimatedImageTokens;
                total += (message.Attachments ?? new List<ChatAttachment>())
                    .Where(attachment => attachment != null)
                    .Sum(attachment => Math.Max(attachment.ExtractedCharCount, (attachment.ExtractedText ?? string.Empty).Length) / 2);
            }
            return total;
        }
    }
}
