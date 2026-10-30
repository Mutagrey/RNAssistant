using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Tools
{
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class TextEditIntent
    {
        [JsonProperty("old", Required = Required.Always)] public string Old { get; set; }
        [JsonProperty("new", Required = Required.Always)] public string New { get; set; }
        [JsonProperty("anchors_before", NullValueHandling = NullValueHandling.Ignore)] public string[] AnchorsBefore { get; set; }
        [JsonProperty("anchors_after", NullValueHandling = NullValueHandling.Ignore)] public string[] AnchorsAfter { get; set; }
    }

    // Typed intents share one streaming resolver. No model offsets or diff parser.
    public sealed class TextPatch
    {
        private readonly IReadOnlyList<TextEditIntent> _edits;
        internal IReadOnlyList<TextEditIntent> Intents => _edits;
        private readonly IReadOnlyList<Tuple<long, long>> _coverage;
        public TextPatch(IEnumerable<TextEditIntent> edits, IEnumerable<Tuple<long, long>> coverage = null)
        {
            _edits = (edits ?? Enumerable.Empty<TextEditIntent>()).ToArray();
            _coverage = coverage?.OrderBy(r => r.Item1).ToArray();
            if (_edits.Count == 0 || _edits.Count > 100) throw Error("invalid_edit", "Use 1 to 100 edits per target.");
            for (var i = 0; i < _edits.Count; i++)
            {
                var e = _edits[i];
                if (e == null || string.IsNullOrEmpty(e.Old) || e.New == null)
                    throw Error("invalid_edit", "Use exact nonempty old and literal new (empty deletes).", i + 1);
                var before = e.AnchorsBefore ?? new string[0];
                var after = e.AnchorsAfter ?? new string[0];
                var anchors = before.Concat(after).ToArray();
                if (before.Length > 100 || after.Length > 100 || anchors.Any(a => a == null || a.IndexOfAny(new[] { '\r', '\n' }) >= 0))
                    throw Error("invalid_edit", "Each anchor array allows up to 100 complete lines without EOL.", i + 1);
                foreach (var text in anchors.Concat(new[] { e.Old, e.New })) ValidateUnicode(text, i + 1);
            }
        }
        public TextPatch WithCoverage(IEnumerable<Tuple<long, long>> coverage) { return new TextPatch(_edits, coverage); }
        public static IReadOnlyList<Tuple<long, long>> Coverage(IEnumerable<ResourceEvidence> evidence)
        {
            var items = (evidence ?? Enumerable.Empty<ResourceEvidence>()).ToArray();
            if (items.Any(e => e.Complete && e.Coverage.Kind == ResourceCoverageKinds.Whole)) return null;
            return items.Where(e => e.Coverage.Kind == ResourceCoverageKinds.CharacterRange && e.Coverage.Start.HasValue && e.Coverage.End.HasValue)
                .Select(e => Tuple.Create(e.Coverage.Start.Value, e.Coverage.End.Value)).ToArray();
        }
        public static TextPatch FromArguments(IDictionary<string, object> arguments)
        {
            object value;
            if (arguments.TryGetValue("edits", out value) && value is TextPatch) return (TextPatch)value;
            throw Error("invalid_edit", "Missing typed target edits.");
        }
        private bool Covered(long start, long end)
        {
            if (_coverage == null) return true;
            var cursor = start;
            foreach (var range in _coverage)
            {
                if (range.Item1 > cursor) break;
                if (range.Item2 >= cursor) { cursor = range.Item2; if (cursor >= end) return true; }
            }
            return false;
        }
        private static void ValidateUnicode(string text, int edit)
        {
            for (var i = 0; i < text.Length; i++)
                if (char.IsHighSurrogate(text[i]))
                { if (++i == text.Length || !char.IsLowSurrogate(text[i])) throw Error("invalid_edit", "Text splits a Unicode scalar.", edit); }
                else if (char.IsLowSurrogate(text[i]) || text[i] == '\0') throw Error("invalid_edit", "Text contains an invalid scalar or NUL.", edit);
        }
        private struct Unit
        {
            internal long Offset, Line;
            internal bool Boundary;
        }
        private sealed class Candidate
        {
            internal long Offset, End, Line;
            internal string Before;
        }
        private sealed class Line
        {
            internal long Offset, End, Number;
            internal string Text, Ending;
        }
        private sealed class State
        {
            internal TextEditIntent Intent;
            internal int[] Prefix;
            internal Unit[] Ring;
            internal long Seen;
            internal int Matched, BeforeIndex;
            internal bool Unobserved;
            internal long BeforeEnd;
            internal string[] BeforeAnchors, AfterAnchors, CanonicalLines, CanonicalEndings;
            internal readonly Queue<Line> Lines = new Queue<Line>();
            internal List<Candidate>[] Exact, Canonical;
        }
        private static List<Candidate>[] Buckets(int n)
        { return Enumerable.Range(0, n + 1).Select(i => new List<Candidate>(2)).ToArray(); }
        private static void Add(List<Candidate> bucket, Candidate candidate)
        {
            if (bucket.Count < 2 && !bucket.Any(c => c.Offset == candidate.Offset && c.End == candidate.End)) bucket.Add(candidate);
        }
        private static State CreateState(TextEditIntent intent, Func<string, string> key)
        {
            var pattern = intent.Old;
            var prefix = new int[pattern.Length];
            for (var i = 1; i < pattern.Length; i++)
            {
                var j = prefix[i - 1];
                while (j > 0 && pattern[i] != pattern[j]) j = prefix[j - 1];
                if (pattern[i] == pattern[j]) j++;
                prefix[i] = j;
            }
            var before = (intent.AnchorsBefore ?? new string[0]).Select(key).ToArray();
            var after = (intent.AnchorsAfter ?? new string[0]).Select(key).ToArray();
            var parts = Regex.Split(pattern, "(\\r\\n|\\r|\\n)");
            var count = (parts.Length + (parts[parts.Length - 1].Length == 0 ? 0 : 1)) / 2;
            return new State { Intent = intent, Prefix = prefix, Ring = new Unit[pattern.Length],
                BeforeAnchors = before, AfterAnchors = after, Exact = Buckets(after.Length), Canonical = Buckets(after.Length),
                CanonicalLines = Enumerable.Range(0, count).Select(i => key(parts[i * 2])).ToArray(),
                CanonicalEndings = Enumerable.Range(0, count).Select(i => i * 2 + 1 < parts.Length ? parts[i * 2 + 1] : "").ToArray() };
        }

        public IReadOnlyList<ResolvedTextEdit> Resolve(TextReader reader, CancellationToken token = default(CancellationToken),
            Func<string, string> normalizeLine = null)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            Func<string, string> key = normalizeLine ?? (s => s);
            var states = _edits.Select(e => CreateState(e, key)).ToArray();
            var lineLimit = states.SelectMany(s => s.BeforeAnchors.Concat(s.AfterAnchors).Concat(s.CanonicalLines))
                .Select(s => s.Length).DefaultIfEmpty().Max() + (normalizeLine == null ? 0 : 65536);
            long offset = 0, number = 1, lineStart = 0;
            if (reader.Peek() == '\uFEFF') { reader.Read(); offset = lineStart = 1; }
            var lineText = new StringBuilder();
            int c, previous = -1;
            while ((c = reader.Read()) >= 0)
            {
                if ((offset & 4095) == 0) token.ThrowIfCancellationRequested();
                var pairedCr = c == '\r' && reader.Peek() == '\n';
                var ending = c == '\n' ? (previous == '\r' ? "\r\n" : "\n") : c == '\r' && !pairedCr ? "\r" : "";
                var unit = new Unit { Offset = offset, Line = number, Boundary = !(previous == '\r' && c == '\n') };
                foreach (var state in states)
                {
                    var pattern = state.Intent.Old;
                    state.Ring[(int)(state.Seen++ % state.Ring.Length)] = unit;
                    while (state.Matched > 0 && pattern[state.Matched] != c) state.Matched = state.Prefix[state.Matched - 1];
                    if (pattern[state.Matched] == c) state.Matched++;
                    if (state.Matched == pattern.Length)
                    {
                        var begin = state.Ring[(int)((state.Seen - pattern.Length) % state.Ring.Length)];
                        if (!Covered(begin.Offset, offset + 1)) state.Unobserved = true;
                        if (begin.Boundary && !pairedCr && state.BeforeIndex == state.BeforeAnchors.Length &&
                            begin.Offset >= state.BeforeEnd && Covered(begin.Offset, offset + 1))
                            Add(state.Exact[0], new Candidate { Offset = begin.Offset, End = offset + 1, Line = begin.Line, Before = pattern });
                        state.Matched = state.Prefix[state.Matched - 1];
                    }
                }
                offset++;
                if (ending.Length > 0)
                {
                    FinishLine(states, new Line { Offset = lineStart, End = offset, Number = number,
                        Text = offset - ending.Length - lineStart <= lineLimit ? lineText.ToString() : null, Ending = ending }, key, normalizeLine != null);
                    number++; lineStart = offset; lineText.Clear();
                }
                else if (c != '\r' && offset - lineStart <= lineLimit) lineText.Append((char)c);
                previous = c;
            }
            if (offset > lineStart)
                FinishLine(states, new Line { Offset = lineStart, End = offset, Number = number,
                    Text = offset - lineStart <= lineLimit ? lineText.ToString() : null, Ending = "" }, key, normalizeLine != null);
            var result = new List<ResolvedTextEdit>();
            for (var i = 0; i < states.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var s = states[i];
                var candidates = s.Exact[s.AfterAnchors.Length];
                if (candidates.Count == 0) candidates = s.Canonical[s.AfterAnchors.Length];
                if (candidates.Count == 0) throw Error((s.Unobserved || _coverage != null && _coverage.Count == 0) ? "observation_required" : "context_not_found", "No edit location in delivered source. Read current source and refine anchors.", i + 1);
                if (candidates.Count > 1) throw Error("ambiguous_context", "Multiple edit locations. Add surrounding complete lines.", i + 1,
                    candidates.Select(m => new PatchCandidate { Line = m.Line, Context = PatchDiagnostic.Limit(m.Before, 160) }).ToArray());
                var match = candidates[0];
                result.Add(new ResolvedTextEdit { Hunk = i + 1, Offset = match.Offset, Before = match.Before, After = s.Intent.New });
            }
            result.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            for (var i = 1; i < result.Count; i++)
                if (result[i].Offset == result[i - 1].Offset || result[i].Offset < result[i - 1].Offset + result[i - 1].Before.Length)
                    throw Error("overlap", "Edits overlap in the original source.", result[i].Hunk);
            return result.AsReadOnly();
        }
        private void FinishLine(State[] states, Line line, Func<string, string> key, bool canonical)
        {
            var lineKey = line.Text == null ? null : key(line.Text);
            // Anchors exclude EOL: a delivered complete logical line is enough.
            var observed = Covered(line.Offset, line.Text == "" ? line.End : line.End - line.Ending.Length);
            foreach (var s in states)
            {
                if (!observed && (s.BeforeAnchors.Contains(lineKey) || s.AfterAnchors.Contains(lineKey))) s.Unobserved = true;
                // For a fixed candidate, the earliest ordered chain dominates every
                // later chain. Keep two distinct ranges per progress bucket to prove
                // ambiguity without enumerating repeated-anchor combinations.
                foreach (var buckets in new[] { s.Exact, s.Canonical })
                    for (var n = s.AfterAnchors.Length - 1; n >= 0; n--)
                    {
                        if (!observed || lineKey != s.AfterAnchors[n]) continue;
                        for (var k = buckets[n].Count - 1; k >= 0; k--)
                        {
                            var candidate = buckets[n][k];
                            if (candidate.End > line.Offset) continue;
                            Add(buckets[n + 1], candidate); buckets[n].RemoveAt(k);
                        }
                    }
                if (canonical && s.CanonicalLines.Length > 0)
                {
                    s.Lines.Enqueue(line);
                    if (s.Lines.Count > s.CanonicalLines.Length) s.Lines.Dequeue();
                    if (s.Lines.Count == s.CanonicalLines.Length && s.BeforeIndex == s.BeforeAnchors.Length &&
                        s.Lines.Peek().Offset >= s.BeforeEnd && s.Canonical[0].Count < 2)
                    {
                        var lines = s.Lines.ToArray();
                        var hasEnding = s.CanonicalEndings[s.CanonicalEndings.Length - 1].Length > 0;
                        var end = hasEnding ? line.End : line.End - line.Ending.Length;
                        if (Covered(lines[0].Offset, end) && lines.Select((l, n) => l.Text != null && key(l.Text) == s.CanonicalLines[n] &&
                            (n == lines.Length - 1 && !hasEnding || l.Ending == s.CanonicalEndings[n])).All(b => b))
                        {
                            var raw = string.Concat(lines.Select((l, n) => l.Text + (n < lines.Length - 1 || hasEnding ? l.Ending : "")));
                            Add(s.Canonical[0], new Candidate { Offset = lines[0].Offset, End = end, Line = lines[0].Number, Before = raw });
                        }
                    }
                }
                if (observed && s.BeforeIndex < s.BeforeAnchors.Length && lineKey == s.BeforeAnchors[s.BeforeIndex])
                { s.BeforeIndex++; s.BeforeEnd = line.End; }
            }
        }
        // Read actual changed fragments from the retained verified after-source. This
        // bounded display helper never substitutes intended bytes for missing evidence.
        public static IReadOnlyList<ResolvedTextEdit> ReadAfterFragments(TextReader reader, IReadOnlyList<ResolvedTextEdit> edits)
        {
            var actual = new List<ResolvedTextEdit>();
            var buffer = new char[8192];
            long position = 0, delta = 0;
            foreach (var edit in edits)
            {
                var start = edit.Offset + delta;
                while (position < start)
                {
                    var count = reader.Read(buffer, 0, (int)Math.Min(buffer.Length, start - position));
                    if (count == 0) throw new InvalidDataException("Retained after-source is incomplete.");
                    position += count;
                }
                var text = new StringBuilder();
                while (text.Length < edit.After.Length)
                {
                    var count = reader.Read(buffer, 0, Math.Min(buffer.Length, edit.After.Length - text.Length));
                    if (count == 0) throw new InvalidDataException("Retained changed fragment is incomplete.");
                    text.Append(buffer, 0, count); position += count;
                }
                var after = text.ToString();
                if (after != edit.After) throw new InvalidDataException("Retained changed fragment differs from the verified patch.");
                actual.Add(new ResolvedTextEdit { Hunk = edit.Hunk, Offset = edit.Offset, Before = edit.Before, After = after });
                delta += (long)edit.After.Length - edit.Before.Length;
            }
            return actual.AsReadOnly();
        }

        private static TextPatternException Error(string code, string message, int? hunk = null, IReadOnlyList<PatchCandidate> candidates = null)
        { return new TextPatternException(code, message, hunk, candidates); }
    }
}
