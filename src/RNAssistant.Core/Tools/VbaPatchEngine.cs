using System;

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

        internal VbaPatchResult(VbaPatchStatus status, string text, string find, int count)
        {
            Status = status;
            Text = text;
            NormalizedFind = find;
            MatchCount = count;
        }
    }

    // One exact text replacement. JSON/tool policy, ordered dispatch and persistence
    // remain with the caller. No ToolResult, Office, resource or journal dependency.
    public static class VbaPatchEngine
    {
        public static VbaPatchResult Replace(string source, string find, string replacement)
        {
            source = source ?? string.Empty;
            find = VbaTextCanonicalizer.MatchLineEndings(find, source);
            replacement = VbaTextCanonicalizer.MatchLineEndings(replacement ?? string.Empty, source);
            if (string.IsNullOrEmpty(find))
                return new VbaPatchResult(VbaPatchStatus.EmptyFind, source, find, 0);
            var count = CountOccurrences(source, find);
            if (count == 0) return new VbaPatchResult(VbaPatchStatus.NotFound, source, find, count);
            if (count != 1) return new VbaPatchResult(VbaPatchStatus.Ambiguous, source, find, count);
            var index = source.IndexOf(find, StringComparison.Ordinal);
            var updated = source.Substring(0, index) + replacement + source.Substring(index + find.Length);
            return new VbaPatchResult(string.Equals(updated, source, StringComparison.Ordinal)
                ? VbaPatchStatus.Unchanged : VbaPatchStatus.Changed, updated, find, count);
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
    }
}
