using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Vba
{
    internal sealed partial class VbaMutationService
    {
        public ToolResult ApplyPatch(
            ToolCommand command,
            string requestedModuleName,
            JArray requestedOperations,
            bool dryRun,
            ChatSession session,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var moduleName = (requestedModuleName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(moduleName)) return ToolResult.Fail("moduleName is required.");

            var operations = ParsePatchOperations(requestedOperations);
            if (operations.Count == 0) return ToolResult.Fail("Patch has no operations.");

            VbaModuleState module;
            ToolResult error;
            string resolvedModuleName;
            if (!TryReadExistingModule(moduleName, out resolvedModuleName, out module, out error)) return error;
            moduleName = resolvedModuleName;

            var code = module.Code;
            var currentHash = CodeSha256(code);
            var guardError = ValidateApplyPatchGuard(command, session, moduleName, module);
            if (guardError != null) return guardError;

            var updated = code;
            var summary = new List<object>();
            foreach (JObject operation in operations.OfType<JObject>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var beforeOperation = updated;
                var result = ApplyPatchOperation(updated, operation, out updated);
                if (!result.Success) return result;
                summary.Add(new
                {
                    op = (string)operation["op"],
                    changed = !string.Equals(beforeOperation, updated, StringComparison.Ordinal),
                    message = result.Message
                });
            }
            if (summary.Count != operations.Count)
            {
                return ToolResult.Fail("Each patch operation must be a JSON object.");
            }
            if (string.Equals(updated, code, StringComparison.Ordinal))
            {
                return ToolResult.Ok(
                    "VBA patch is already satisfied; no document write was needed.",
                    JsonConvert.SerializeObject(new
                    {
                        moduleName = moduleName,
                        operations = summary,
                        changed = false,
                        codeSha256 = currentHash
                    }));
            }

            var preview = JsonConvert.SerializeObject(new
            {
                moduleName = moduleName,
                operations = summary,
                changed = true,
                oldLength = code.Length,
                newLength = updated.Length,
                previousCodeSha256 = currentHash,
                codeSha256 = CodeSha256(updated)
            });
            if (dryRun)
            {
                return ToolResult.Ok("Dry run: would apply VBA patch to " + moduleName + ".", preview);
            }

            VbaMutationPreparation prepared;
            ToolResult journalError;
            if (!TryPrepareJournaledMutation(
                command,
                session,
                "patch",
                moduleName,
                module,
                true,
                updated,
                module.ComponentType,
                out prepared,
                out journalError))
            {
                return journalError;
            }

            return ExecuteJournaledMutation(prepared, delegate
            {
                var writeResult = WriteModule(moduleName, updated, false, currentHash);
                if (writeResult == null || !writeResult.Success)
                {
                    return writeResult ?? ToolResult.Fail(
                        "VBA patch write returned no result.",
                        null,
                        "vba_patch_failed",
                        false);
                }
                return _verifier.VerifyModuleWrite(
                    moduleName,
                    updated,
                    "VBA patch applied to " + moduleName + ".",
                    preview,
                    "vba_patch",
                    null,
                    session);
            });
        }

        private ToolResult WriteModule(
            string moduleName,
            string code,
            bool createIfMissing,
            string expectedCodeSha256)
        {
            var write = new ToolCommand { ToolId = BackendToolId("vba_replace_module") };
            write.Arguments["moduleName"] = moduleName;
            write.Arguments["code"] = code;
            write.Arguments["createIfMissing"] = createIfMissing;
            if (!string.IsNullOrWhiteSpace(expectedCodeSha256))
            {
                write.Arguments["expectedCodeSha256"] = expectedCodeSha256;
            }
            return _adapter.ExecuteTool(write);
        }

        private static JArray ParsePatchOperations(JArray patchValue)
        {
            return patchValue == null ? new JArray() : (JArray)patchValue.DeepClone();
        }

        private static ToolResult ApplyPatchOperation(
            string current,
            JObject operation,
            out string updated)
        {
            updated = current;
            var op = ((string)operation["op"] ?? string.Empty).Trim();
            if (!string.Equals(op, "replace", StringComparison.Ordinal))
            {
                return ToolResult.Fail(
                    "Unsupported VBA patch op: " + op + ". Use replace with one exact unique source block.",
                    null,
                    "vba_patch_invalid",
                    true);
            }
            var patch = VbaPatchEngine.Replace(current, (string)operation["find"], (string)operation["text"]);
            if (patch.Status == VbaPatchStatus.EmptyFind)
            {
                return ToolResult.Fail(
                    "VBA patch replace requires a non-empty exact find block.",
                    null,
                    "vba_patch_invalid",
                    true);
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
