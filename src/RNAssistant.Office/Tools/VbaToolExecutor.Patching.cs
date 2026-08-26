using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class VbaToolExecutor
    {
        private static JArray ParsePatchOperations(object patchValue)
        {
            var operations = patchValue as JArray;
            return operations == null ? new JArray() : (JArray)operations.DeepClone();
        }

        private static ToolResult ApplyPatchOperation(string current, JObject operation, out string updated)
        {
            updated = current;
            var op = ((string)operation["op"] ?? string.Empty).Trim();
            var find = MatchLineEndings((string)operation["find"], current);
            var text = MatchLineEndings((string)operation["text"] ?? string.Empty, current);
            if (!string.Equals(op, "replace", StringComparison.Ordinal))
            {
                return ToolResult.Fail("Unsupported VBA patch op: " + op + ". Use replace with one exact unique source block.", null, "vba_patch_invalid", true);
            }
            if (string.IsNullOrEmpty(find))
            {
                return ToolResult.Fail("VBA patch replace requires a non-empty exact find block.", null, "vba_patch_invalid", true);
            }
            var exactCount = CountOccurrences(current, find);
            if (exactCount == 0)
            {
                return ToolResult.Fail(
                    "The exact VBA source block was not found in the current module. Nothing was written; re-read the smallest relevant range and rebuild the patch from current code.",
                    JsonConvert.SerializeObject(new
                    {
                        findSha256 = TextPatternEngine.Sha256(find),
                        inspectTool = "common.vba_read_module",
                        retrySamePatch = false
                    }),
                    "vba_patch_stale_source",
                    true);
            }
            if (exactCount != 1)
            {
                return ToolResult.Fail(
                    "The exact VBA source block occurs " + exactCount + " times. Nothing was written; include more unchanged surrounding source so find identifies one block.",
                    JsonConvert.SerializeObject(new
                    {
                        matchCount = exactCount,
                        findSha256 = TextPatternEngine.Sha256(find),
                        inspectTool = "common.vba_read_module",
                        retrySamePatch = false
                    }),
                    "vba_patch_ambiguous",
                    true);
            }
            var index = current.IndexOf(find, StringComparison.Ordinal);
            updated = current.Substring(0, index) + text + current.Substring(index + find.Length);
            if (string.Equals(updated, current, StringComparison.Ordinal))
            {
                return ToolResult.Fail("The exact VBA patch makes no change.", null, "vba_patch_no_change", true);
            }
            return ToolResult.Ok("Replaced one exact unique VBA source block without changing text outside it.");
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
