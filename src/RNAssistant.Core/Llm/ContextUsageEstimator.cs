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

        public static object FromPrompt(IEnumerable<ChatMessage> promptMessages, AppSettings settings, int? actualPromptTokens, LlmRequestOptions requestOptions)
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
            return Usage(usedChars, actualPromptTokens ?? estimatedTokens, limit, count, actualPromptTokens.HasValue, settings);
        }

        public static object FromSession(ChatSession session, AppSettings settings)
        {
            var limit = ModelContextBudget.InputBudgetTokens(settings);
            var usedChars = 0;
            var usedTokens = 0;
            var baseTokens = 0;
            var hasMedia = false;
            var count = 0;
            if (session != null && session.Messages != null)
            {
                var startIndex = 0;
                var checkpoint = session.ContextCheckpoints == null || string.IsNullOrWhiteSpace(session.ActiveContextCheckpointId)
                    ? null
                    : session.ContextCheckpoints.FirstOrDefault(item => item != null &&
                        string.Equals(item.Id, session.ActiveContextCheckpointId, StringComparison.OrdinalIgnoreCase));
                if (checkpoint != null)
                {
                    usedChars += (checkpoint.SummaryMarkdown ?? string.Empty).Length;
                    usedTokens += ModelContextBudget.EstimateTextTokens(checkpoint.SummaryMarkdown, settings);
                    baseTokens += ModelContextBudget.EstimateTextTokens(checkpoint.SummaryMarkdown);
                    count += 1;
                    var throughIndex = session.Messages.FindIndex(message => message != null &&
                        string.Equals(message.Id, checkpoint.ThroughMessageId, StringComparison.OrdinalIgnoreCase));
                    if (throughIndex >= 0) startIndex = throughIndex + 1;
                }
                foreach (var message in session.Messages.Skip(startIndex))
                {
                    var protocolTool = message != null && message.ProtocolMessage &&
                        (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(message.Role, "developer", StringComparison.OrdinalIgnoreCase));
                    if (message == null || message.ExcludeFromModelContext || message.Activity != null ||
                        !protocolTool && (string.IsNullOrWhiteSpace(message.Content) ||
                        (!string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))))
                    {
                        continue;
                    }

                    usedChars += (message.Content ?? string.Empty).Length;
                    usedChars += message.AttachmentAnalysis == null
                        ? 0
                        : (message.AttachmentAnalysis.Content ?? string.Empty).Length;
                    usedTokens += ModelContextBudget.EstimateMessageTokens(message, settings, null, false);
                    baseTokens += ModelContextBudget.EstimateMessageTokens(message, null, null, false);
                    foreach (var attachment in message.Attachments ?? new List<ChatAttachment>())
                    {
                        if (attachment == null)
                        {
                            continue;
                        }
                        var analyzed = IsAnalyzedAttachment(message, attachment);
                        hasMedia = hasMedia ||
                            !analyzed && (string.Equals(attachment.Kind, "image", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(attachment.Kind, "audio", StringComparison.OrdinalIgnoreCase));
                        if (analyzed) continue;
                        var extractedChars = Math.Max(
                            attachment.ExtractedCharCount,
                            (attachment.ExtractedText ?? string.Empty).Length);
                        usedChars += extractedChars;
                        usedTokens += ModelContextBudget.EstimateCharacterCountTokens(extractedChars, settings);
                        baseTokens += ModelContextBudget.EstimateCharacterCountTokens(extractedChars, null);
                    }
                    count += 1;
                }
            }
            if (session != null && session.Context != null && session.Context.Notes != null)
            {
                var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var note in session.Context.Notes)
                {
                    if (note == null)
                    {
                        continue;
                    }

                    var text = note.Text ?? note.Preview ?? string.Empty;
                    var identity = !string.IsNullOrWhiteSpace(note.Reference)
                        ? note.Host + "|" + note.Kind + "|" + note.Reference
                        : note.Id;
                    if (string.IsNullOrWhiteSpace(text) || !included.Add(identity))
                    {
                        continue;
                    }
                    usedChars += text.Length;
                    usedTokens += ModelContextBudget.EstimateTextTokens(text, settings);
                    baseTokens += ModelContextBudget.EstimateTextTokens(text);
                }
            }

            usedTokens = hasMedia
                ? TokenEstimateCalibration.AddPromptIntercept(settings, usedTokens)
                : TokenEstimateCalibration.PredictPromptTokens(settings, baseTokens);
            return Usage(usedChars, usedTokens, limit, count, false, settings);
        }

        private static bool IsAnalyzedAttachment(ChatMessage message, ChatAttachment attachment)
        {
            return message != null && attachment != null && !string.IsNullOrWhiteSpace(attachment.Id) &&
                message.AttachmentAnalysis != null &&
                (message.AttachmentAnalysis.AttachmentIds ?? new List<string>())
                    .Contains(attachment.Id, StringComparer.OrdinalIgnoreCase);
        }

        private static object Usage(int usedChars, int usedTokens, int limitTokens, int count, bool actual, AppSettings settings)
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
