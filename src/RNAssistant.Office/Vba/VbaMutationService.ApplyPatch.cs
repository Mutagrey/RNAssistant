using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Vba
{
    internal sealed partial class VbaMutationService
    {
        public VbaMutationOutcome ApplyPatch(
            VbaApplyPatchRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var moduleName = (request == null ? null : request.RequestedModuleName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                return VbaMutationOutcome.Error("moduleName is required.");
            }

            var operations = request.Operations == null
                ? new List<VbaPatchOperationRequest>()
                : request.Operations.ToList();
            if (operations.Count == 0)
            {
                return VbaMutationOutcome.Error("Patch has no operations.");
            }
            if (operations.Any(operation => operation == null))
            {
                return VbaMutationOutcome.Error("Each patch operation must be a JSON object.");
            }

            VbaModuleState module;
            string resolvedModuleName;
            var readError = TryReadExistingModule(moduleName, out resolvedModuleName, out module);
            if (readError != null) return readError;
            moduleName = resolvedModuleName;

            var code = module.Code;
            var currentHash = CodeSha256(code);
            var guardError = ValidateApplyPatchGuard(request, moduleName, module);
            if (guardError != null) return guardError;

            var updated = code;
            var summary = new List<Tuple<string, bool, string>>();
            foreach (var operation in operations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var beforeOperation = updated;
                var result = ApplyPatchOperation(updated, operation, out updated);
                if (result.Status != VbaMutationOutcomeStatus.Ok) return result;
                summary.Add(Tuple.Create(
                    operation.Operation,
                    !string.Equals(beforeOperation, updated, StringComparison.Ordinal),
                    result.Message));
            }

            var summaryData = VbaMutationData.Operations(summary);
            if (string.Equals(updated, code, StringComparison.Ordinal))
            {
                return VbaMutationOutcome.Ok(
                    "VBA patch is already satisfied; no document write was needed.",
                    new JObject
                    {
                        ["moduleName"] = moduleName,
                        ["operations"] = summaryData,
                        ["changed"] = false,
                        ["codeSha256"] = currentHash
                    });
            }

            var preview = new JObject
            {
                ["moduleName"] = moduleName,
                ["operations"] = summaryData,
                ["changed"] = true,
                ["oldLength"] = code.Length,
                ["newLength"] = updated.Length,
                ["previousCodeSha256"] = currentHash,
                ["codeSha256"] = CodeSha256(updated)
            };
            if (request.DryRun)
            {
                return VbaMutationOutcome.Ok(
                    "Dry run: would apply VBA patch to " + moduleName + ".",
                    preview);
            }

            var preparation = PrepareJournaledMutation(new VbaModuleMutationRequest
            {
                Operation = "patch",
                ModuleName = moduleName,
                Before = module,
                IntendedAfterExists = true,
                IntendedAfterCode = updated,
                IntendedComponentType = module.ComponentType,
                Correlation = CorrelationFrom(request.Guard, request.Correlation)
            });
            if (!preparation.Success) return preparation.Error;

            return ExecuteJournaledMutation(
                preparation.Preparation,
                delegate
                {
                    var writeResult = _backend.ReplaceModule(new VbaModuleWriteRequest
                    {
                        ModuleName = moduleName,
                        Code = updated,
                        CreateIfMissing = false,
                        ExpectedCodeSha256 = currentHash
                    });
                    if (writeResult == null ||
                        writeResult.Status != VbaMutationActionStatus.Succeeded)
                    {
                        return writeResult ?? VbaMutationActionResult.Error(
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
                        request.Correlation == null ? null : request.Correlation.SessionId);
                },
                cancellationToken);
        }

        private static VbaMutationOutcome ApplyPatchOperation(
            string current,
            VbaPatchOperationRequest operation,
            out string updated)
        {
            updated = current;
            var op = (operation == null ? null : operation.Operation ?? string.Empty).Trim();
            if (!string.Equals(op, "replace", StringComparison.Ordinal))
            {
                return VbaMutationOutcome.Error(
                    "Unsupported VBA patch op: " + op + ". Use replace with one exact unique source block.",
                    null,
                    "vba_patch_invalid",
                    true);
            }

            var patch = VbaPatchEngine.Replace(current, operation.Find, operation.Text);
            if (patch.Status == VbaPatchStatus.EmptyFind)
            {
                return VbaMutationOutcome.Error(
                    "VBA patch replace requires a non-empty exact find block.",
                    null,
                    "vba_patch_invalid",
                    true);
            }
            if (patch.Status == VbaPatchStatus.NotFound)
            {
                return VbaMutationOutcome.Error(
                    "The exact VBA source block was not found in the current module. Nothing was written; re-read the smallest relevant range and rebuild the patch from current code.",
                    new JObject
                    {
                        ["findSha256"] = TextPatternEngine.Sha256(patch.NormalizedFind),
                        ["inspectTool"] = "common.resources_read",
                        ["resourceProvider"] = VbaResourceProvider.ProviderName,
                        ["resourceKind"] = VbaResourceProvider.ComponentKind,
                        ["retrySamePatch"] = false
                    },
                    "vba_patch_stale_source",
                    true);
            }
            if (patch.Status == VbaPatchStatus.Ambiguous)
            {
                return VbaMutationOutcome.Error(
                    "The exact VBA source block occurs " + patch.MatchCount + " times. Nothing was written; include more unchanged surrounding source so find identifies one block.",
                    new JObject
                    {
                        ["matchCount"] = patch.MatchCount,
                        ["findSha256"] = TextPatternEngine.Sha256(patch.NormalizedFind),
                        ["inspectTool"] = "common.resources_read",
                        ["resourceProvider"] = VbaResourceProvider.ProviderName,
                        ["resourceKind"] = VbaResourceProvider.ComponentKind,
                        ["retrySamePatch"] = false
                    },
                    "vba_patch_ambiguous",
                    true);
            }

            updated = patch.Text;
            return patch.Status == VbaPatchStatus.Unchanged
                ? VbaMutationOutcome.Ok("The exact VBA replacement already matches current source; skipped.")
                : VbaMutationOutcome.Ok("Replaced one exact unique VBA source block without changing text outside it.");
        }
    }
}
