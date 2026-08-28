using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RNAssistant.Core.Tools
{
    // Text representations only. These hashes are not the SHA-256 of raw CAS bytes.
    public static class VbaTextCanonicalizer
    {
        public static string PackageCodeSha256(string code)
        {
            var normalized = NormalizePackageCode(code);
            return TextPatternEngine.Sha256(normalized);
        }

        public static string LiveCodeSha256(string code)
        {
            return TextPatternEngine.Sha256(NormalizeLiveCode(code));
        }

        public static string VbeComparableCodeSha256(string code)
        {
            return TextPatternEngine.Sha256(NormalizeVbeComparableCode(code));
        }

        public static string PackageComparableCodeSha256(string code)
        {
            return VbeComparableCodeSha256(NormalizePackageCode(code));
        }

        public static string NormalizeLiveCode(string code)
        {
            var normalized = (code ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            // A final line terminator is a transport detail of CodeModule.Lines/InsertLines.
            // Preserve all other leading/trailing whitespace and blank lines for exact edit hashes.
            return normalized.EndsWith("\n", StringComparison.Ordinal)
                ? normalized.Substring(0, normalized.Length - 1)
                : normalized;
        }

        public static int LiveCodeLineCount(string code)
        {
            var normalized = NormalizeLiveCode(code);
            if (normalized.Length == 0) return 0;
            var count = 1;
            for (var index = 0; index < normalized.Length; index++)
            {
                if (normalized[index] == '\n') count++;
            }
            return count;
        }

        public static string NormalizeVbeComparableCode(string code)
        {
            var lines = (code ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[lines.Count - 1]))
            {
                lines.RemoveAt(lines.Count - 1);
            }

            return string.Join("\n", lines.Select(NormalizeVbeComparableLine).ToArray());
        }

        private static string NormalizeVbeComparableLine(string line)
        {
            var output = new StringBuilder();
            line = line ?? string.Empty;
            var index = 0;
            while (index < line.Length)
            {
                var value = line[index];
                if (char.IsWhiteSpace(value))
                {
                    index++;
                    continue;
                }
                if (value == '\'')
                {
                    AppendVbeToken(output, line.Substring(index).TrimEnd());
                    break;
                }
                if (value == '"' || value == '[')
                {
                    var start = index++;
                    var terminator = value == '"' ? '"' : ']';
                    while (index < line.Length)
                    {
                        if (line[index] != terminator)
                        {
                            index++;
                            continue;
                        }
                        if (terminator == '"' && index + 1 < line.Length && line[index + 1] == '"')
                        {
                            index += 2;
                            continue;
                        }
                        index++;
                        break;
                    }
                    AppendVbeToken(output, line.Substring(start, index - start));
                    continue;
                }
                if (char.IsLetterOrDigit(value) || value == '_')
                {
                    var start = index++;
                    while (index < line.Length && (char.IsLetterOrDigit(line[index]) || line[index] == '_')) index++;
                    if (index < line.Length && "%&@!#$".IndexOf(line[index]) >= 0) index++;
                    AppendVbeToken(output, line.Substring(start, index - start).ToLowerInvariant());
                    continue;
                }

                var token = value.ToString();
                if (index + 1 < line.Length && IsVbaCompoundOperator(value, line[index + 1]))
                {
                    token += line[++index];
                }
                AppendVbeToken(output, token.ToLowerInvariant());
                index++;
            }
            return output.ToString();
        }

        private static bool IsVbaCompoundOperator(char first, char second)
        {
            return first == '<' && (second == '=' || second == '>') ||
                   first == '>' && second == '=' ||
                   first == ':' && second == '=' ||
                   (first == '+' || first == '-' || first == '*' || first == '/' || first == '\\' || first == '^' || first == '&') && second == '=';
        }

        private static void AppendVbeToken(StringBuilder output, string token)
        {
            output.Append((token ?? string.Empty).Length).Append(':').Append(token).Append(';');
        }

        public static string NormalizePackageCode(string code)
        {
            var lines = (code ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
                .Where(line => !line.TrimStart().StartsWith("' RNAssistantPackage:", StringComparison.OrdinalIgnoreCase) &&
                               !line.TrimStart().StartsWith("' RNAssistantSession:", StringComparison.OrdinalIgnoreCase))
                .ToList();
            StripExportHeader(lines);
            return string.Join("\n", lines.ToArray()).Trim();
        }

        private static void StripExportHeader(IList<string> lines)
        {
            if (lines == null || lines.Count == 0) return;
            var index = 0;
            while (index < lines.Count && string.IsNullOrWhiteSpace(lines[index])) index++;
            if (index >= lines.Count) return;

            if (lines[index].TrimStart().StartsWith("VERSION ", StringComparison.OrdinalIgnoreCase))
            {
                var sawAttribute = false;
                while (index < lines.Count)
                {
                    var value = lines[index].TrimStart();
                    if (value.StartsWith("Attribute ", StringComparison.OrdinalIgnoreCase)) sawAttribute = true;
                    else if (sawAttribute && !string.IsNullOrWhiteSpace(value)) break;
                    index++;
                }
                if (!sawAttribute) return;
                for (var remove = 0; remove < index; remove++) lines.RemoveAt(0);
                return;
            }

            var first = index;
            while (index < lines.Count && lines[index].TrimStart().StartsWith("Attribute ", StringComparison.OrdinalIgnoreCase)) index++;
            if (index == first) return;
            while (index < lines.Count && string.IsNullOrWhiteSpace(lines[index])) index++;
            for (var remove = 0; remove < index; remove++) lines.RemoveAt(0);
        }

        // Normalize only actual newline characters, never literal backslash escapes.
        public static string MatchLineEndings(string value, string current)
        {
            if (value == null) return null;
            var source = current ?? string.Empty;
            var newline = source.IndexOf("\r\n", StringComparison.Ordinal) >= 0
                ? "\r\n" : source.IndexOf('\r') >= 0 ? "\r" : "\n";
            return value.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", newline);
        }
    }
}
