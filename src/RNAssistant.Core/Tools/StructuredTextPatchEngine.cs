using System;
using System.Collections.Generic;
using System.Linq;

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

        public StructuredTextPatchException(string errorCode, string message)
            : base(message)
        {
            ErrorCode = errorCode;
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
            foreach (var operation in items)
            {
                if (operation == null)
                {
                    throw Error("text_patch_invalid", "Each patch operation must be an object.");
                }

                StructuredTextPatchStep step;
                current = ApplyOne(current, operation, out step);
                if (maxOutputCharacters > 0 && current.Length > maxOutputCharacters)
                {
                    throw Error("text_patch_too_large", "Patched text exceeds " + maxOutputCharacters + " characters.");
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
            var find = MatchLineEndings(operation.Find, current);
            switch (normalized)
            {
                case "replace":
                    RequireFind(find);
                    var exactCount = CountOccurrences(current, find);
                    if (exactCount == 0) throw Error("text_patch_not_found", "Patch find text was not found.");
                    if (exactCount != 1)
                    {
                        throw Error("text_patch_ambiguous", "Patch replace requires one exact match but found " + exactCount + ". Use a narrower find or replaceAll explicitly.");
                    }
                    step = Step(op, 1, "Replaced one exact occurrence.");
                    return ReplaceFirst(current, find, text);

                case "replaceall":
                    RequireFind(find);
                    var allCount = CountOccurrences(current, find);
                    if (allCount == 0) throw Error("text_patch_not_found", "Patch find text was not found.");
                    step = Step(op, allCount, "Replaced " + allCount + " occurrence(s).");
                    return current.Replace(find, text);

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
            var count = CountOccurrences(current, find);
            if (count == 0) throw Error("text_patch_not_found", "Patch insertion anchor was not found.");
            if (count != 1)
            {
                throw Error("text_patch_ambiguous", "Patch insertion anchor occurs " + count + " times. Use a unique anchor or replaceLines.");
            }
            var index = current.IndexOf(find, StringComparison.Ordinal);
            var insertionIndex = before ? index : index + find.Length;
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

        private static StructuredTextPatchException Error(string code, string message)
        {
            return new StructuredTextPatchException(code, message);
        }

        private static int CountOccurrences(string value, string find)
        {
            var count = 0;
            var index = 0;
            while ((index = value.IndexOf(find, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += find.Length;
            }
            return count;
        }

        private static string ReplaceFirst(string current, string find, string replacement)
        {
            var index = current.IndexOf(find, StringComparison.Ordinal);
            return current.Substring(0, index) + replacement + current.Substring(index + find.Length);
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
