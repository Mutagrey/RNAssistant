using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;

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
            if (!string.Equals(op, "replace", StringComparison.Ordinal))
            {
                return ToolResult.Fail("Unsupported VBA patch op: " + op + ". Use replace with one exact unique source block.", null, "vba_patch_invalid", true);
            }
            var patch = VbaPatchEngine.Replace(current, (string)operation["find"], (string)operation["text"]);
            if (patch.Status == VbaPatchStatus.EmptyFind)
            {
                return ToolResult.Fail("VBA patch replace requires a non-empty exact find block.", null, "vba_patch_invalid", true);
            }
            if (patch.Status == VbaPatchStatus.NotFound)
            {
                return ToolResult.Fail(
                    "The exact VBA source block was not found in the current module. Nothing was written; re-read the smallest relevant range and rebuild the patch from current code.",
                    JsonConvert.SerializeObject(new
                    {
                        findSha256 = TextPatternEngine.Sha256(patch.NormalizedFind),
                        inspectTool = "common.resources_read",
                        resourceProvider = VbaResourceProvider.ProviderName,
                        resourceKind = VbaResourceProvider.ComponentKind,
                        retrySamePatch = false
                    }),
                    "vba_patch_stale_source",
                    true);
            }
            if (patch.Status == VbaPatchStatus.Ambiguous)
            {
                return ToolResult.Fail(
                    "The exact VBA source block occurs " + patch.MatchCount + " times. Nothing was written; include more unchanged surrounding source so find identifies one block.",
                    JsonConvert.SerializeObject(new
                    {
                        matchCount = patch.MatchCount,
                        findSha256 = TextPatternEngine.Sha256(patch.NormalizedFind),
                        inspectTool = "common.resources_read",
                        resourceProvider = VbaResourceProvider.ProviderName,
                        resourceKind = VbaResourceProvider.ComponentKind,
                        retrySamePatch = false
                    }),
                    "vba_patch_ambiguous",
                    true);
            }
            updated = patch.Text;
            if (patch.Status == VbaPatchStatus.Unchanged)
            {
                return ToolResult.Ok("The exact VBA replacement already matches current source; skipped.");
            }
            return ToolResult.Ok("Replaced one exact unique VBA source block without changing text outside it.");
        }

    }
}
