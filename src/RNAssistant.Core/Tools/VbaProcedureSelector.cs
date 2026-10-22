using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Tools
{
    // Streaming boundary parser, not a VBA compiler. It scans all declarations
    // for uniqueness without retaining the module or evaluating #If branches.
    public static class VbaProcedureSelector
    {
        public static void Validate(string name, string kind)
        {
            if (string.IsNullOrWhiteSpace(name) || name != name.Trim() ||
                kind != null && kind != "sub" && kind != "function" &&
                kind != "property-get" && kind != "property-let" && kind != "property-set")
                throw Failure("Use a VBA procedure name and optionally procedureKind=sub/function/property-get/property-let/property-set.", "invalid");
        }

        public static ResourceProcedureSelection Select(TextReader source, string name, string kind = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            Validate(name, kind);
            if (source == null) throw new ArgumentNullException(nameof(source));
            var scanner = new Scanner(source, Math.Max(16, name.Length), cancellationToken);
            var branches = new List<int>();
            var branchId = 0;
            ResourceProcedureSelection active = null, selected = null;
            string activeBranch = null;
            Statement statement;
            while ((statement = scanner.Next()) != null)
            {
                var words = statement.Words;
                if (words.Count == 0) continue;
                if (Is(words[0], "#"))
                {
                    if (words.Count > 1 && Is(words[1], "If")) branches.Add(++branchId);
                    else if (words.Count > 1 && (Is(words[1], "Else") || Is(words[1], "ElseIf")))
                    {
                        if (branches.Count == 0) throw InvalidSource();
                        branches[branches.Count - 1] = ++branchId;
                    }
                    else if (words.Count > 2 && Is(words[1], "End") && Is(words[2], "If"))
                    {
                        if (branches.Count == 0) throw InvalidSource();
                        branches.RemoveAt(branches.Count - 1);
                    }
                    continue;
                }
                var index = 0;
                while (index < words.Count && (Is(words[index], "Public") || Is(words[index], "Private") ||
                    Is(words[index], "Friend") || Is(words[index], "Global") || Is(words[index], "Static"))) index++;
                string declaredKind = null;
                if (index < words.Count && (Is(words[index], "Sub") || Is(words[index], "Function")))
                    declaredKind = words[index++].ToLowerInvariant();
                else if (index + 1 < words.Count && Is(words[index], "Property") &&
                    (Is(words[index + 1], "Get") || Is(words[index + 1], "Let") || Is(words[index + 1], "Set")))
                {
                    declaredKind = "property-" + words[index + 1].ToLowerInvariant();
                    index += 2;
                }
                if (declaredKind != null)
                {
                    if (active != null || index >= words.Count) throw InvalidSource();
                    var declaredName = words[index];
                    if (declaredName.Length == 0 || declaredName[0] != '[' &&
                        declaredName[0] != '_' && !char.IsLetter(declaredName[0])) throw InvalidSource();
                    if (declaredName.StartsWith("[", StringComparison.Ordinal))
                        declaredName = declaredName.Substring(1, declaredName.Length - 2);
                    active = new ResourceProcedureSelection { Name = declaredName, Kind = declaredKind,
                        StartChar = statement.Start, StartLine = statement.StartLine,
                        ConditionalCompilation = branches.Count > 0 };
                    activeBranch = string.Join("/", branches);
                }
                else if (words.Count >= 2 && Is(words[0], "End") &&
                    (Is(words[1], "Sub") || Is(words[1], "Function") || Is(words[1], "Property")))
                {
                    if (active == null || words.Count != 2 ||
                        !Is(words[1], active.Kind.StartsWith("property-", StringComparison.Ordinal) ? "Property" : active.Kind) ||
                        activeBranch != string.Join("/", branches)) throw InvalidSource();
                    active.EndChar = statement.End;
                    active.EndLine = statement.EndLine;
                    if (Is(active.Name, name) && (kind == null || active.Kind == kind))
                    {
                        if (selected != null)
                            throw Failure("The VBA procedure name is ambiguous. Specify procedureKind for a property accessor; for conditional/duplicate declarations use startLine/lineCount or startChar/charCount.", "ambiguous");
                        selected = active;
                    }
                    active = null;
                }
            }
            if (active != null || branches.Count != 0) throw InvalidSource();
            if (selected == null)
                throw Failure("No matching VBA procedure was found. Search the source, then use its exact name/kind or line/character selectors.", "not_found");
            return selected;
        }

        public static void RequireFits(ResourceProcedureSelection selection, int maximumCharacters)
        {
            if (maximumCharacters < 1) throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
            if (selection.EndChar - selection.StartChar > maximumCharacters)
                throw Failure("The selected procedure exceeds the model read budget. Read it in parts with startLine/lineCount (starts at " +
                    selection.StartLine + ") or startChar/charCount (starts at " + selection.StartChar + ").", "too_large");
        }

        private static bool Is(string value, string expected)
        { return string.Equals(value, expected, StringComparison.OrdinalIgnoreCase); }
        private static ResourceRequestException InvalidSource()
        { return Failure("VBA procedure boundaries are incomplete or cross conditional branches. Use source line/character selectors; no complete procedure was returned.", "source_invalid"); }
        private static ResourceRequestException Failure(string message, string suffix)
        { return new ResourceRequestException(message, "resource_procedure_" + suffix, false); }

        private sealed class Statement
        {
            public int Start, End, StartLine, EndLine;
            // Only the prefix is needed. Larger statements and literals are scanned, not buffered.
            public readonly List<string> Words = new List<string>();
        }

        private sealed class Scanner
        {
            private readonly TextReader _source;
            private readonly int _tokenLength;
            private readonly CancellationToken _cancellation;
            private int _offset, _line = 1;
            public Scanner(TextReader source, int tokenLength, CancellationToken cancellation)
            { _source = source; _tokenLength = tokenLength; _cancellation = cancellation; }

            private int Read()
            {
                if ((_offset & 4095) == 0) _cancellation.ThrowIfCancellationRequested();
                var value = _source.Read();
                if (value >= 0)
                {
                    if (_offset == int.MaxValue)
                        throw Failure("The source exceeds the current UTF-16 coordinate range.", "source_invalid");
                    _offset++;
                }
                return value;
            }

            private void NewLine()
            {
                var value = Read();
                if (value == '\r' && _source.Peek() == '\n') Read();
                _line++;
            }

            public Statement Next()
            {
                _cancellation.ThrowIfCancellationRequested();
                if (_source.Peek() < 0) return null;
                var statement = new Statement { Start = _offset, StartLine = _line };
                while (_source.Peek() >= 0)
                {
                    var value = (char)_source.Peek();
                    if (value == '\r' || value == '\n')
                    {
                        statement.EndLine = _line;
                        NewLine();
                        statement.End = _offset;
                        return statement;
                    }
                    if (char.IsWhiteSpace(value) || value == '\uFEFF' && _offset == 0) { Read(); continue; }
                    if (value == '\'') { Comment(); continue; }
                    // # also denotes file channels and numeric type suffixes.
                    // Treat it as punctuation; valid date/time components cannot
                    // form a procedure declaration or terminator after a colon.
                    if (value == '"')
                    {
                        Read();
                        Literal(value);
                        Add(statement, "?");
                        continue;
                    }
                    if (value == ':')
                    {
                        Read();
                        if (_source.Peek() == '=') { Read(); Add(statement, ":="); continue; }
                        statement.End = _offset; statement.EndLine = _line;
                        return statement;
                    }
                    if (value == '[')
                    {
                        Read();
                        var word = new StringBuilder();
                        while (_source.Peek() >= 0 && _source.Peek() != ']')
                        {
                            if (_source.Peek() == '\r' || _source.Peek() == '\n') throw InvalidSource();
                            Append(word, (char)Read());
                        }
                        if (Read() != ']') throw InvalidSource();
                        Add(statement, "[" + word + "]");
                        continue;
                    }
                    if (char.IsLetter(value) || value == '_')
                    {
                        var word = new StringBuilder();
                        while (_source.Peek() >= 0 && (char.IsLetterOrDigit((char)_source.Peek()) || _source.Peek() == '_'))
                            Append(word, (char)Read());
                        var text = word.ToString();
                        if (Is(text, "Rem") && (_source.Peek() < 0 || char.IsWhiteSpace((char)_source.Peek())))
                        { Comment(); continue; }
                        if (text == "_" && statement.Words.Count > 0)
                        {
                            while (_source.Peek() == ' ' || _source.Peek() == '\t') Read();
                            if (_source.Peek() == '\r' || _source.Peek() == '\n') { NewLine(); continue; }
                        }
                        Add(statement, text);
                        continue;
                    }
                    Read(); Add(statement, value.ToString());
                }
                statement.End = _offset; statement.EndLine = _line;
                return statement;
            }

            private void Append(StringBuilder word, char value)
            {
                // An overlong token cannot equal a keyword or the requested name.
                // Keep a sentinel suffix, without imposing a source/identifier limit.
                if (word.Length <= _tokenLength) word.Append(value);
            }
            private static void Add(Statement statement, string word)
            { if (statement.Words.Count < 8) statement.Words.Add(word); }
            private void Comment()
            { while (_source.Peek() >= 0 && _source.Peek() != '\r' && _source.Peek() != '\n') Read(); }
            private void Literal(char delimiter)
            {
                while (_source.Peek() >= 0)
                {
                    var value = Read();
                    if (value == '\r' || value == '\n') throw InvalidSource();
                    if (value != delimiter) continue;
                    if (delimiter == '"' && _source.Peek() == '"') { Read(); continue; }
                    return;
                }
                throw InvalidSource();
            }
        }
    }
}
