using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RNAssistant.Core.Tools
{
    public sealed class TextPatternOptions
    {
        public string Mode { get; set; }
        public bool MatchCase { get; set; }
        public bool WholeWord { get; set; }

        public TextPatternOptions()
        {
            Mode = "literal";
        }
    }

    public sealed class TextPatternMatch
    {
        public int Index { get; set; }
        public int Length { get; set; }
        public string Value { get; set; }
        public string Preview { get; set; }
    }

    public sealed class TextPatternSearchResult
    {
        public int MatchCount { get; set; }
        public bool Truncated { get; set; }
        public List<TextPatternMatch> Matches { get; private set; }

        public TextPatternSearchResult()
        {
            Matches = new List<TextPatternMatch>();
        }
    }

    public sealed class TextPatternReplaceResult
    {
        public string Text { get; set; }
        public int MatchCount { get; set; }
    }

    public sealed class TextPatternReplacement
    {
        public int Index { get; set; }
        public int Length { get; set; }
        public string Text { get; set; }
    }

    public sealed class TextPatternException : Exception
    {
        public string ErrorCode { get; private set; }

        public TextPatternException(string errorCode, string message)
            : base(message)
        {
            ErrorCode = errorCode;
        }
    }

    public static class TextPatternEngine
    {
        public const int MaxPatternChars = 2048;
        public const int MaxResults = 500;
        private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

        public static TextPatternSearchResult Find(
            string text,
            string pattern,
            TextPatternOptions options,
            int maxResults,
            int contextChars)
        {
            text = text ?? string.Empty;
            maxResults = Math.Max(1, Math.Min(MaxResults, maxResults));
            contextChars = Math.Max(0, Math.Min(1000, contextChars));
            var result = new TextPatternSearchResult();
            try
            {
                var regex = BuildRegex(pattern, options);
                foreach (Match match in regex.Matches(text))
                {
                    result.MatchCount += 1;
                    if (result.Matches.Count < maxResults)
                    {
                        result.Matches.Add(new TextPatternMatch
                        {
                            Index = match.Index,
                            Length = match.Length,
                            Value = match.Value,
                            Preview = Preview(text, match.Index, match.Length, contextChars)
                        });
                    }
                    else
                    {
                        result.Truncated = true;
                        break;
                    }
                }
                return result;
            }
            catch (RegexMatchTimeoutException)
            {
                throw new TextPatternException("pattern_timeout", "Pattern matching timed out.");
            }
        }

        public static TextPatternReplaceResult Replace(
            string text,
            string pattern,
            string replacement,
            TextPatternOptions options,
            bool replaceAll,
            int maxReplacements)
        {
            text = text ?? string.Empty;
            replacement = replacement ?? string.Empty;
            maxReplacements = Math.Max(1, Math.Min(10000, maxReplacements));
            try
            {
                var regex = BuildRegex(pattern, options);
                var edits = PlanReplacements(text, regex, replacement, replaceAll, maxReplacements);
                var builder = new StringBuilder(text);
                for (var index = edits.Count - 1; index >= 0; index--)
                {
                    var edit = edits[index];
                    builder.Remove(edit.Index, edit.Length);
                    builder.Insert(edit.Index, edit.Text);
                }
                return new TextPatternReplaceResult { Text = builder.ToString(), MatchCount = edits.Count };
            }
            catch (RegexMatchTimeoutException)
            {
                throw new TextPatternException("pattern_timeout", "Pattern matching timed out.");
            }
        }

        public static List<TextPatternReplacement> PlanReplacements(
            string text,
            string pattern,
            string replacement,
            TextPatternOptions options,
            bool replaceAll,
            int maxReplacements)
        {
            try
            {
                return PlanReplacements(text ?? string.Empty, BuildRegex(pattern, options), replacement ?? string.Empty, replaceAll, maxReplacements);
            }
            catch (RegexMatchTimeoutException)
            {
                throw new TextPatternException("pattern_timeout", "Pattern matching timed out.");
            }
        }

        private static List<TextPatternReplacement> PlanReplacements(string text, Regex regex, string replacement, bool replaceAll, int maxReplacements)
        {
            maxReplacements = Math.Max(1, Math.Min(10000, maxReplacements));
            var edits = new List<TextPatternReplacement>();
            foreach (Match match in regex.Matches(text))
            {
                if (match.Length == 0)
                {
                    throw new TextPatternException("zero_length_replacement", "A replacement pattern must not produce zero-length matches.");
                }
                edits.Add(new TextPatternReplacement { Index = match.Index, Length = match.Length, Text = match.Result(replacement) });
                if (!replaceAll) break;
                if (edits.Count > maxReplacements)
                {
                    throw new TextPatternException("replacement_limit_exceeded", "Replacement count exceeds maxReplacements=" + maxReplacements + ".");
                }
            }
            return edits;
        }

        public static string Sha256(string value)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static Regex BuildRegex(string pattern, TextPatternOptions options)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                throw new TextPatternException("invalid_pattern", "Pattern is required.");
            }
            if (pattern.Length > MaxPatternChars)
            {
                throw new TextPatternException("invalid_pattern", "Pattern exceeds " + MaxPatternChars + " characters.");
            }

            options = options ?? new TextPatternOptions();
            var expression = string.Equals(options.Mode, "regex", StringComparison.OrdinalIgnoreCase)
                ? pattern
                : Regex.Escape(pattern);
            if (options.WholeWord)
            {
                expression = "(?<!\\w)(?:" + expression + ")(?!\\w)";
            }
            var regexOptions = RegexOptions.CultureInvariant;
            if (!options.MatchCase) regexOptions |= RegexOptions.IgnoreCase;
            try
            {
                return new Regex(expression, regexOptions, MatchTimeout);
            }
            catch (ArgumentException ex)
            {
                throw new TextPatternException("invalid_pattern", "Invalid regular expression: " + ex.Message);
            }
        }

        private static string Preview(string text, int index, int length, int contextChars)
        {
            var start = Math.Max(0, index - contextChars);
            var end = Math.Min(text.Length, index + length + contextChars);
            return text.Substring(start, Math.Max(0, end - start));
        }
    }
}
