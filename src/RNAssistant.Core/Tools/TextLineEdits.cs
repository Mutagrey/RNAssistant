using System;
using System.Collections.Generic;
using System.Linq;

namespace RNAssistant.Core.Tools
{
    public sealed class TextLineEdit
    {
        public int StartLine { get; set; }
        public int RemoveCount { get; set; }
        public string[] Insert { get; set; }
    }
    // Physical line operations for owners such as VBIDE. No language-specific parsing.
    public static class TextLineEdits
    {
        public static IReadOnlyList<TextLineEdit> Plan(string before, string after)
        {
            var a = Lines(before); var b = Lines(after); var edits = new List<TextLineEdit>();
            Diff(a, 0, a.Length, b, 0, b.Length, edits);
            return edits.OrderByDescending(edit => edit.StartLine).ToArray();
        }
        private static string[] Lines(string text)
        { return text.Length == 0 ? new string[0] : text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'); }
        private static void Diff(string[] a, int startA, int endA, string[] b, int startB, int endB, List<TextLineEdit> edits)
        {
            while (startA < endA && startB < endB && a[startA] == b[startB]) { startA++; startB++; }
            while (startA < endA && startB < endB && a[endA - 1] == b[endB - 1]) { endA--; endB--; }
            if (startA == endA && startB == endB) return;
            var n = endA - startA; var m = endB - startB;
            // Bounded exact LCS; preserve common boundaries even for a huge whole-source rewrite.
            if ((long)n * m > 1000000 || n == 0 || m == 0)
            { edits.Add(new TextLineEdit { StartLine = startA + 1, RemoveCount = n, Insert = b.Skip(startB).Take(m).ToArray() }); return; }
            var lengths = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
                for (var j = m - 1; j >= 0; j--)
                    lengths[i, j] = a[startA + i] == b[startB + j] ? lengths[i + 1, j + 1] + 1 : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            int x = 0, y = 0, fromA = 0, fromB = 0;
            while (x < n || y < m)
            {
                if (x < n && y < m && a[startA + x] == b[startB + y])
                {
                    Add(edits, startA, b, startB, fromA, x, fromB, y);
                    x++; y++; fromA = x; fromB = y;
                }
                else if (y < m && (x == n || lengths[x, y + 1] >= lengths[x + 1, y])) y++;
                else x++;
            }
            Add(edits, startA, b, startB, fromA, x, fromB, y);
        }
        private static void Add(List<TextLineEdit> edits, int startA, string[] b, int startB, int fromA, int toA, int fromB, int toB)
        {
            if (fromA == toA && fromB == toB) return;
            edits.Add(new TextLineEdit { StartLine = startA + fromA + 1, RemoveCount = toA - fromA,
                Insert = b.Skip(startB + fromB).Take(toB - fromB).ToArray() });
        }
    }
}
