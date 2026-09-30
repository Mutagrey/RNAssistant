using System;
using System.Collections.Generic;

namespace RNAssistant.Core.Tools
{
    public enum VbaPatchStatus
    {
        EmptyFind,
        InvalidLocation,
        NotFound,
        Ambiguous,
        Unchanged,
        Changed
    }

    public sealed class VbaPatchLocation
    {
        public int StartLine { get; private set; }
        public int StartColumn { get; private set; }
        internal VbaPatchLocation(int line, int column) { StartLine = line; StartColumn = column; }
    }

    public sealed class VbaPatchResult
    {
        public VbaPatchStatus Status { get; private set; }
        public string Text { get; private set; }
        public string NormalizedFind { get; private set; }
        public int MatchCount { get; private set; }
        public int FindMatchCount { get; private set; }
        public bool FormatNormalizedMatch { get; private set; }
        public IReadOnlyList<VbaPatchLocation> Locations { get; private set; }
        public bool LocationsComplete { get { return Locations.Count == MatchCount; } }

        internal VbaPatchResult(VbaPatchStatus status, string text, string find, int count, int findCount,
            bool formatNormalizedMatch = false, IReadOnlyList<VbaPatchLocation> locations = null)
        {
            Status = status;
            Text = text;
            NormalizedFind = find;
            MatchCount = count;
            FindMatchCount = findCount;
            FormatNormalizedMatch = formatNormalizedMatch;
            Locations = locations ?? new VbaPatchLocation[0];
        }
    }

    // One exact text replacement. JSON/tool policy, ordered dispatch and persistence
    // remain with the caller. No ToolResult, Office, resource or journal dependency.
    public static class VbaPatchEngine
    {
        public static VbaPatchResult Replace(string source, string find, string replacement)
        {
            return Replace(source, find, replacement, null, null);
        }

        public static VbaPatchResult Replace(
            string source,
            string find,
            string replacement,
            string contextBefore,
            string contextAfter,
            int? startLine = null,
            int? startColumn = null)
        {
            source = source ?? string.Empty;
            var rawFind = find;
            var rawBefore = contextBefore ?? string.Empty;
            var rawAfter = contextAfter ?? string.Empty;
            var copiedBlock = rawBefore + rawFind + rawAfter;
            var copiedBlockExists = !string.IsNullOrEmpty(rawFind) &&
                source.IndexOf(copiedBlock, StringComparison.Ordinal) >= 0;
            find = copiedBlockExists ? rawFind : VbaTextCanonicalizer.MatchLineEndings(find, source);
            replacement = VbaTextCanonicalizer.MatchLineEndings(replacement ?? string.Empty, source);
            contextBefore = copiedBlockExists ? rawBefore : VbaTextCanonicalizer.MatchLineEndings(rawBefore, source);
            contextAfter = copiedBlockExists ? rawAfter : VbaTextCanonicalizer.MatchLineEndings(rawAfter, source);
            if (string.IsNullOrEmpty(find))
                return new VbaPatchResult(VbaPatchStatus.EmptyFind, source, find, 0, 0);
            var exactBlock = contextBefore + find + contextAfter;
            var positions = new List<int>();
            var count = CountOccurrences(source, exactBlock, positions);
            var locations = new List<VbaPatchLocation>();
            foreach (var position in positions) locations.Add(LocationAt(source, position + contextBefore.Length));
            var findCount = CountOccurrences(source, find);
            if (startLine.HasValue || startColumn.HasValue)
            {
                int position;
                if (!startLine.HasValue || !TryPosition(source, startLine.Value, startColumn ?? 1, out position))
                    return new VbaPatchResult(VbaPatchStatus.InvalidLocation, source, find, count, findCount, locations: locations);
                var blockStart = position - contextBefore.Length;
                if (blockStart < 0 || exactBlock.Length > source.Length - blockStart ||
                    string.CompareOrdinal(source, blockStart, exactBlock, 0, exactBlock.Length) != 0)
                    return new VbaPatchResult(VbaPatchStatus.NotFound, source, find, count, findCount, locations: locations);
                var pointed = source.Substring(0, position) + replacement + source.Substring(position + find.Length);
                return new VbaPatchResult(pointed == source ? VbaPatchStatus.Unchanged : VbaPatchStatus.Changed,
                    pointed, find, 1, findCount, locations: new[] { LocationAt(source, position) });
            }
            if (count == 0)
            {
                // VBE can normalize spaces and identifier casing after a write.
                // Accept one equivalent sequence of complete logical lines;
                // inline boundaries remain exact because their offsets cannot be
                // recovered safely after VBE changes spacing.
                if (CanMatchCompleteLines(find, contextBefore, contextAfter))
                {
                    var normalized = ReplaceUniqueNormalizedLines(source, find, replacement,
                        contextBefore, contextAfter, findCount);
                    if (normalized != null) return normalized;
                }
                return new VbaPatchResult(VbaPatchStatus.NotFound, source, find, count, findCount);
            }
            if (count != 1) return new VbaPatchResult(VbaPatchStatus.Ambiguous, source, find, count, findCount, locations: locations);
            var blockIndex = source.IndexOf(exactBlock, StringComparison.Ordinal);
            var index = blockIndex + contextBefore.Length;
            var updated = source.Substring(0, index) + replacement + source.Substring(index + find.Length);
            return new VbaPatchResult(string.Equals(updated, source, StringComparison.Ordinal)
                ? VbaPatchStatus.Unchanged : VbaPatchStatus.Changed, updated, find, count, findCount, locations: locations);
        }

