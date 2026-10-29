using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace RNAssistant.Core.Tools
{
    public sealed class TextPatchEdit
    {
        public long Offset { get; internal set; }
        public int Hunk { get; internal set; }
        public string Before { get; internal set; }
        public string After { get; internal set; }
    }

    // A patch body for one source. No paths, language scopes or model-owned revisions.
    // Matching is streamed and all hunks address the same original snapshot.
    public sealed class TextPatch
    {
        private sealed class Hunk
        {
            internal readonly List<string> Anchors = new List<string>();
            internal readonly List<string> Lines = new List<string>();
            internal bool EndOfFile;
        }
        private sealed class SourceLine
        {
            internal long Offset, Length, Number;
            internal string Text, Key, Ending;
            internal long End => Offset + Length + Ending.Length;
            internal string Raw => Text + Ending;
        }
        private sealed class Match
        {
            internal Hunk Hunk;
            internal string[] Anchors, Old;
            internal int[] Prefix;
            internal int AnchorIndex, Count;
            internal long AnchorEnd;
            internal readonly List<SourceLine> Pending = new List<SourceLine>();
            internal readonly List<PatchCandidate> Locations = new List<PatchCandidate>();
            internal SourceLine[] Candidate;
        }
        private readonly List<Hunk> _hunks;
        private TextPatch(List<Hunk> hunks) { _hunks = hunks; }
        public bool RequiresWholeSource => _hunks.Any(h => h.Lines.All(l => l[0] == '+'));

        public static TextPatch Parse(string patch)
        {
            if (string.IsNullOrWhiteSpace(patch)) throw Error("invalid_patch", "patch must contain a diff body starting with @@.");
            var lines = patch.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var hunks = new List<Hunk>();
            Hunk current = null;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line == "@@" || line.StartsWith("@@ ", StringComparison.Ordinal))
                {
                    if (current == null || current.Lines.Count > 0)
                    {
                        current = new Hunk(); hunks.Add(current);
                        if (hunks.Count > 100) throw Error("invalid_patch", "Use at most 100 hunks.");
                    }
                    if (line.Length > 3)
                    {
                        current.Anchors.Add(line.Substring(3));
                        if (current.Anchors.Count > 100) throw Error("invalid_patch", "Too many anchors in one hunk.");
                    }
                    continue;
                }
                if (line.Length == 0)
                {
                    // Blank separators are allowed only between hunks or at the end.
                    var next = i + 1;
                    while (next < lines.Length && lines[next].Length == 0) next++;
                    if (next == lines.Length || lines[next] == "@@" || lines[next].StartsWith("@@ ", StringComparison.Ordinal)) continue;
                    throw Error("invalid_patch", "Prefix an unchanged empty line with a space.", current == null ? (int?)null : hunks.Count);
                }
                if (current == null) throw Error("invalid_patch", "Use @@, followed by context, -old and +new lines; omit file headers.");
                if (line == "*** End of File" && current.Lines.Count > 0 && !current.EndOfFile)
                { current.EndOfFile = true; continue; }
                if (current.EndOfFile || (line[0] != ' ' && line[0] != '+' && line[0] != '-'))
                    throw Error("invalid_patch", "Invalid patch line " + (i + 1) + "; use a space, + or - prefix.", hunks.Count);
                current.Lines.Add(line);
            }
            if (hunks.Count == 0) throw Error("invalid_patch", "Each hunk must contain an addition or deletion.");
            for (var i = 0; i < hunks.Count; i++)
                if (!hunks[i].Lines.Any(l => l[0] == '+' || l[0] == '-'))
                    throw Error("invalid_patch", "Each hunk must contain an addition or deletion.", i + 1);
            return new TextPatch(hunks);
        }

        // The owner may supply a documented line equivalence (live VBA only).
        // Text that does not participate in a match is never retained in memory.
        public IReadOnlyList<TextPatchEdit> Resolve(TextReader reader, CancellationToken token = default(CancellationToken),
            Func<string, string> normalizeLine = null)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            Func<string, string> key = normalizeLine ?? (s => s);
            var states = _hunks.Select(h => CreateMatch(h, key)).ToArray();
            var keys = new HashSet<string>(states.SelectMany(s => s.Old.Concat(s.Anchors)), StringComparer.Ordinal);
            var maxLength = _hunks.SelectMany(h => h.Anchors.Concat(h.Lines.Where(l => l[0] != '+').Select(l => l.Substring(1))))
                .Select(l => l.Length).DefaultIfEmpty(0).Max();
            // Nonmatching very long lines are counted and skipped, not buffered.
            var limit = (long)maxLength + (normalizeLine == null ? 0 : 65536);
            long offset = 0, number = 0;
            if (reader.Peek() == '\uFEFF') { reader.Read(); offset++; }
            SourceLine last = null;
            var newline = "\n";
            var sawNewline = false;
            SourceLine line;
            while ((line = ReadLine(reader, offset, ++number, limit, token)) != null)
            {
                token.ThrowIfCancellationRequested();
                offset = line.End;
                if (!sawNewline && line.Ending.Length > 0) { newline = line.Ending; sawNewline = true; }
                line.Key = line.Text == null ? null : key(line.Text);
                if (line.Key == null || !keys.Contains(line.Key)) { line.Key = null; line.Text = null; }
                foreach (var state in states)
                {
                    if (state.Hunk.EndOfFile) { state.Count = 0; state.Candidate = null; state.Locations.Clear(); }
                    if (state.AnchorIndex == state.Anchors.Length && line.Number > state.AnchorEnd && state.Old.Length > 0)
                    {
                        while (state.Pending.Count > 0 && line.Key != state.Old[state.Pending.Count])
                        {
                            var keep = state.Prefix[state.Pending.Count - 1];
                            state.Pending.RemoveRange(0, state.Pending.Count - keep);
                        }
                        if (line.Key == state.Old[state.Pending.Count]) state.Pending.Add(line);
                        if (state.Pending.Count == state.Old.Length)
                        {
                            if (state.Count == 0) state.Candidate = state.Pending.ToArray();
                            if (state.Count < int.MaxValue) state.Count++;
                            if (state.Locations.Count < 5) state.Locations.Add(new PatchCandidate { Line = state.Pending[0].Number, Context = PatchDiagnostic.Limit(state.Pending[0].Text, 160) });
                            var keep = state.Prefix[state.Pending.Count - 1];
                            state.Pending.RemoveRange(0, state.Pending.Count - keep);
                        }
                    }
                    // Anchors are ordered complete source lines, strictly before the block.
                    if (state.AnchorIndex < state.Anchors.Length && line.Key == state.Anchors[state.AnchorIndex])
                    { state.AnchorIndex++; state.AnchorEnd = line.Number; }
                }
                last = line;
            }
            var edits = new List<TextPatchEdit>();
            for (var i = 0; i < states.Length; i++)
            {
                var state = states[i];
                if (state.Old.Length == 0 && state.AnchorIndex == state.Anchors.Length && (last == null || state.Hunk.EndOfFile))
                { state.Count = 1; state.Candidate = new SourceLine[0]; }
                if (state.Old.Length == 0 && last != null && !state.Hunk.EndOfFile &&
                    state.AnchorIndex == state.Anchors.Length)
                    throw Error("invalid_patch", "Hunk " + (i + 1) +
                        ": an @@ anchor alone does not locate an insertion. Add *** End of File to append, or include an unchanged context line at the insertion point.", i + 1);
                if (state.Count == 0) throw Error("context_not_found", "Hunk " + (i + 1) + ": anchors or old/context lines were not found. Read the current source. A context-free insertion needs an empty source or *** End of File.", i + 1);
                if (state.Count != 1) throw Error("ambiguous_context", "Hunk " + (i + 1) + ": multiple matches at original lines " + string.Join(", ", state.Locations.Select(c => c.Line)) + ". Add @@ anchors or unchanged context.", i + 1, state.Locations);
                AddEdits(edits, state, offset, last, newline, i + 1);
            }
            edits.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            for (var i = 1; i < edits.Count; i++)
                if (edits[i].Offset == edits[i - 1].Offset || edits[i].Offset < edits[i - 1].Offset + edits[i - 1].Before.Length)
                    throw Error("overlap", "Hunks overlap in the original source.", edits[i].Hunk);
            return edits.AsReadOnly();
        }

        // Read actual changed fragments from the retained verified after-source. This
        // bounded display helper never substitutes intended bytes for missing evidence.
        public static IReadOnlyList<TextPatchEdit> ReadAfterFragments(TextReader reader, IReadOnlyList<TextPatchEdit> edits)
        {
            var actual = new List<TextPatchEdit>();
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
                actual.Add(new TextPatchEdit { Hunk = edit.Hunk, Offset = edit.Offset, Before = edit.Before, After = after });
                delta += (long)edit.After.Length - edit.Before.Length;
            }
            return actual.AsReadOnly();
        }

        private static Match CreateMatch(Hunk hunk, Func<string, string> key)
        {
            var old = hunk.Lines.Where(l => l[0] != '+').Select(l => key(l.Substring(1))).ToArray();
            var prefix = new int[old.Length];
            for (var i = 1; i < old.Length; i++)
            {
                var j = prefix[i - 1];
                while (j > 0 && old[i] != old[j]) j = prefix[j - 1];
                if (old[i] == old[j]) j++;
                prefix[i] = j;
            }
            return new Match { Hunk = hunk, Anchors = hunk.Anchors.Select(key).ToArray(), Old = old, Prefix = prefix };
        }

        private static SourceLine ReadLine(TextReader reader, long offset, long number, long limit, CancellationToken token)
        {
            if (reader.Peek() < 0) return null;
            var text = new StringBuilder();
            long length = 0;
            string ending = "";
            int c;
            while ((c = reader.Read()) >= 0)
            {
                if ((length & 4095) == 0) token.ThrowIfCancellationRequested();
                if (c == '\r' || c == '\n')
                {
                    ending = ((char)c).ToString();
                    if (c == '\r' && reader.Peek() == '\n') { reader.Read(); ending = "\r\n"; }
                    break;
                }
                if (++length <= limit) text.Append((char)c);
            }
            return new SourceLine { Offset = offset, Number = number, Length = length,
                Text = length <= limit ? text.ToString() : null, Ending = ending };
        }

        private static void AddEdits(List<TextPatchEdit> edits, Match match, long end, SourceLine last, string defaultNewline, int hunk)
        {
            var source = match.Candidate;
            var oldIndex = 0;
            for (var i = 0; i < match.Hunk.Lines.Count;)
            {
                if (match.Hunk.Lines[i][0] == ' ') { oldIndex++; i++; continue; }
                var offset = oldIndex < source.Length ? source[oldIndex].Offset : source.Length > 0 ? source[source.Length - 1].End : end;
                var newline = oldIndex < source.Length && source[oldIndex].Ending.Length > 0 ? source[oldIndex].Ending :
                    oldIndex > 0 && source[oldIndex - 1].Ending.Length > 0 ? source[oldIndex - 1].Ending : defaultNewline;
                var before = new StringBuilder();
                var added = new List<string>();
                while (i < match.Hunk.Lines.Count && match.Hunk.Lines[i][0] != ' ')
                {
                    var change = match.Hunk.Lines[i++];
                    if (change[0] == '-') before.Append(source[oldIndex++].Raw);
                    else added.Add(change.Substring(1));
                }
                var after = string.Join(newline, added);
                if (added.Count > 0)
                {
                    if (offset == end && before.Length == 0 && last != null && last.Ending.Length == 0) after = newline + after;
                    if (offset + before.Length < end || last == null || last.Ending.Length > 0) after += newline;
                }
                edits.Add(new TextPatchEdit { Hunk = hunk, Offset = offset, Before = before.ToString(), After = after });
            }
        }
        private static TextPatternException Error(string code, string message, int? hunk = null, IReadOnlyList<PatchCandidate> candidates = null) { return new TextPatternException(code, message, hunk, candidates); }
    }
}
