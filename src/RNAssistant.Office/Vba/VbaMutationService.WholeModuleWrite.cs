using System;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Office.Vba
{
    internal sealed partial class VbaMutationService
    {
        public VbaWholeModuleWriteGuardPreparation PrepareWholeModuleWriteGuard(
            VbaWholeModuleWriteGuardRequest request)
        {
            var requestedName = (request == null
                ? null
                : request.RequestedModuleName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(requestedName))
            {
                return new VbaWholeModuleWriteGuardPreparation
                {
                    Error = VbaMutationOutcome.Error(
                        "moduleName is required.",
                        null,
                        "vba_module_name_required",
                        true)
                };
            }

            var correlation = request.Correlation ?? new VbaMutationCorrelation();
            var read = _reader.ReadModule(requestedName, 1000000);
            if (read == null)
            {
                return WholeModuleWriteGuardFailure(ReadFailure(null));
            }
            if (read.Success)
            {
                var resolved = string.IsNullOrWhiteSpace(read.Module.Name)
                    ? requestedName
                    : read.Module.Name;
                return BindWholeModuleWriteGuard(
                    correlation,
                    resolved,
                    read.Module,
                    requestedName);
            }
            if (!read.IsNotFound)
            {
                return WholeModuleWriteGuardFailure(ReadFailure(read));
            }

            var normalizedName = VbaReader.NormalizeModuleName(requestedName);
            if (!string.Equals(
                    requestedName,
                    normalizedName,
                    StringComparison.OrdinalIgnoreCase))
            {
                read = _reader.ReadModule(normalizedName, 1000000);
                if (read == null)
                {
                    return WholeModuleWriteGuardFailure(ReadFailure(null));
                }
                if (read.Success)
                {
                    var resolved = string.IsNullOrWhiteSpace(read.Module.Name)
                        ? normalizedName
                        : read.Module.Name;
                    return BindWholeModuleWriteGuard(
                        correlation,
                        resolved,
                        read.Module,
                        requestedName);
                }
                if (!read.IsNotFound)
                {
                    return WholeModuleWriteGuardFailure(ReadFailure(read));
                }
            }

            return new VbaWholeModuleWriteGuardPreparation
            {
                ResolvedModuleName = normalizedName,
                Guard = CreateGuard(
                    correlation,
                    normalizedName,
                    null,
                    requestedName,
                    false)
            };
        }

        public VbaMutationOutcome WriteWholeModule(
            VbaWholeModuleWriteRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request == null)
            {
                return VbaMutationOutcome.Error(
                    "VBA whole-module write request is missing.",
                    null,
                    "vba_write_request_missing",
                    false);
            }

            var moduleName = (request.ModuleName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                return VbaMutationOutcome.Error(
                    "moduleName is required.",
                    null,
                    "vba_module_name_required",
                    true);
            }
            if (request.Mode != VbaWholeModuleWriteMode.Upsert &&
                request.Mode != VbaWholeModuleWriteMode.CreateOnly &&
                request.Mode != VbaWholeModuleWriteMode.UpdateOnly)
            {
                return VbaMutationOutcome.Error(
                    "Whole-module write mode is invalid.",
                    null,
                    "vba_write_mode_invalid",
                    true);
            }

            var code = request.Code ?? string.Empty;
            var componentType = string.IsNullOrWhiteSpace(request.ComponentType)
                ? "StdModule"
                : request.ComponentType;
            var read = _reader.ReadModule(moduleName, 1000000);
            if (read == null) return ReadFailure(null);
            var exists = read.Success;
            if (!exists && !read.IsNotFound) return ReadFailure(read);
            var existing = exists ? read.Module : null;

            var guardError = ValidateWholeModuleWriteGuard(
                request,
                moduleName,
                exists,
                existing);
            if (guardError != null) return guardError;

            if (exists && request.Mode == VbaWholeModuleWriteMode.CreateOnly)
            {
                return VbaMutationOutcome.Error(
                    "VBA module already exists: " + moduleName +
                    ". Use mode=upsert to replace its complete source, or common.vba_apply_patch for a targeted edit.",
                    new JObject
                    {
                        ["moduleName"] = moduleName,
                        ["suggestedMode"] = "upsert",
                        ["patchTool"] = ToolId("vba_apply_patch")
                    },
                    "vba_module_exists",
                    true);
            }
            if (!exists && request.Mode == VbaWholeModuleWriteMode.UpdateOnly)
            {
                return VbaMutationOutcome.Error(
                    "VBA module does not exist: " + moduleName +
                    ". Use mode=upsert to create it automatically.",
                    new JObject
                    {
                        ["moduleName"] = moduleName,
                        ["suggestedMode"] = "upsert"
                    },
                    "vba_module_not_found",
                    true);
            }

            var guard = request.Guard;
            var expectedComponentType = exists ? existing.ComponentType : componentType;
            var mode = WriteModeName(request.Mode);
            var operationData = new JObject
            {
                ["requestedModuleName"] = guard == null
                    ? moduleName
                    : guard.RequestedModuleName,
                ["moduleName"] = moduleName,
                ["nameNormalized"] = guard != null &&
                    !string.Equals(
                        guard.RequestedModuleName,
                        moduleName,
                        StringComparison.Ordinal),
                ["componentType"] = expectedComponentType,
                ["mode"] = mode,
                ["created"] = !exists,
                ["codeSha256"] = CodeSha256(code)
            };
            if (request.DryRun)
            {
                return VbaMutationOutcome.Ok(
                    "Dry run: would " + (exists ? "update" : "create") + " VBA " +
                    expectedComponentType + " " + moduleName + ".",
                    operationData);
            }

            var correlation = CorrelationFrom(request.Guard, request.Correlation);
            var preparation = PrepareJournaledMutation(new VbaModuleMutationRequest
            {
                Operation = "write",
                ModuleName = moduleName,
                Before = existing,
                IntendedAfterExists = true,
                IntendedAfterCode = code,
                IntendedComponentType = expectedComponentType,
                Correlation = correlation
            });
            if (!preparation.Success) return preparation.Error;

            return ExecuteJournaledMutation(
                preparation.Preparation,
                delegate
                {
                    var action = exists
                        ? _backend.ReplaceModule(new VbaModuleWriteRequest
                        {
                            ModuleName = moduleName,
                            Code = code,
                            CreateIfMissing = false,
                            ExpectedCodeSha256 = CodeSha256(existing.Code)
                        })
                        : _backend.CreateModule(new VbaModuleCreateRequest
                        {
                            ModuleName = moduleName,
                            ComponentType = componentType,
                            Code = code
                        });
                    if (action == null ||
                        action.Status != VbaMutationActionStatus.Succeeded)
                    {
                        return action ?? VbaMutationActionResult.Error(
                            "VBA module write returned no result.",
                            null,
                            "vba_write_failed",
                            false);
                    }
                    return _verifier.VerifyModuleWrite(
                        moduleName,
                        code,
                        "VBA module " + (exists ? "updated: " : "created: ") + moduleName,
                        operationData,
                        "vba_write",
                        expectedComponentType,
                        correlation.SessionId);
                },
                cancellationToken);
        }

        private VbaWholeModuleWriteGuardPreparation BindWholeModuleWriteGuard(
            VbaMutationCorrelation correlation,
            string moduleName,
            VbaModuleState existing,
            string requestedName)
        {
            var currentHash = CodeSha256(existing == null ? string.Empty : existing.Code);
            string observedHash;
            if (TryGetObservation(correlation.SessionId, moduleName, out observedHash) &&
                !string.Equals(observedHash, currentHash, StringComparison.OrdinalIgnoreCase))
            {
                RemoveObservation(correlation.SessionId, moduleName);
                return WholeModuleWriteGuardFailure(StaleSnapshot(
                    moduleName,
                    true,
                    observedHash,
                    true,
                    currentHash,
                    "write"));
            }
            return new VbaWholeModuleWriteGuardPreparation
            {
                ResolvedModuleName = moduleName,
                Guard = CreateGuard(
                    correlation,
                    moduleName,
                    currentHash,
                    requestedName)
            };
        }

        private VbaMutationOutcome ValidateWholeModuleWriteGuard(
            VbaWholeModuleWriteRequest request,
            string moduleName,
            bool moduleExists,
            VbaModuleState current)
        {
            var guard = request.Guard;
            if (guard == null || guard.Version != 2 ||
                string.IsNullOrWhiteSpace(guard.ModuleName))
            {
                return SnapshotRequired(moduleName);
            }
            if (!GuardContextMatches(guard, request.Correlation, moduleName))
            {
                return VbaMutationOutcome.Error(
                    "The prepared VBA action belongs to another document, chat, or module. Retry the same tool in the current document.",
                    new JObject
                    {
                        ["moduleName"] = moduleName,
                        ["retrySameTool"] = true
                    },
                    "vba_snapshot_context_changed",
                    true);
            }

            var actualHash = moduleExists && current != null
                ? CodeSha256(current.Code)
                : null;
            if (guard.ModuleExists != moduleExists ||
                moduleExists && !string.Equals(
                    guard.CodeSha256,
                    actualHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                var correlation = request.Correlation ?? new VbaMutationCorrelation();
                RemoveObservation(correlation.SessionId, moduleName);
                return StaleSnapshot(
                    moduleName,
                    guard.ModuleExists,
                    guard.CodeSha256,
                    moduleExists,
                    actualHash,
                    "write");
            }
            return null;
        }

        private static VbaWholeModuleWriteGuardPreparation WholeModuleWriteGuardFailure(
            VbaMutationOutcome error)
        {
            return new VbaWholeModuleWriteGuardPreparation { Error = error };
        }

        private static string WriteModeName(VbaWholeModuleWriteMode mode)
        {
            if (mode == VbaWholeModuleWriteMode.CreateOnly) return "createOnly";
            if (mode == VbaWholeModuleWriteMode.UpdateOnly) return "updateOnly";
            return "upsert";
        }
    }
}
