using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RNAssistant.Core.Tools
{
    public static class VbaSourceValidator
    {
        public static bool TryValidateLiveCode(string code, out string error)
        {
            code = code ?? string.Empty;
            for (var index = 0; index < code.Length; index++)
            {
                var value = code[index];
                if (value == '\uFEFF')
                {
                    error = "VBA code contains a BOM/zero-width no-break space at character " + index + ". Remove the hidden character before writing.";
                    return false;
                }
                if (value == '\u2028' || value == '\u2029')
                {
                    error = "VBA code contains an unsupported Unicode line separator at character " + index + ". Use CRLF or LF line endings.";
                    return false;
                }
                if (char.GetUnicodeCategory(value) == UnicodeCategory.Format)
                {
                    error = "VBA code contains a hidden Unicode formatting character at character " + index + ".";
                    return false;
                }
                if (char.IsControl(value) && value != '\r' && value != '\n' && value != '\t')
                {
                    error = "VBA code contains raw control character U+" + ((int)value).ToString("X4") +
                        " at character " + index + ". Use a VBA expression such as ChrW$(" + ((int)value) +
                        ") instead of embedding the control character in source text.";
                    return false;
                }
            }

            int syntaxLine;
            string syntaxReason;
            if (TryFindUnsafeSyntax(code, out syntaxLine, out syntaxReason))
            {
                error = "VBA code is not safe to write on line " + syntaxLine + ": " + syntaxReason;
                return false;
            }

            int joinedLine;
            string joinedFragment;
            if (TryFindJoinedVbaBlock(code, out joinedLine, out joinedFragment))
            {
                error = "VBA code appears to join a block terminator and following code on line " + joinedLine +
                    " near '" + joinedFragment + "'. Insert a line break before writing.";
                return false;
            }

            DuplicateProcedure duplicate;
            if (TryFindDuplicateProcedure(code, out duplicate))
            {
                error = "VBA code contains duplicate procedure/property declaration '" +
                    duplicate.Name + "' on lines " + duplicate.FirstLine + " and " +
                    duplicate.SecondLine + ". Keep one final implementation before writing.";
                return false;
            }

            error = null;
            return true;
        }

        private static bool TryFindUnsafeSyntax(
            string code,
            out int lineNumber,
            out string reason)
        {
            var conditionalDepth = 0;
            var lines = Lines(code);
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                var line = lines[lineIndex] ?? string.Empty;
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("Attribute VB_", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("VERSION 1.0 CLASS", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("BEGIN VB.", StringComparison.OrdinalIgnoreCase))
                {
                    lineNumber = lineIndex + 1;
                    reason = "export-file metadata cannot be inserted through a live CodeModule; provide source code only.";
                    return true;
                }

                var executable = new StringBuilder(line.Length);
                var inString = false;
                for (var index = 0; index < line.Length; index++)
                {
                    var value = line[index];
                    if (!inString && value == '\'') break;
                    if (!inString && IsVbaRemComment(line, index)) break;
                    if (value == '"')
                    {
                        if (!inString && index > 0 && line[index - 1] == '\\')
                        {
                            lineNumber = lineIndex + 1;
                            reason = "found C/JSON-style \\\" escaping. VBA embeds a quote inside a string as \"\" and does not use backslash escaping.";
                            return true;
                        }
                        if (inString && index + 1 < line.Length && line[index + 1] == '"')
                        {
                            executable.Append("  ");
                            index++;
                            continue;
                        }
                        inString = !inString;
                        executable.Append(' ');
                        continue;
                    }
                    executable.Append(inString ? ' ' : value);
                }
                if (inString)
                {
                    lineNumber = lineIndex + 1;
                    reason = "string literal is not closed; use doubled quotes (\"\") inside VBA strings.";
                    return true;
                }

                var outside = executable.ToString();
                foreach (var token in new[] { "==", "!=", "&&", "||", "{", "}" })
                {
                    if (outside.IndexOf(token, StringComparison.Ordinal) >= 0)
                    {
                        lineNumber = lineIndex + 1;
                        reason = "found non-VBA token '" + token + "'.";
                        return true;
                    }
                }

                var preprocessor = outside.Trim();
                if (StartsWithPreprocessor(preprocessor, "#If")) conditionalDepth++;
                else if (StartsWithPreprocessor(preprocessor, "#End If"))
                {
                    conditionalDepth--;
                    if (conditionalDepth < 0)
                    {
                        lineNumber = lineIndex + 1;
                        reason = "#End If has no matching #If.";
                        return true;
                    }
                }
            }
            if (conditionalDepth != 0)
            {
                lineNumber = lines.Length;
                reason = "conditional-compilation #If block is not closed with #End If.";
                return true;
            }
            lineNumber = 0;
            reason = null;
            return false;
        }

        private static bool TryFindJoinedVbaBlock(
            string code,
            out int lineNumber,
            out string fragment)
        {
            var terminators = new[] { "End Sub", "End Function", "End Property", "End Type", "End Enum" };
            var lines = Lines(code);
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                var executable = VbaCodeOutsideStringsAndComments(lines[lineIndex]);
                foreach (var terminator in terminators)
                {
                    var searchFrom = 0;
                    while (searchFrom < executable.Length)
                    {
                        var found = executable.IndexOf(terminator, searchFrom, StringComparison.OrdinalIgnoreCase);
                        if (found < 0) break;
                        var after = found + terminator.Length;
                        var remainder = executable.Substring(after).Trim();
                        if (remainder.Length > 0)
                        {
                            lineNumber = lineIndex + 1;
                            var sourceFragment = lines[lineIndex].Substring(found).Trim();
                            fragment = sourceFragment.Substring(0, Math.Min(80, sourceFragment.Length));
                            return true;
                        }
                        searchFrom = after;
                    }
                }
            }
            lineNumber = 0;
            fragment = string.Empty;
            return false;
        }

        private static bool TryFindDuplicateProcedure(
            string code,
            out DuplicateProcedure duplicate)
        {
            var procedures = new Dictionary<string, ProcedureDeclaration>(
                StringComparer.OrdinalIgnoreCase);
            var properties = new Dictionary<string, Dictionary<string, ProcedureDeclaration>>(
                StringComparer.OrdinalIgnoreCase);
            var lines = Lines(code);
            var conditionalDepth = 0;
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                var executable = VbaCodeOutsideStringsAndComments(lines[lineIndex]);
                var trimmed = executable.Trim();
                if (TrackPreprocessorConditional(trimmed, ref conditionalDepth)) continue;
                if (conditionalDepth > 0) continue;

                ProcedureDeclaration declaration;
                if (!TryReadProcedureDeclaration(trimmed, lineIndex + 1, out declaration))
                    continue;

                ProcedureDeclaration existing;
                if (string.Equals(declaration.Kind, "Procedure", StringComparison.Ordinal))
                {
                    if (procedures.TryGetValue(declaration.Name, out existing) ||
                        TryGetFirstProperty(properties, declaration.Name, out existing))
                    {
                        duplicate = DuplicateProcedure.From(existing, declaration);
                        return true;
                    }
                    procedures[declaration.Name] = declaration;
                    continue;
                }

                if (procedures.TryGetValue(declaration.Name, out existing))
                {
                    duplicate = DuplicateProcedure.From(existing, declaration);
                    return true;
                }

                Dictionary<string, ProcedureDeclaration> accessors;
                if (!properties.TryGetValue(declaration.Name, out accessors))
                {
                    accessors = new Dictionary<string, ProcedureDeclaration>(
                        StringComparer.OrdinalIgnoreCase);
                    properties[declaration.Name] = accessors;
                }
                if (accessors.TryGetValue(declaration.Accessor, out existing))
                {
                    duplicate = DuplicateProcedure.From(existing, declaration);
                    return true;
                }
                accessors[declaration.Accessor] = declaration;
            }

            duplicate = null;
            return false;
        }

        private static bool TryGetFirstProperty(
            Dictionary<string, Dictionary<string, ProcedureDeclaration>> properties,
            string name,
            out ProcedureDeclaration declaration)
        {
            declaration = null;
            Dictionary<string, ProcedureDeclaration> accessors;
            if (!properties.TryGetValue(name, out accessors)) return false;
            foreach (var item in accessors.Values)
            {
                declaration = item;
                return true;
            }
            return false;
        }

        private static bool TryReadProcedureDeclaration(
            string line,
            int lineNumber,
            out ProcedureDeclaration declaration)
        {
            declaration = null;
            if (string.IsNullOrWhiteSpace(line)) return false;
            var index = 0;
            var token = ReadToken(line, ref index);
            if (token == null ||
                string.Equals(token, "Attribute", StringComparison.OrdinalIgnoreCase))
                return false;

            while (IsProcedureModifier(token))
            {
                token = ReadToken(line, ref index);
                if (token == null) return false;
            }

            if (string.Equals(token, "Declare", StringComparison.OrdinalIgnoreCase))
                return false;

            if (string.Equals(token, "Sub", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "Function", StringComparison.OrdinalIgnoreCase))
            {
                var name = ReadToken(line, ref index);
                if (string.IsNullOrWhiteSpace(name)) return false;
                declaration = new ProcedureDeclaration(
                    name,
                    "Procedure",
                    string.Empty,
                    lineNumber);
                return true;
            }

            if (string.Equals(token, "Property", StringComparison.OrdinalIgnoreCase))
            {
                var accessor = ReadToken(line, ref index);
                if (!IsPropertyAccessor(accessor)) return false;
                var name = ReadToken(line, ref index);
                if (string.IsNullOrWhiteSpace(name)) return false;
                declaration = new ProcedureDeclaration(
                    name,
                    "Property",
                    accessor,
                    lineNumber);
                return true;
            }

            return false;
        }

        private static string ReadToken(string text, ref int index)
        {
            SkipWhiteSpace(text, ref index);
            if (index >= (text ?? string.Empty).Length) return null;
            if (text[index] == '[')
            {
                var start = ++index;
                while (index < text.Length && text[index] != ']') index++;
                if (index >= text.Length || index == start) return null;
                var token = text.Substring(start, index - start);
                index++;
                return token;
            }
            if (!IsIdentifierStart(text[index])) return null;
            var tokenStart = index;
            index++;
            while (index < text.Length && IsIdentifierPart(text[index])) index++;
            return text.Substring(tokenStart, index - tokenStart);
        }

        private static void SkipWhiteSpace(string text, ref int index)
        {
            while (index < (text ?? string.Empty).Length &&
                char.IsWhiteSpace(text[index]))
            {
                index++;
            }
        }

        private static bool IsIdentifierStart(char value)
        {
            return value == '_' || char.IsLetter(value);
        }

        private static bool IsIdentifierPart(char value)
        {
            return value == '_' || char.IsLetterOrDigit(value);
        }

        private static bool IsProcedureModifier(string token)
        {
            return string.Equals(token, "Public", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "Private", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "Friend", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "Global", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "Static", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPropertyAccessor(string token)
        {
            return string.Equals(token, "Get", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "Let", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "Set", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TrackPreprocessorConditional(
            string line,
            ref int conditionalDepth)
        {
            if (string.IsNullOrWhiteSpace(line) || line[0] != '#') return false;
            if (StartsWithPreprocessor(line, "#If"))
            {
                conditionalDepth++;
                return true;
            }
            if (StartsWithPreprocessor(line, "#End If"))
            {
                if (conditionalDepth > 0) conditionalDepth--;
                return true;
            }
            return StartsWithPreprocessor(line, "#Else") ||
                StartsWithPreprocessor(line, "#ElseIf");
        }

        private static bool StartsWithPreprocessor(string line, string keyword)
        {
            if (!line.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
                return false;
            return line.Length == keyword.Length ||
                char.IsWhiteSpace(line[keyword.Length]);
        }

        private static string VbaCodeOutsideStringsAndComments(string line)
        {
            var source = line ?? string.Empty;
            var output = new StringBuilder(source.Length);
            var inString = false;
            for (var index = 0; index < source.Length; index++)
            {
                var value = source[index];
                if (!inString && value == '\'') break;
                if (!inString && IsVbaRemComment(source, index)) break;
                if (value == '"')
                {
                    output.Append(' ');
                    if (inString && index + 1 < source.Length && source[index + 1] == '"')
                    {
                        output.Append(' ');
                        index++;
                        continue;
                    }
                    inString = !inString;
                    continue;
                }
                output.Append(inString ? ' ' : value);
            }
            return output.ToString();
        }

        private static bool IsVbaRemComment(string source, int index)
        {
            if (index < 0 || index + 3 > (source ?? string.Empty).Length ||
                !string.Equals(source.Substring(index, 3), "Rem", StringComparison.OrdinalIgnoreCase)) return false;
            var validBefore = index == 0 || char.IsWhiteSpace(source[index - 1]) || source[index - 1] == ':';
            var after = index + 3;
            var validAfter = after >= source.Length || char.IsWhiteSpace(source[after]);
            return validBefore && validAfter;
        }

        private static string[] Lines(string code)
        {
            return (code ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        private sealed class ProcedureDeclaration
        {
            public ProcedureDeclaration(
                string name,
                string kind,
                string accessor,
                int line)
            {
                Name = name ?? string.Empty;
                Kind = kind ?? string.Empty;
                Accessor = accessor ?? string.Empty;
                Line = line;
            }

            public string Name { get; private set; }
            public string Kind { get; private set; }
            public string Accessor { get; private set; }
            public int Line { get; private set; }
        }

        private sealed class DuplicateProcedure
        {
            public string Name { get; private set; }
            public int FirstLine { get; private set; }
            public int SecondLine { get; private set; }

            public static DuplicateProcedure From(
                ProcedureDeclaration first,
                ProcedureDeclaration second)
            {
                return new DuplicateProcedure
                {
                    Name = second == null ? string.Empty : second.Name,
                    FirstLine = first == null ? 0 : first.Line,
                    SecondLine = second == null ? 0 : second.Line
                };
            }
        }
    }
}
