using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal static class ChatTitleBuilder
    {
        public static void ApplyFallback(ChatSession session, string userText, string assistantText)
        {
            if (!ShouldAssign(session))
            {
                return;
            }

            var title = BuildFallback(assistantText, userText);
            if (!string.IsNullOrWhiteSpace(title))
            {
                session.Title = title;
            }
        }

        public static async Task<string> GenerateLlmTitleAsync(
            AppSettings settings,
            string userText,
            string assistantText,
            Func<AppSettings, IEnumerable<ChatMessage>, CancellationToken, Task<LlmCompletionResult>> completeAsync,
            CancellationToken cancellationToken)
        {
            if (settings == null || completeAsync == null)
            {
                return BuildFallback(assistantText, userText);
            }

            var messages = new[]
            {
                new ChatMessage
                {
                    Role = "system",
                    Content = "Ты называешь чаты. Верни только короткое название на языке пользователя: 2-6 слов, без кавычек, точки, markdown и пояснений."
                },
                new ChatMessage
                {
                    Role = "user",
                    Content =
                        "Запрос пользователя:\n" + Clip(CleanSource(userText), 1400) +
                        "\n\nОтвет ассистента:\n" + Clip(CleanSource(assistantText), 1800)
                }
            };

            var completion = await completeAsync(CreateTitleSettings(settings), messages, cancellationToken).ConfigureAwait(false);
            var title = CleanLlmTitle(completion == null ? null : completion.Content);
            return string.IsNullOrWhiteSpace(title)
                ? BuildFallback(assistantText, userText)
                : title;
        }

        public static string BuildFallbackTitle(string userText, string assistantText)
        {
            return BuildFallback(assistantText, userText);
        }

        public static bool ShouldAssign(ChatSession session)
        {
            if (session == null)
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(session.Title)
                || string.Equals(session.Title.Trim(), "New chat", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildFallback(string assistantText, string userText)
        {
            var title = FirstCandidate(assistantText, false);
            if (string.IsNullOrWhiteSpace(title))
            {
                title = FirstCandidate(userText, false);
            }

            return Limit(title);
        }

        private static AppSettings CreateTitleSettings(AppSettings source)
        {
            return new AppSettings
            {
                BaseUrl = source.BaseUrl,
                Model = source.Model,
                SystemPrompt = source.SystemPrompt,
                AgentPrompt = source.AgentPrompt,
                MaxTokens = 32,
                RequestTimeoutSeconds = Math.Max(30, Math.Min(source.RequestTimeoutSeconds <= 0 ? 300 : source.RequestTimeoutSeconds, 60)),
                Temperature = Math.Min(Math.Max(source.Temperature, 0), 0.2),
                TopP = source.TopP <= 0 ? 1.0 : Math.Min(source.TopP, 1.0),
                ContextCharLimit = source.ContextCharLimit,
                StreamResponses = false,
                AutoRunToolCalls = source.AutoRunToolCalls,
                AutoConfirmToolActions = source.AutoConfirmToolActions,
                AutoRetryToolErrors = source.AutoRetryToolErrors,
                SmartChatTitles = source.SmartChatTitles,
                IncludeVbaContext = source.IncludeVbaContext,
                VbaContextCharLimit = source.VbaContextCharLimit,
                MaxAgentIterations = source.MaxAgentIterations,
                MaxAgentToolSteps = source.MaxAgentToolSteps,
                RequireVerificationForMutations = source.RequireVerificationForMutations,
                AutoContinueAfterConfirmation = source.AutoContinueAfterConfirmation,
                AllowUnsafeHtmlArtifacts = source.AllowUnsafeHtmlArtifacts,
                AgentPrompts = source.AgentPrompts,
                UiFontScale = source.UiFontScale,
                CustomHeaders = source.CustomHeaders == null
                    ? null
                    : new Dictionary<string, string>(source.CustomHeaders, StringComparer.OrdinalIgnoreCase)
            };
        }

        private static string CleanLlmTitle(string text)
        {
            var title = FirstCandidate(text, false);
            title = Regex.Replace(title, "^(название|title)\\s*[:\\-]\\s*", string.Empty, RegexOptions.IgnoreCase);
            return Limit(title);
        }

        private static string Clip(string text, int maxChars)
        {
            text = Normalize(text);
            if (string.IsNullOrWhiteSpace(text) || maxChars <= 0 || text.Length <= maxChars)
            {
                return text;
            }

            return text.Substring(0, maxChars).TrimEnd();
        }

        private static string FirstCandidate(string text, bool removeRequestPrefix)
        {
            foreach (var candidate in ExtractCandidates(text))
            {
                var title = removeRequestPrefix ? RemoveRequestPrefix(candidate) : candidate;
                title = Normalize(title);
                if (!string.IsNullOrWhiteSpace(title))
                {
                    return title;
                }
            }

            return string.Empty;
        }

        private static IEnumerable<string> ExtractCandidates(string text)
        {
            var clean = CleanSource(text);
            if (string.IsNullOrWhiteSpace(clean))
            {
                yield break;
            }

            var parts = Regex.Split(clean, "[\\.!?\\n]+");
            foreach (var part in parts)
            {
                var value = Normalize(part);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    yield return value;
                }
            }
        }

        private static string CleanSource(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var value = text.Replace("\r", "\n");
            value = Regex.Replace(value, "```[\\s\\S]*?```", " ");
            value = Regex.Replace(value, "`([^`]*)`", "$1");
            value = Regex.Replace(value, "<[^>]+>", " ");
            value = Regex.Replace(value, "\\brnassistant-(agent|skill)\\b", " ", RegexOptions.IgnoreCase);

            var lines = value
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Where(line => !line.StartsWith("{", StringComparison.Ordinal))
                .Where(line => !line.StartsWith("[", StringComparison.Ordinal))
                .Where(line => line.IndexOf("\"toolId\"", StringComparison.OrdinalIgnoreCase) < 0)
                .Where(line => line.IndexOf("\"steps\"", StringComparison.OrdinalIgnoreCase) < 0);

            return string.Join("\n", lines);
        }

        private static string RemoveRequestPrefix(string text)
        {
            var value = Normalize(text);
            value = Regex.Replace(value, "^(еще\\s+)?(я\\s+хочу\\s+)?", string.Empty, RegexOptions.IgnoreCase);
            value = Regex.Replace(
                value,
                "^(пожалуйста|можешь|можно|нужно|надо|сделай|создай|напиши|проверь|добавь|исправь|улучши|реализуй|обнови|please|can you|could you|make|create|write|check|add|fix|improve|update|implement)\\s+",
                string.Empty,
                RegexOptions.IgnoreCase);
            return Normalize(value);
        }

        private static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var value = Regex.Replace(text.Trim(), "\\s+", " ");
            return value.Trim(' ', '\t', '-', '*', '#', ':', ';', ',', '.', '"', '\'');
        }

        private static string Limit(string title)
        {
            title = Normalize(title);
            if (title.Length > 0)
            {
                title = char.ToUpperInvariant(title[0]) + title.Substring(1);
            }

            if (title.Length <= 64)
            {
                return title;
            }

            var shortened = title.Substring(0, 61);
            var lastSpace = shortened.LastIndexOf(' ');
            if (lastSpace >= 32)
            {
                shortened = shortened.Substring(0, lastSpace);
            }

            return shortened.TrimEnd() + "...";
        }
    }
}
