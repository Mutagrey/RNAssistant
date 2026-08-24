using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class VbaToolExecutor
    {
        private static JArray ParsePatchOperations(string patchJson)
        {
            if (string.IsNullOrWhiteSpace(patchJson))
            {
                return new JArray();
            }

            var token = JToken.Parse(patchJson);
            if (token.Type == JTokenType.Array)
            {
                return (JArray)token;
            }

            return new JArray(token);
        }

        private static ToolResult ApplyPatchOperation(string current, JObject operation, out string updated)
        {
            updated = current;
            var op = ((string)operation["op"] ?? string.Empty).Trim();
            var find = MatchLineEndings((string)operation["find"], current);
            var text = MatchLineEndings((string)(operation["text"] ?? operation["replace"]) ?? string.Empty, current);
            switch (op.ToLowerInvariant())
            {
                case "replace":
                    if (string.IsNullOrEmpty(find))
                    {
                        return ToolResult.Fail("Patch replace requires find.");
                    }
                    var exactCount = CountOccurrences(current, find);
                    if (exactCount == 0) return ToolResult.Fail("Patch find text was not found.");
                    if (exactCount != 1)
                    {
                        return ToolResult.Fail(
                            "Patch replace requires one exact match but found " + exactCount + ". Use a narrower find or replaceAll explicitly.",
                            JsonConvert.SerializeObject(new { matchCount = exactCount }),
                            "vba_patch_ambiguous",
                            true);
                    }
                    updated = ReplaceFirst(current, find, text);
                    return ToolResult.Ok("Replaced one exact occurrence.");
                case "replaceall":
                    if (string.IsNullOrEmpty(find))
                    {
                        return ToolResult.Fail("Patch replace requires find.");
                    }

                    var count = CountOccurrences(current, find);
                    if (count == 0)
                    {
                        return ToolResult.Fail("Patch find text was not found.");
                    }

                    updated = current.Replace(find, text);
                    return ToolResult.Ok("Replaced " + count + " occurrence(s).");
                case "replacefirst":
                    return ReplaceAtMatch(current, find, text, out updated);
                case "insertbefore":
                    return InsertAtUniqueMatch(current, find, text, true, out updated);
                case "insertafter":
                    return InsertAtUniqueMatch(current, find, text, false, out updated);
                case "replacelines":
                    return ReplaceLines(current, operation, text, out updated);
                case "regexreplace":
                    var pattern = (string)(operation["pattern"] ?? operation["find"]);
                    if (string.IsNullOrEmpty(pattern)) return ToolResult.Fail("regexReplace requires pattern.", null, "vba_patch_invalid", true);
                    try
                    {
                        var planned = TextPatternEngine.Replace(
                            current,
                            pattern,
                            text,
                            new TextPatternOptions { Mode = "regex", MatchCase = (bool?)(operation["matchCase"]) ?? true, WholeWord = (bool?)(operation["wholeWord"]) ?? false },
                            (bool?)(operation["replaceAll"]) ?? true,
                            Math.Max(1, Math.Min(10000, (int?)(operation["maxReplacements"]) ?? 500)));
                        if (planned.MatchCount == 0) return ToolResult.Fail("Patch regex was not found.");
                        updated = planned.Text;
                        return ToolResult.Ok("Regex replaced " + planned.MatchCount + " occurrence(s).");
                    }
                    catch (TextPatternException ex) { return ToolResult.Fail(ex.Message, null, ex.ErrorCode, false); }
                default:
                    return ToolResult.Fail("Unsupported patch op: " + op);
            }
        }

        private static ToolResult ReplaceAtMatch(string current, string find, string replacement, out string updated)
        {
            updated = current;
            if (string.IsNullOrEmpty(find))
            {
                return ToolResult.Fail("Patch operation requires find.");
            }

            var index = current.IndexOf(find, StringComparison.Ordinal);
            if (index < 0)
            {
                return ToolResult.Fail("Patch find text was not found.");
            }

            updated = current.Substring(0, index) + replacement + current.Substring(index + find.Length);
            return ToolResult.Ok("Patched first occurrence.");
        }

        private static ToolResult InsertAtUniqueMatch(string current, string find, string text, bool before, out string updated)
        {
            updated = current;
            if (string.IsNullOrEmpty(find)) return ToolResult.Fail("Patch insertion requires a non-empty anchor.");
            if (string.IsNullOrEmpty(text)) return ToolResult.Fail("Patch insertion requires non-empty text.", null, "vba_patch_invalid", true);
            var count = CountOccurrences(current, find);
            if (count == 0) return ToolResult.Fail("Patch insertion anchor was not found.");
            if (count != 1)
            {
                return ToolResult.Fail(
                    "Patch insertion anchor occurs " + count + " times. Re-read the exact target lines and retry with a unique anchor or replaceLines; do not bypass this safety check by running a macro.",
                    JsonConvert.SerializeObject(new
                    {
                        matchCount = count,
                        recovery = "Use common.vba_read_lines, then retry common.vba_apply_patch with a unique anchor or replaceLines."
                    }),
                    "vba_patch_ambiguous",
                    true);
            }
            var index = current.IndexOf(find, StringComparison.Ordinal);
            var insertionIndex = before ? index : index + find.Length;
            var newline = CurrentNewLine(current);
            if (insertionIndex > 0 && !IsLineBreak(current[insertionIndex - 1]) && !StartsWithLineBreak(text))
            {
                text = newline + text;
            }
            if (insertionIndex < current.Length && !IsLineBreak(current[insertionIndex]) && !EndsWithLineBreak(text))
            {
                text += newline;
            }
            updated = current.Insert(insertionIndex, text);
            return ToolResult.Ok("Inserted a line-safe block " + (before ? "before" : "after") + " the unique anchor.");
        }

        private static string ReplaceFirst(string current, string find, string replacement)
        {
            var index = current.IndexOf(find, StringComparison.Ordinal);
            return index < 0
                ? current
                : current.Substring(0, index) + replacement + current.Substring(index + find.Length);
        }

        private static string MatchLineEndings(string value, string current)
        {
            if (value == null) return null;
            var newline = CurrentNewLine(current);
            return value.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", newline);
        }

        private static string CurrentNewLine(string value)
        {
            return (value ?? string.Empty).IndexOf("\r\n", StringComparison.Ordinal) >= 0
                ? "\r\n"
                : (value ?? string.Empty).IndexOf('\r') >= 0 ? "\r" : "\n";
        }

        private static bool StartsWithLineBreak(string value)
        {
            return !string.IsNullOrEmpty(value) && IsLineBreak(value[0]);
        }

        private static bool EndsWithLineBreak(string value)
        {
            return !string.IsNullOrEmpty(value) && IsLineBreak(value[value.Length - 1]);
        }

        private static bool IsLineBreak(char value)
        {
            return value == '\r' || value == '\n';
        }

        private static ToolResult ReplaceLines(string current, JObject operation, string text, out string updated)
        {
            updated = current;
            int startLine;
            int deleteCount;
            if (!int.TryParse(Convert.ToString(operation["startLine"]), out startLine) ||
                !int.TryParse(Convert.ToString(operation["deleteCount"] ?? 0), out deleteCount) ||
                startLine <= 0 || deleteCount < 0)
            {
                return ToolResult.Fail("replaceLines requires startLine >= 1 and deleteCount >= 0.");
            }

            var newline = current.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            var lines = current.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
            var index = startLine - 1;
            if (index > lines.Count)
            {
                return ToolResult.Fail("replaceLines startLine is outside the module.");
            }

            if (deleteCount > lines.Count - index)
            {
                return ToolResult.Fail("replaceLines deleteCount extends past the end of the module.");
            }
            if (deleteCount > 0)
            {
                lines.RemoveRange(index, deleteCount);
            }

            if (!string.IsNullOrEmpty(text))
            {
                var inserted = text.Replace("\r\n", "\n").Replace('\r', '\n');
                if (inserted.EndsWith("\n", StringComparison.Ordinal))
                {
                    inserted = inserted.Substring(0, inserted.Length - 1);
                }
                if (inserted.Length > 0) lines.InsertRange(index, inserted.Split('\n'));
            }

            updated = string.Join(newline, lines.ToArray());
            return ToolResult.Ok("Replaced lines at " + startLine + " deleting " + deleteCount + ".");
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
    }
}
