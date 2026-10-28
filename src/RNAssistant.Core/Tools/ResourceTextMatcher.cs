using System;
using System.Text.RegularExpressions;

namespace RNAssistant.Core.Tools
{
    // One matching contract for search and replacement. Offsets are UTF-16, matches do not overlap.
    public sealed class ResourceTextMatcher
    {
        private readonly string _query;
        private readonly StringComparison _comparison;
        private readonly Regex _regex;
        public bool IsRegex { get { return _regex != null; } }
        public ResourceTextMatcher(string query, string mode, bool matchCase)
        {
            if (string.IsNullOrEmpty(query) || query.Length > 500 || query.IndexOfAny(new[] { '\r', '\n' }) >= 0 ||
                (mode != "literal" && mode != "regex"))
                throw new TextPatternException("invalid_pattern", "Use a single-line query of 1–500 characters and mode literal/regex.");
            _query = query; _comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (mode == "regex")
            {
                try { _regex = new Regex(query, RegexOptions.CultureInvariant |
                    (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromMilliseconds(100)); }
                catch (ArgumentException ex) { throw new TextPatternException("invalid_pattern", ex.Message); }
            }
        }
        public ResourceTextMatch Next(string text, int start)
        {
            if (_regex == null)
            {
                var index = text.IndexOf(_query, start, _comparison);
                return index < 0 ? null : new ResourceTextMatch(index, _query.Length, null);
            }
            var match = _regex.Match(text, start);
            return match.Success ? new ResourceTextMatch(match.Index, match.Length, match) : null;
        }
    }
    public sealed class ResourceTextMatch
    {
        private readonly Match _match;
        public int Index { get; }
        public int Length { get; }
        internal ResourceTextMatch(int index, int length, Match match) { Index = index; Length = length; _match = match; }
        public string Replacement(string replacement) { return _match == null ? replacement : _match.Result(replacement); }
    }
}
