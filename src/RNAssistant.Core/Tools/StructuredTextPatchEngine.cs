using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RNAssistant.Core.Tools
{
    public sealed class StructuredTextPatchOperation
    {
        public string Op { get; set; }
        public string Find { get; set; }
        public string Text { get; set; }
        public string Pattern { get; set; }
        public int? StartLine { get; set; }
        public int? DeleteCount { get; set; }
        public bool MatchCase { get; set; }
        public bool WholeWord { get; set; }
        public bool ReplaceAll { get; set; }
        public int MaxReplacements { get; set; }

        public StructuredTextPatchOperation()
        {
            MatchCase = true;
            ReplaceAll = true;
            MaxReplacements = 500;
        }
    }

    public sealed class StructuredTextPatchStep
    {
        public string Op { get; set; }
        public int MatchCount { get; set; }
        public string Message { get; set; }
    }

    public sealed class StructuredTextPatchResult
    {
        public string Text { get; set; }
        public List<StructuredTextPatchStep> Steps { get; private set; }

        public StructuredTextPatchResult()
        {
            Steps = new List<StructuredTextPatchStep>();
        }
    }

    public sealed class StructuredTextPatchException : Exception
    {
        public string ErrorCode { get; private set; }
        public int OperationIndex { get; private set; }

        public StructuredTextPatchException(string errorCode, string message, int operationIndex = 0)
            : base(message)
        {
            ErrorCode = errorCode;
            OperationIndex = operationIndex;
        }
    }

    public static class StructuredTextPatchEngine
    {
        public static StructuredTextPatchResult Apply(
            string source,
            IEnumerable<StructuredTextPatchOperation> operations,
            int maxOutputCharacters)
        {
            var items = (operations ?? new StructuredTextPatchOperation[0]).ToList();
            if (items.Count == 0)
            {
                throw Error("text_patch_invalid", "Patch has no operations.");
            }

            var current = source ?? string.Empty;
            var result = new StructuredTextPatchResult();
            for (var index = 0; index < items.Count; index++)
            {
                var operation = items[index];
                if (operation == null)
                {
                    throw Error("text_patch_invalid", "Each patch operation must be an object.", index + 1);
                }

                StructuredTextPatchStep step;
                try
                {
                    current = ApplyOne(current, operation, out step);
                }
                catch (StructuredTextPatchException ex)
                {
                    throw Error(ex.ErrorCode, "Patch operation " + (index + 1) + ": " + ex.Message, index + 1);
                }
                if (maxOutputCharacters > 0 && current.Length > maxOutputCharacters)
                {
                    throw Error("text_patch_too_large", "Patched text exceeds " + maxOutputCharacters + " characters.", index + 1);
                }
                result.Steps.Add(step);
            }

            result.Text = current;
            return result;
        }

        private static string ApplyOne(
            string current,
            StructuredTextPatchOperation operation,
            out StructuredTextPatchStep step)
        {
            var op = (operation.Op ?? string.Empty).Trim();
            var normalized = op.ToLowerInvariant();
            var text = MatchLineEndings(operation.Text ?? string.Empty, current);
            var find = operation.Find;
            switch (normalized)
            {
                case "replace":
                    RequireFind(find);
                    var exactMatches = FindMatches(current, find);
                    if (exactMatches.Count == 0) throw Error("text_patch_not_found", "Patch find text was not found.");
                    if (exactMatches.Count != 1)
                    {
                        throw Error("text_patch_ambiguous", "Patch replace requires one match but found " + exactMatches.Count + ". Use a narrower find or replaceAll explicitly.");
                    }
                    step = Step(op, 1, "Replaced one occurrence.");
                    return ReplaceMatches(current, exactMatches, text);

                case "replaceall":
                    RequireFind(find);
                    var allMatches = FindMatches(current, find);
                    if (allMatches.Count == 0) throw Error("text_patch_not_found", "Patch find text was not found.");
                    step = Step(op, allMatches.Count, "Replaced " + allMatches.Count + " occurrence(s).");
                    return ReplaceMatches(current, allMatches, text);

                case "insertbefore":
                    return InsertAtUniqueMatch(current, find, text, true, op, out step);

                case "insertafter":
                    return InsertAtUniqueMatch(current, find, text, false, op, out step);

                case "replacelines":
                    return ReplaceLines(current, operation, text, op, out step);

                case "regexreplace":
                    if (string.IsNullOrEmpty(operation.Pattern))
                    {
                        throw Error("text_patch_invalid", "regexReplace requires pattern.");
                    }
                    try
                    {
                        var replaced = TextPatternEngine.Replace(
                            current,
                            operation.Pattern,
                            text,
                            new TextPatternOptions
                            {
                                Mode = "regex",
                                MatchCase = operation.MatchCase,
                                WholeWord = operation.WholeWord
                            },
                            operation.ReplaceAll,
                            operation.MaxReplacements);
                        if (replaced.MatchCount == 0)
                        {
                            throw Error("text_patch_not_found", "Patch regex was not found.");
                        }
                        step = Step(op, replaced.MatchCount, "Regex replaced " + replaced.MatchCount + " occurrence(s).");
                        return replaced.Text;
                    }
                    catch (TextPatternException ex)
                    {
                        throw Error(ex.ErrorCode, ex.Message);
                    }

                default:
                    throw Error("text_patch_invalid", "Unsupported patch op: " + op);
            }
        }

        private static string InsertAtUniqueMatch(
            string current,
            string find,
            string text,
            bool before,
            string op,
            out StructuredTextPatchStep step)
        {
            RequireFind(find);
            if (string.IsNullOrEmpty(text)) throw Error("text_patch_invalid", "Patch insertion requires non-empty text.");
            var matches = FindMatches(current, find);
            if (matches.Count == 0) throw Error("text_patch_not_found", "Patch insertion anchor was not found.");
            if (matches.Count != 1)
            {
                throw Error("text_patch_ambiguous", "Patch insertion anchor occurs " + matches.Count + " times. Use a unique anchor.");
            }
            var insertionIndex = before ? matches[0].Start : matches[0].Start + matches[0].Length;
            step = Step(op, 1, "Inserted text " + (before ? "before" : "after") + " one unique anchor.");
            return current.Insert(insertionIndex, text);
        }

        private static string ReplaceLines(
            string current,
            StructuredTextPatchOperation operation,
            string text,
            string op,
            out StructuredTextPatchStep step)
        {
            if (!operation.StartLine.HasValue || !operation.DeleteCount.HasValue ||
                operation.StartLine.Value < 1 || operation.DeleteCount.Value < 0)
            {
                throw Error("text_patch_range_invalid", "replaceLines requires startLine >= 1 and deleteCount >= 0.");
            }

            var newline = CurrentNewLine(current);
            var lines = NormalizeLineEndings(current).Split('\n').ToList();
            var index = operation.StartLine.Value - 1;
            if (index > lines.Count)
            {
                throw Error("text_patch_range_invalid", "replaceLines startLine is outside the file.");
            }
            if (operation.DeleteCount.Value > lines.Count - index)
            {
                throw Error("text_patch_range_invalid", "replaceLines deleteCount extends past the end of the file.");
            }

            if (operation.DeleteCount.Value > 0) lines.RemoveRange(index, operation.DeleteCount.Value);
            if (!string.IsNullOrEmpty(text))
            {
                var inserted = NormalizeLineEndings(text);
                if (inserted.EndsWith("\n", StringComparison.Ordinal))
                {
                    inserted = inserted.Substring(0, inserted.Length - 1);
                }
                if (inserted.Length > 0) lines.InsertRange(index, inserted.Split('\n'));
            }

            step = Step(op, operation.DeleteCount.Value, "Replaced lines at " + operation.StartLine.Value + " deleting " + operation.DeleteCount.Value + ".");
            return string.Join(newline, lines.ToArray());
        }

        private static void RequireFind(string find)
        {
            if (string.IsNullOrEmpty(find)) throw Error("text_patch_invalid", "Patch operation requires find.");
        }

        private static StructuredTextPatchStep Step(string op, int matchCount, string message)
        {
            return new StructuredTextPatchStep { Op = op, MatchCount = matchCount, Message = message };
        }

        private static StructuredTextPatchException Error(string code, string message, int operationIndex = 0)
        {
            return new StructuredTextPatchException(code, message, operationIndex);
        }

        private struct PatchMatch
        {
            internal int Start;
            internal int Length;
        }

        private static List<PatchMatch> FindMatches(string source, string find)
        {
            var exact = new List<PatchMatch>();
            for (var index = 0; (index = source.IndexOf(find, index, StringComparison.Ordinal)) >= 0;
                 index += find.Length)
            {
                exact.Add(new PatchMatch { Start = index, Length = find.Length });
            }
            if (exact.Count > 0 ||
                (find.IndexOf('\n') < 0 && find.IndexOf('\r') < 0))
                return exact;

            // A copied anchor wins above. If a read normalized its line endings,
            // map each normalized match back to exact source character offsets.
            var normalized = new StringBuilder(source.Length);
            var offsets = new List<int>(source.Length + 1);
            for (var index = 0; index < source.Length; index++)
            {
                offsets.Add(index);
                if (source[index] == '\r')
                {
                    normalized.Append('\n');
                    if (index + 1 < source.Length && source[index + 1] == '\n') index++;
                }
                else
                {
                    normalized.Append(source[index]);
                }
            }
            offsets.Add(source.Length);
            var normalizedSource = normalized.ToString();
            var normalizedFind = NormalizeLineEndings(find);
            var result = new List<PatchMatch>();
            for (var index = 0; (index = normalizedSource.IndexOf(normalizedFind,
                     index, StringComparison.Ordinal)) >= 0; index += normalizedFind.Length)
            {
                result.Add(new PatchMatch
                {
                    Start = offsets[index],
                    Length = offsets[index + normalizedFind.Length] - offsets[index]
                });
            }
            return result;
        }

        private static string ReplaceMatches(string current, List<PatchMatch> matches, string replacement)
        {
            var result = new StringBuilder(current.Length);
            var offset = 0;
            foreach (var match in matches)
            {
                result.Append(current, offset, match.Start - offset);
                result.Append(replacement);
                offset = match.Start + match.Length;
            }
            result.Append(current, offset, current.Length - offset);
            return result.ToString();
        }

        private static string MatchLineEndings(string value, string current)
        {
            if (value == null) return null;
            return NormalizeLineEndings(value).Replace("\n", CurrentNewLine(current));
        }

        private static string NormalizeLineEndings(string value)
        {
            return (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        }

        private static string CurrentNewLine(string value)
        {
            return (value ?? string.Empty).IndexOf("\r\n", StringComparison.Ordinal) >= 0
                ? "\r\n"
                : (value ?? string.Empty).IndexOf('\r') >= 0 ? "\r" : "\n";
        }
    }
}
