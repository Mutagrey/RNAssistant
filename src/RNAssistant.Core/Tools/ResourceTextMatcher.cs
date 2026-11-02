using System;
using System.Text.RegularExpressions;

namespace RNAssistant.Core.Tools
{
    // One matching contract for search and replacement. Offsets are UTF-16, matches do not overlap.
    public sealed class ResourceTextMatcher
    {
        public const string LineSearchGuidance = "Search matches one physical line at a time, without line endings. " +
            "For a multiline change, search a short single-line fragment, read the surrounding source with core.read, " +
            "then use core.patch with exact old/new text including line endings.";
        private readonly string _query;
        private readonly StringComparison _comparison;
        private readonly Regex _regex;
        public bool IsRegex { get { return _regex != null; } }
        public ResourceTextMatcher(string query, string mode, bool matchCase)
        {
            if (string.IsNullOrEmpty(query) || query.Length > 500 ||
                (mode != "literal" && mode != "regex"))
                throw new TextPatternException("invalid_pattern", "Use a single-line query of 1–500 characters and mode literal/regex.");
            if (query.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                throw new TextPatternException("multiline_pattern_unsupported", LineSearchGuidance);
            _query = query; _comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (mode == "regex")
            {
                try { _regex = new Regex(query, RegexOptions.CultureInvariant |
                    (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromMilliseconds(100)); }
                catch (ArgumentException ex) { throw new TextPatternException("invalid_pattern", ex.Message); }
                if (HasLineEndingEscape(query))
                    throw new TextPatternException("multiline_pattern_unsupported", LineSearchGuidance);
            }
        }

        // Reject explicit CR/LF tokens rather than claiming a complete zero-match search.
        // Negated classes such as [^\r\n] and escaped backslashes remain line-local.
        private static bool HasLineEndingEscape(string query)
        {
            var inClass = false; var negated = false; var classStart = -1;
            for (var i = 0; i < query.Length; i++)
            {
                if (!inClass && query[i] == '(' && i + 2 < query.Length && query[i + 1] == '?' && query[i + 2] == '#')
                { i = query.IndexOf(')', i + 3); if (i < 0) return false; continue; }
                if (query[i] == '\\' && i + 1 < query.Length)
                {
                    var escaped = query[++i];
                    var newline = escaped == 'r' || escaped == 'n';
                    var digits = escaped == 'x' ? 2 : escaped == 'u' ? 4 : 0;
                    int code;
                    if (digits > 0 && i + digits < query.Length && int.TryParse(query.Substring(i + 1, digits),
                        System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out code))
                    { newline = code == 10 || code == 13; i += digits; }
                    else if (escaped == 'c' && i + 1 < query.Length)
                    { var control = char.ToUpperInvariant(query[++i]); newline = control == 'J' || control == 'M'; }
                    if (newline && (!inClass || !negated)) return true;
                    continue;
                }
                if (!inClass && query[i] == '[')
                { inClass = true; negated = i + 1 < query.Length && query[i + 1] == '^'; classStart = i + (negated ? 2 : 1); }
                else if (inClass && query[i] == ']' && i > classStart) inClass = false;
            }
            return false;
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
