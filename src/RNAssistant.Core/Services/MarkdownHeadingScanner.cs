using System;
using System.Collections.Generic;

namespace RNAssistant.Core.Services
{
    public sealed class MarkdownSectionException : InvalidOperationException
    {
        public string Code { get; private set; }
        public MarkdownSectionException(string message, string code) : base(message) { Code = code; }
    }

    // The same bounded ATX grammar drives discovery labels and section selection.
    internal static class MarkdownHeadingScanner
    {
        internal sealed class Heading
        {
            internal int Start, ContentStart, LineEnd, Level;
        }

        internal static IEnumerable<Heading> Scan(string text)
        {
            char fence = '\0'; int fenceLength = 0;
            for (var start = 0; start < text.Length;)
            {
                var end = text.IndexOf('\n', start); if (end < 0) end = text.Length;
                var position = start;
                while (position < end && position - start < 4 && text[position] == ' ') position++;
                if (position - start < 4 && position < end)
                {
                    var marker = text[position]; var after = position;
                    while (after < end && text[after] == marker) after++;
                    var count = after - position;
                    if ((marker == '`' || marker == '~') && count >= 3)
                    {
                        if (fence == '\0') { fence = marker; fenceLength = count; }
                        else if (fence == marker && count >= fenceLength && string.IsNullOrWhiteSpace(text.Substring(after, end - after))) fence = '\0';
                    }
                    else if (fence == '\0' && marker == '#' && count <= 6 && (after == end || char.IsWhiteSpace(text[after])))
                        yield return new Heading { Start = start, ContentStart = after, LineEnd = end, Level = count };
                }
                start = end + 1;
            }
        }
    }
}