        private static int CountOccurrences(string value, string find, List<int> positions = null)
        {
            var count = 0;
            var index = 0;
            while ((index = value.IndexOf(find, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                // Location previews are bounded diagnostics; the full match count
                // and LocationsComplete explicitly describe omitted candidates.
                if (positions != null && positions.Count < 20) positions.Add(index);
                // Distinct start offsets are ambiguous even when matches overlap.
                index++;
            }
            return count;
        }

        private static VbaPatchLocation LocationAt(string source, int position)
        {
            var line = 1;
            var lineStart = 0;
            for (var index = 0; index < position; index++)
            {
                if (!IsLineTerminator(source[index])) continue;
                if (source[index] == '\r' && index + 1 < position && source[index + 1] == '\n') index++;
                line++;
                lineStart = index + 1;
            }
            return new VbaPatchLocation(line, position - lineStart + 1);
        }

        private static bool TryPosition(string source, int line, int column, out int position)
        {
            position = 0;
            if (line < 1 || column < 1) return false;
            var lines = Lines(source);
            if (line > lines.Count) return false;
            var selected = lines[line - 1];
            if (column > selected.End - selected.Start + 1) return false;
            position = selected.Start + column - 1;
            return true;
        }

        private static bool CanMatchCompleteLines(string find, string contextBefore, string contextAfter)
        {
            if (contextBefore.Length > 0 && !IsLineTerminator(contextBefore[contextBefore.Length - 1]))
                return false;
            return contextAfter.Length == 0 || IsLineTerminator(find[find.Length - 1]) ||
                IsLineTerminator(contextAfter[0]);
        }

        private static bool IsLineTerminator(char value)
        { return value == '\n' || value == '\r'; }

        private static VbaPatchResult ReplaceUniqueNormalizedLines(string source, string find, string replacement,
            string contextBefore, string contextAfter, int findCount)
        {
            var expectedBlock = contextBefore + find + contextAfter;
            var expected = Lines(expectedBlock);
            if (expected.Count == 0) return null;
            var actual = Lines(source);
            if (actual.Count < expected.Count) return null;
            var beforeLineCount = Lines(contextBefore).Count;
            var findLineCount = Lines(find).Count;
            var normalizedExpected = new string[expected.Count];
            for (var index = 0; index < expected.Count; index++)
                normalizedExpected[index] = VbaTextCanonicalizer.NormalizeVbeComparableCode(
                    expectedBlock.Substring(expected[index].Start, expected[index].End - expected[index].Start));
            if (Array.TrueForAll(normalizedExpected, string.IsNullOrEmpty)) return null;
            var normalizedActual = new string[actual.Count];
            for (var index = 0; index < actual.Count; index++)
                normalizedActual[index] = VbaTextCanonicalizer.NormalizeVbeComparableCode(
                    source.Substring(actual[index].Start, actual[index].End - actual[index].Start));
            var needsFinalNewline = expected[expected.Count - 1].Terminated;
            var matches = 0;
            var locations = new List<VbaPatchLocation>();
            var matchedFindStart = 0;
            var matchedFindEnd = 0;
            for (var start = 0; start <= actual.Count - expected.Count; start++)
            {
                var last = actual[start + expected.Count - 1];
                if (needsFinalNewline && !last.Terminated) continue;
                var equal = true;
                for (var offset = 0; offset < expected.Count; offset++)
                {
                    if (string.Equals(normalizedActual[start + offset], normalizedExpected[offset],
                        StringComparison.Ordinal)) continue;
                    equal = false;
                    break;
                }
                if (!equal) continue;
                matches++;
                var firstFindLine = actual[start + beforeLineCount];
                var lastFindLine = actual[start + beforeLineCount + findLineCount - 1];
                matchedFindStart = firstFindLine.Start;
                if (locations.Count < 20) locations.Add(new VbaPatchLocation(start + beforeLineCount + 1, 1));
                matchedFindEnd = IsLineTerminator(find[find.Length - 1])
                    ? lastFindLine.TerminatorEnd : lastFindLine.End;
            }
            if (matches == 0) return null;
            if (matches != 1)
                return new VbaPatchResult(VbaPatchStatus.Ambiguous, source, find, matches, findCount, true, locations);
            var updated = source.Substring(0, matchedFindStart) + replacement + source.Substring(matchedFindEnd);
            return new VbaPatchResult(string.Equals(updated, source, StringComparison.Ordinal)
                ? VbaPatchStatus.Unchanged : VbaPatchStatus.Changed, updated, find, 1, findCount, true, locations);
        }

        private static List<LineSpan> Lines(string text)
        {
            var result = new List<LineSpan>();
            var start = 0;
            for (var index = 0; index < text.Length; index++)
            {
                if (text[index] != '\n' && text[index] != '\r') continue;
                var end = index;
                if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                result.Add(new LineSpan(start, end, index + 1));
                start = index + 1;
            }
            if (start < text.Length) result.Add(new LineSpan(start, text.Length, text.Length));
            return result;
        }

        private sealed class LineSpan
        {
            internal readonly int Start;
            internal readonly int End;
            internal readonly int TerminatorEnd;
            internal bool Terminated { get { return TerminatorEnd > End; } }

            internal LineSpan(int start, int end, int terminatorEnd)
            { Start = start; End = end; TerminatorEnd = terminatorEnd; }
        }
    }
}
