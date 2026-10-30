using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Llm
{
    public static class ContextUsageEstimator
    {
        public static object FromPrompt(IEnumerable<ChatMessage> promptMessages, AppSettings settings)
        {
            return FromPrompt(promptMessages, settings, null);
        }

        public static object FromPrompt(IEnumerable<ChatMessage> promptMessages, AppSettings settings, int? actualPromptTokens)
        {
            return FromPrompt(promptMessages, settings, actualPromptTokens, null);
        }

        public static object FromPrompt(IEnumerable<ChatMessage> promptMessages, AppSettings settings, int? actualPromptTokens,
            LlmRequestOptions requestOptions, int? actualCompletionTokens = null, int? actualTotalTokens = null)
        {
            var limit = ModelContextBudget.InputBudgetTokens(settings);
            var usedChars = 0;
            var estimatedTokens = 0;
            var baseEstimatedTokens = 0;
            var hasMedia = false;
            var count = 0;
            if (promptMessages != null)
            {
                foreach (var message in promptMessages)
                {
                    if (message == null || message.ExcludeFromModelContext)
                    {
                        continue;
                    }

                    usedChars += (message.Content ?? string.Empty).Length;
                    usedChars += message.AttachmentAnalysis == null
                        ? 0
                        : (message.AttachmentAnalysis.Content ?? string.Empty).Length;
                    estimatedTokens += ModelContextBudget.EstimateMessageTokens(message, settings, null, false);
                    baseEstimatedTokens += ModelContextBudget.EstimateMessageTokens(message, null, null, false);
                    foreach (var attachment in message.Attachments ?? new List<ChatAttachment>())
                    {
                        if (attachment == null) continue;
                        var analyzed = IsAnalyzedAttachment(message, attachment);
                        hasMedia = hasMedia ||
                            !analyzed && (string.Equals(attachment.Kind, "image", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(attachment.Kind, "audio", StringComparison.OrdinalIgnoreCase));
                        if (analyzed) continue;
                        usedChars += attachment.ExtractedCharCount > 0
                            ? attachment.ExtractedCharCount
                            : (attachment.ExtractedText ?? string.Empty).Length;
                        estimatedTokens += ModelContextBudget.EstimateCharacterCountTokens(
                            Math.Max(attachment.ExtractedCharCount, (attachment.ExtractedText ?? string.Empty).Length),
                            settings);
                        baseEstimatedTokens += ModelContextBudget.EstimateCharacterCountTokens(
                            Math.Max(attachment.ExtractedCharCount, (attachment.ExtractedText ?? string.Empty).Length),
                            null);
                    }
                    count += 1;
                }
            }

            var baseOptionsTokens = ModelContextBudget.EstimateRequestOptionsTokens(requestOptions);
            estimatedTokens = hasMedia
                ? TokenEstimateCalibration.AddPromptIntercept(settings, estimatedTokens) +
                    ModelContextBudget.EstimateRequestOptionsTokens(requestOptions, settings)
                : TokenEstimateCalibration.PredictPromptTokens(settings, baseEstimatedTokens + baseOptionsTokens);
            return Usage(usedChars, actualPromptTokens ?? estimatedTokens, limit, count,
                actualPromptTokens.HasValue, settings, "request", actualPromptTokens, actualCompletionTokens, actualTotalTokens);
        }

        // UI snapshots need the last provider usage, not another projection of the
        // entire history. Full estimates belong to request compilation/preview.
        public static object FromSession(ChatSession session, AppSettings settings)
        {
            ChatMessage lastUsage = null;
            var messages = session == null ? null : session.Messages;
            for (var index = messages == null ? -1 : messages.Count - 1; index >= 0; index--)
            {
                var message = messages[index];
                if (message == null || message.RuntimeProjection ||
                    !string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) ||
                    message.AcceptedCallOrigin != null && message.AcceptedCallOrigin.CallIndex != 0)
                    continue;
                if (!message.ProtocolMessage && !message.PromptTokens.HasValue &&
                    !message.CompletionTokens.HasValue && !message.TotalTokens.HasValue) continue;
                lastUsage = message;
                break;
            }
            return Usage(0, lastUsage?.PromptTokens ?? 0, ModelContextBudget.InputBudgetTokens(settings), 0,
                lastUsage?.PromptTokens.HasValue == true, settings, "last_response",
                lastUsage?.PromptTokens, lastUsage?.CompletionTokens, lastUsage?.TotalTokens);
        }

        private static bool IsAnalyzedAttachment(ChatMessage message, ChatAttachment attachment)
        {
            return message != null && attachment != null && !string.IsNullOrWhiteSpace(attachment.Id) &&
                message.AttachmentAnalysis != null &&
                (message.AttachmentAnalysis.AttachmentIds ?? new List<string>())
                    .Contains(attachment.Id, StringComparer.OrdinalIgnoreCase);
        }

        private static object Usage(int usedChars, int usedTokens, int limitTokens, int count, bool actual,
            AppSettings settings, string scope, int? lastPromptTokens, int? lastCompletionTokens, int? lastTotalTokens)
        {
            var contextWindowTokens = Math.Max(4096, ModelContextBudget.ContextWindowTokens(settings));
            var safetyTokens = ModelContextBudget.SafetyReserveTokens(contextWindowTokens);
            var reservedOutputTokens = Math.Max(1, contextWindowTokens - safetyTokens - limitTokens);
            var availableOutputTokens = Math.Max(0, contextWindowTokens - safetyTokens - usedTokens);
            var calibration = TokenEstimateCalibration.Get(settings);
            return new
            {
                usedChars = usedChars,
                limitChars = 0,
                usedTokens = usedTokens,
                limitTokens = limitTokens,
                percent = limitTokens <= 0 ? 0 : Math.Min(100, (int)Math.Round(usedTokens * 100.0 / limitTokens)),
                messageCount = count,
                actual = actual,
                scope = scope,
                lastPromptTokens = lastPromptTokens,
                lastCompletionTokens = lastCompletionTokens,
                lastTotalTokens = lastTotalTokens,
                contextWindowTokens = contextWindowTokens,
                reservedOutputTokens = reservedOutputTokens,
                maxOutputTokens = ModelContextBudget.RequestedOutputTokens(settings),
                safetyTokens = safetyTokens,
                availableOutputTokens = availableOutputTokens,
                estimateMultiplier = TokenEstimateCalibration.EffectiveMultiplier(settings),
                estimateInterceptTokens = TokenEstimateCalibration.EffectiveInterceptTokens(settings),
                calibrationSamples = TokenEstimateCalibration.SampleCount(settings),
                calibrationMultiplier = calibration == null ? 1.0 : calibration.Multiplier,
                calibrationInterceptTokens = calibration == null ? 0 : calibration.InterceptTokens,
                calibrationLastEstimatedPromptTokens = calibration == null ? 0 : calibration.LastEstimatedPromptTokens,
                calibrationLastActualPromptTokens = calibration == null ? 0 : calibration.LastActualPromptTokens,
                calibrationUpdatedUtc = calibration == null ? null : (DateTime?)calibration.UpdatedUtc,
                calibrationProfile = calibration == null ? null : calibration.Clone(),
                estimateMethod = "utf8_bytes_div_4_linear_calibrated",
                estimateModel = settings == null ? string.Empty : settings.Model ?? string.Empty,
                manualEstimateMultiplier = settings == null || settings.TokenEstimateMultiplier <= 0
                    ? AppSettings.DefaultTokenEstimateMultiplier
                    : settings.TokenEstimateMultiplier,
                autoCalibrateEstimate = settings != null && settings.AutoCalibrateTokenEstimate
            };
        }
    }
}
