using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;

namespace RNAssistant.Core.Tools
{
    // All coordinates belong to the original source, never to an intermediate edit.
    public sealed class ResolvedTextEdit
    {
        public int Offset { get; set; }
        public string Before { get; set; }
        public string After { get; set; }
    }

    public sealed class TextEditPlan
    {
        public string Text { get; private set; }
        public IReadOnlyList<ResolvedTextEdit> Edits { get; private set; }

        public static TextEditPlan Patch(string source, TextPatch patch,
            CancellationToken cancellation = default(CancellationToken), Func<string, string> normalizeLine = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            using (var reader = new System.IO.StringReader(source))
                return ApplyResolved(source, patch.Resolve(reader, cancellation, normalizeLine)
                    .Select(edit => new ResolvedTextEdit { Offset = checked((int)edit.Offset), Before = edit.Before, After = edit.After }), cancellation);
        }

        public static TextEditPlan ApplyResolved(string source, IEnumerable<ResolvedTextEdit> resolved,
            CancellationToken cancellation = default(CancellationToken))
        {
            var edits = resolved.ToList();
            foreach (var edit in edits)
            {
                if (edit?.Before == null || edit.After == null || !Matches(source, edit.Offset, edit.Before))
                    throw Error("patch_not_found", "Resolved edit no longer matches the source.");
                RequireScalarBoundary(source, edit.Offset);
                RequireScalarBoundary(source, edit.Offset + edit.Before.Length);
            }
            return Apply(source, edits, cancellation);
        }

        public static TextEditPlan Replace(string source, string query, string replacement,
            string mode, bool matchCase, int expectedMatches,
            CancellationToken cancellation = default(CancellationToken))
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (expectedMatches < 1 || expectedMatches > 10000)
                throw Error("replacement_count_invalid", "expectedMatches must be between 1 and 10000.");
            var edits = new List<ResolvedTextEdit>();
            var matcher = new ResourceTextMatcher(query, mode ?? "literal", matchCase);
            // The public resource regex search is line-oriented. Preserve exact terminators.
            if (matcher.IsRegex)
            {
                var start = 0;
                while (start < source.Length)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var end = start;
                    while (end < source.Length && source[end] != '\r' && source[end] != '\n') end++;
                    AddReplacements(source, start, source.Substring(start, end - start), replacement, matcher, edits);
                    if (end < source.Length && source[end++] == '\r' && end < source.Length && source[end] == '\n') end++;
                    start = end;
                }
            }
            else AddReplacements(source, 0, source, replacement, matcher, edits);
            if (edits.Count != expectedMatches)
                throw Error("replacement_count_conflict", "Expected " + expectedMatches + " matches but found " + edits.Count + ". Nothing was written.");
            return Apply(source, edits, cancellation);
        }

        private static void AddReplacements(string source, int offset, string text,
            string replacement, ResourceTextMatcher matcher, List<ResolvedTextEdit> edits)
        {
            try
            {
                for (var position = 0; position <= text.Length;)
                {
                    var match = matcher.Next(text, position);
                    if (match == null) break;
                    if (match.Length == 0) throw Error("zero_length_replacement", "Use core.patch for zero-length insertions.");
                    if (edits.Count >= 10000) throw Error("replacement_limit_exceeded", "Too many replacements; narrow the scope.");
                    RequireScalarBoundary(source, offset + match.Index);
                    RequireScalarBoundary(source, offset + match.Index + match.Length);
                    edits.Add(new ResolvedTextEdit { Offset = offset + match.Index,
                        Before = text.Substring(match.Index, match.Length), After = match.Replacement(replacement) });
                    position = match.Index + match.Length;
                }
            }
            catch (RegexMatchTimeoutException) { throw Error("pattern_timeout", "Pattern matching timed out."); }
        }

        private static TextEditPlan Apply(string source, List<ResolvedTextEdit> edits, CancellationToken cancellation)
        {
            edits.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            for (var i = 1; i < edits.Count; i++)
                if (edits[i].Offset == edits[i - 1].Offset || edits[i].Offset < edits[i - 1].Offset + edits[i - 1].Before.Length)
                    throw Error("patch_overlap", "Hunks overlap in the original source.");
            var builder = new StringBuilder();
            var position = 0;
            foreach (var edit in edits)
            {
                cancellation.ThrowIfCancellationRequested();
                builder.Append(source, position, edit.Offset - position);
                builder.Append(edit.After);
                position = edit.Offset + edit.Before.Length;
            }
            builder.Append(source, position, source.Length - position);
            return new TextEditPlan { Text = builder.ToString(), Edits = edits.AsReadOnly() };
        }

        private static bool Matches(string source, int offset, string value)
        { return offset >= 0 && value.Length <= source.Length - offset && string.CompareOrdinal(source, offset, value, 0, value.Length) == 0; }

        public static int Position(string text, int line, int column)
        {
            if (line < 1 || column < 1) throw Error("patch_location_invalid", "Coordinates are 1-based UTF-16 positions.");
            var start = 0;
            for (var current = 1; current < line; current++)
            {
                while (start < text.Length && text[start] != '\r' && text[start] != '\n') start++;
                if (start == text.Length) throw Error("patch_location_invalid", "Line is outside the source.");
                if (text[start++] == '\r' && start < text.Length && text[start] == '\n') start++;
            }
            var end = start;
            while (end < text.Length && text[end] != '\r' && text[end] != '\n') end++;
            if (column - 1 > end - start) throw Error("patch_location_invalid", "Column is outside the line.");
            return start + column - 1;
        }

        private static void RequireScalarBoundary(string text, int position)
        {
            if (position > 0 && position < text.Length && char.IsHighSurrogate(text[position - 1]) && char.IsLowSurrogate(text[position]))
                throw Error("patch_location_invalid", "An edit cannot split a Unicode surrogate pair.");
        }
        private static TextPatternException Error(string code, string message) { return new TextPatternException(code, message); }
    }
}
