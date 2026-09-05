using System;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Office.Vba
{
    internal sealed partial class VbaMutationService
    {
        public VbaDeleteModuleGuardPreparation PrepareDeleteModuleGuard(
            VbaDeleteModuleGuardRequest request)
        {
            var requestedName = (request == null
                ? null
                : request.RequestedModuleName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(requestedName))
            {
                return DeleteModuleGuardFailure(VbaMutationOutcome.Error(
                    "moduleName is required.",
                    null,
                    "vba_module_name_required",
                    true));
            }

            string resolvedName;
            VbaModuleState current;
            var readError = TryReadExistingModule(
                requestedName,
                out resolvedName,
                out current);
            if (readError != null)
            {
                return DeleteModuleGuardFailure(readError);
            }

            var correlation = request.Correlation ?? new VbaMutationCorrelation();
            var currentHash = CodeSha256(current.Code);
            string observedHash;
            if (TryGetObservation(correlation, resolvedName, out observedHash) &&
                !string.Equals(observedHash, currentHash, StringComparison.OrdinalIgnoreCase))
            {
                correlation.ObserveExternalDrift?.Invoke(resolvedName);
                return DeleteModuleGuardFailure(StaleSnapshot(
                    resolvedName,
                    true,
                    observedHash,
                    true,
                    currentHash,
                    "delete"));
            }

            return new VbaDeleteModuleGuardPreparation
            {
                ResolvedModuleName = resolvedName,
                Guard = CreateGuard(
                    correlation,
                    resolvedName,
                    currentHash,
                    requestedName)
            };
        }

        public VbaMutationOutcome DeleteModule(
            VbaDeleteModuleRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request == null)
            {
                return VbaMutationOutcome.Error(
                    "VBA module delete request is missing.",
                    null,
                    "vba_delete_request_missing",
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

            var read = _reader.ReadModule(moduleName, 1000000);
            if (read == null) return ReadFailure(null);
            if (!read.Success) return ReadFailure(read);
            var module = read.Module;
            if (!CanDeleteModule(module))
            {
                return VbaMutationOutcome.Error(
                    "Document modules and UserForms cannot be deleted through RNAssistant.",
                    null,
                    "vba_component_type_read_only",
                    false);
            }

            var guardError = ValidateDeleteModuleGuard(request, moduleName, module);
            if (guardError != null) return guardError;

            var operationData = new JObject
            {
                ["moduleName"] = moduleName,
                ["componentType"] = module.ComponentType
            };
            if (request.DryRun)
            {
                return VbaMutationOutcome.Ok(
                    "Dry run: would delete VBA " + module.ComponentType + " " + moduleName + ".",
                    operationData);
            }

            var correlation = CorrelationFrom(request.Guard, request.Correlation);
            var preparation = PrepareJournaledMutation(new VbaModuleMutationRequest
            {
                Operation = "delete",
                ModuleName = moduleName,
                Before = module,
                IntendedAfterExists = false,
                IntendedAfterCode = null,
                IntendedComponentType = module.ComponentType,
                Correlation = correlation
            });
            if (!preparation.Success) return preparation.Error;

            return ExecuteJournaledMutation(
                preparation.Preparation,
                delegate
                {
                    var action = _backend.DeleteModule(new VbaModuleDeleteRequest
                    {
                        ModuleName = moduleName,
                        ExpectedCodeSha256 = CodeSha256(module.Code)
                    });
                    if (action == null ||
                        action.Status != VbaMutationActionStatus.Succeeded)
                    {
                        return action ?? VbaMutationActionResult.Error(
                            "VBA delete returned no result.",
                            null,
                            "vba_delete_failed",
                            false);
                    }
                    return _verifier.VerifyModuleDeleted(
                        moduleName,
                        action.Data,
                        correlation.SessionId);
                },
                cancellationToken);
        }

        private VbaMutationOutcome ValidateDeleteModuleGuard(
            VbaDeleteModuleRequest request,
            string moduleName,
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

            var actualHash = CodeSha256(current == null ? string.Empty : current.Code);
            if (!guard.ModuleExists ||
                !string.Equals(
                    guard.CodeSha256,
                    actualHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                var correlation = request.Correlation ?? new VbaMutationCorrelation();
                return StaleSnapshot(
                    moduleName,
                    guard.ModuleExists,
                    guard.CodeSha256,
                    true,
                    actualHash,
                    "delete");
            }
            return null;
        }

        private static bool CanDeleteModule(VbaModuleState module)
        {
            return module != null &&
                (string.Equals(
                    module.ComponentType,
                    "StdModule",
                    StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(
                    module.ComponentType,
                    "ClassModule",
                    StringComparison.OrdinalIgnoreCase));
        }

        private static VbaDeleteModuleGuardPreparation DeleteModuleGuardFailure(
            VbaMutationOutcome error)
        {
            return new VbaDeleteModuleGuardPreparation { Error = error };
        }
    }
}
