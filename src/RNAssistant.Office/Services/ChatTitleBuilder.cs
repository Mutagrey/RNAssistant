using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal static class ChatTitleBuilder
    {
        public static void ApplyDeferred(AppSettings settings, ChatSession session, string userText, string assistantText)
        {
            if (!ShouldAssign(session))
            {
                return;
            }

            var title = settings != null && settings.SmartChatTitles == false
                ? BuildFallback(assistantText, userText)
                : BuildSmart(userText, assistantText);
            if (!string.IsNullOrWhiteSpace(title))
            {
                session.Title = title;
            }
        }

        private static bool ShouldAssign(ChatSession session)
        {
            if (session == null)
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(session.Title)
                || string.Equals(session.Title.Trim(), "New chat", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildSmart(string userText, string assistantText)
        {
            var title = FirstCandidate(userText, true);
            if (string.IsNullOrWhiteSpace(title))
            {
                title = FirstCandidate(assistantText, true);
            }

            return Limit(title);
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
