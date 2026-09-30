using System;
using System.Collections.Generic;

namespace RNAssistant.Core.Tools
{
    public enum VbaPatchStatus
    {
        EmptyFind,
        NotFound,
        Ambiguous,
        Unchanged,
        Changed
    }

    public sealed class VbaPatchResult
    {
        public VbaPatchStatus Status { get; private set; }
        public string Text { get; private set; }
        public string NormalizedFind { get; private set; }
        public int MatchCount { get; private set; }
        public int FindMatchCount { get; private set; }
        public bool FormatNormalizedMatch { get; private set; }

        internal VbaPatchResult(VbaPatchStatus status, string text, string find, int count, int findCount,
            bool formatNormalizedMatch = false)
        {
            Status = status;
            Text = text;
            NormalizedFind = find;
            MatchCount = count;
            FindMatchCount = findCount;
            FormatNormalizedMatch = formatNormalizedMatch;
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
            string contextAfter)
        {
            source = source ?? string.Empty;
            find = VbaTextCanonicalizer.MatchLineEndings(find, source);
            replacement = VbaTextCanonicalizer.MatchLineEndings(replacement ?? string.Empty, source);
            contextBefore = VbaTextCanonicalizer.MatchLineEndings(
                contextBefore ?? string.Empty, source);
            contextAfter = VbaTextCanonicalizer.MatchLineEndings(
                contextAfter ?? string.Empty, source);
            if (string.IsNullOrEmpty(find))
                return new VbaPatchResult(VbaPatchStatus.EmptyFind, source, find, 0, 0);
            var exactBlock = contextBefore + find + contextAfter;
            var count = CountOccurrences(source, exactBlock);
            var findCount = CountOccurrences(source, find);
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
            if (count != 1) return new VbaPatchResult(VbaPatchStatus.Ambiguous, source, find, count, findCount);
            var blockIndex = source.IndexOf(exactBlock, StringComparison.Ordinal);
            var index = blockIndex + contextBefore.Length;
            var updated = source.Substring(0, index) + replacement + source.Substring(index + find.Length);
            return new VbaPatchResult(string.Equals(updated, source, StringComparison.Ordinal)
                ? VbaPatchStatus.Unchanged : VbaPatchStatus.Changed, updated, find, count, findCount);
        }

        private static int CountOccurrences(string value, string find)
        {
            var count = 0;
            var index = 0;
            while ((index = value.IndexOf(find, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                // Distinct start offsets are ambiguous even when matches overlap.
                index++;
            }
            return count;
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
                matchedFindEnd = IsLineTerminator(find[find.Length - 1])
                    ? lastFindLine.TerminatorEnd : lastFindLine.End;
            }
            if (matches == 0) return null;
            if (matches != 1)
                return new VbaPatchResult(VbaPatchStatus.Ambiguous, source, find, matches, findCount, true);
            var updated = source.Substring(0, matchedFindStart) + replacement + source.Substring(matchedFindEnd);
            return new VbaPatchResult(string.Equals(updated, source, StringComparison.Ordinal)
                ? VbaPatchStatus.Unchanged : VbaPatchStatus.Changed, updated, find, 1, findCount, true);
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
