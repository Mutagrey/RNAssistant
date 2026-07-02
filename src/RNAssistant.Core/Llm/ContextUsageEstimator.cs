using System;
using System.Collections.Generic;
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
            var limit = ModelContextBudget.InputBudgetTokens(settings);
            var usedChars = 0;
            var estimatedTokens = 0;
            var count = 0;
            if (promptMessages != null)
            {
                foreach (var message in promptMessages)
                {
                    if (message == null)
                    {
                        continue;
                    }

                    usedChars += (message.Content ?? string.Empty).Length;
                    estimatedTokens += 4 + ModelContextBudget.EstimateTextTokens(message.Content);
                    foreach (var attachment in message.Attachments ?? new List<ChatAttachment>())
                    {
                        if (attachment == null) continue;
                        usedChars += attachment.ExtractedCharCount > 0
                            ? attachment.ExtractedCharCount
                            : (attachment.ExtractedText ?? string.Empty).Length;
                        estimatedTokens += Math.Max(attachment.ExtractedCharCount, (attachment.ExtractedText ?? string.Empty).Length) / 2;
                        if (attachment.Kind == "image") estimatedTokens += ModelContextBudget.EstimatedImageTokens;
                    }
                    count += 1;
                }
            }

            return Usage(usedChars, actualPromptTokens ?? estimatedTokens, limit, count, actualPromptTokens.HasValue);
        }

        public static object FromSession(ChatSession session, AppSettings settings)
        {
            var limit = ModelContextBudget.InputBudgetTokens(settings);
            var usedChars = 0;
            var usedTokens = 0;
            var count = 0;
            if (session != null && session.Messages != null)
            {
                foreach (var message in session.Messages)
                {
                    if (message == null ||
                        message.Activity != null ||
                        string.IsNullOrWhiteSpace(message.Content) ||
                        (!string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    usedChars += (message.Content ?? string.Empty).Length;
                    usedTokens += 4 + ModelContextBudget.EstimateTextTokens(message.Content);
                    foreach (var attachment in message.Attachments ?? new List<ChatAttachment>())
                    {
                        if (attachment == null)
                        {
                            continue;
                        }
                        var extractedChars = Math.Max(
                            attachment.ExtractedCharCount,
                            (attachment.ExtractedText ?? string.Empty).Length);
                        usedChars += extractedChars;
                        usedTokens += extractedChars / 2;
                        if (attachment.Kind == "image")
                        {
                            usedTokens += ModelContextBudget.EstimatedImageTokens;
                        }
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
                    usedTokens += ModelContextBudget.EstimateTextTokens(text);
                }
            }

            return Usage(usedChars, usedTokens, limit, count, false);
        }

        private static object Usage(int usedChars, int usedTokens, int limitTokens, int count, bool actual)
        {
            return new
            {
                usedChars = usedChars,
                limitChars = 0,
                usedTokens = usedTokens,
                limitTokens = limitTokens,
                percent = limitTokens <= 0 ? 0 : Math.Min(100, (int)Math.Round(usedTokens * 100.0 / limitTokens)),
                messageCount = count,
                actual = actual
            };
        }
    }
}
