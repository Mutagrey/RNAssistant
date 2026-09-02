using System;
using System.Threading;
using Newtonsoft.Json.Linq;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Vba
{
    internal sealed partial class VbaMutationService
    {
        private const int RenameGuardVersion = 4;

        public VbaRenameGuardPreparation PrepareRenameGuard(VbaRenameGuardRequest request)
        {
            var requestedSourceName = (request == null
                ? null
                : request.RequestedModuleName ?? string.Empty).Trim();
            var requestedTargetName = (request == null
                ? null
                : request.RequestedTargetModuleName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(requestedSourceName) ||
                string.IsNullOrWhiteSpace(requestedTargetName))
            {
                return RenameGuardFailure(VbaMutationOutcome.Error(
                    "moduleName and newModuleName are required for mode=rename.",
                    null,
                    "vba_module_name_required",
                    true));
            }

            string sourceName;
            VbaModuleState source;
            var sourceError = TryReadExistingModule(
                requestedSourceName,
                out sourceName,
                out source);
            if (sourceError != null) return RenameGuardFailure(sourceError);
            if (!CanRenameComponent(source))
            {
                return RenameGuardFailure(VbaMutationOutcome.Error(
                    "Only StdModule, ClassModule, and blank code-only MSForm components can be renamed through RNAssistant.",
                    new JObject
                    {
                        ["moduleName"] = sourceName,
                        ["componentType"] = source.ComponentType
                    },
                    "vba_component_type_read_only",
                    false));
            }

            var targetName = VbaReader.NormalizeModuleName(requestedTargetName);
            if (string.Equals(sourceName, targetName, StringComparison.OrdinalIgnoreCase))
            {
                return RenameGuardFailure(VbaMutationOutcome.Error(
                    "The rename destination resolves to the current VBA component name.",
                    new JObject
                    {
                        ["moduleName"] = sourceName,
                        ["requestedNewModuleName"] = requestedTargetName,
                        ["normalizedNewModuleName"] = targetName
                    },
                    "vba_rename_noop",
                    true));
            }

            var target = _reader.ReadModule(targetName, 1000000);
            if (target == null) return RenameGuardFailure(ReadFailure(null));
            if (target.Success)
            {
                return RenameGuardFailure(VbaMutationOutcome.Error(
                    "VBA rename destination already exists: " + targetName + ".",
                    new JObject
                    {
                        ["moduleName"] = sourceName,
                        ["newModuleName"] = targetName,
                        ["targetComponentType"] = target.Module.ComponentType
                    },
                    "vba_module_exists",
                    true));
            }
            if (!target.IsNotFound) return RenameGuardFailure(ReadFailure(target));

            var correlation = request.Correlation ?? new VbaMutationCorrelation();
            var sourceHash = CodeSha256(source.Code);
            string observedHash;
            if (TryGetObservation(correlation.SessionId, sourceName, out observedHash) &&
                !string.Equals(observedHash, sourceHash, StringComparison.OrdinalIgnoreCase))
            {
                RemoveObservation(correlation.SessionId, sourceName);
                return RenameGuardFailure(StaleSnapshot(
                    sourceName,
                    true,
                    observedHash,
                    true,
                    sourceHash,
                    "rename"));
            }

            return new VbaRenameGuardPreparation
            {
                ResolvedModuleName = sourceName,
                ResolvedTargetModuleName = targetName,
                Guard = CreateRenameGuard(
                    correlation,
                    sourceName,
                    requestedSourceName,
                    source,
                    targetName,
                    requestedTargetName)
            };
        }

        public VbaMutationOutcome RenameModule(
            VbaRenameRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request == null)
            {
                return VbaMutationOutcome.Error(
                    "VBA rename request is missing.",
                    null,
                    "vba_rename_request_missing",
                    false);
            }

            var sourceName = (request.ModuleName ?? string.Empty).Trim();
            var targetName = (request.NewModuleName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(sourceName) || string.IsNullOrWhiteSpace(targetName))
            {
                return VbaMutationOutcome.Error(
                    "moduleName and newModuleName are required for mode=rename.",
                    null,
                    "vba_module_name_required",
                    true);
            }

            var sourceRead = _reader.ReadModule(sourceName, 1000000);
            if (sourceRead == null) return ReadFailure(null);
            var sourceExists = sourceRead.Success;
            if (!sourceExists && !sourceRead.IsNotFound) return ReadFailure(sourceRead);
            var source = sourceExists ? sourceRead.Module : null;

            var targetRead = _reader.ReadModule(targetName, 1000000);
            if (targetRead == null) return ReadFailure(null);
            var targetExists = targetRead.Success;
            if (!targetExists && !targetRead.IsNotFound) return ReadFailure(targetRead);
            var target = targetExists ? targetRead.Module : null;

            var guardError = ValidateRenameGuard(
                request,
                sourceName,
                sourceExists,
                source,
                targetName,
                targetExists,
                target);
            if (guardError != null) return guardError;
            if (!sourceExists) return ReadFailure(sourceRead);
            if (targetExists)
            {
                return VbaMutationOutcome.Error(
                    "VBA rename destination already exists: " + targetName + ".",
                    null,
                    "vba_module_exists",
                    true);
            }
            if (!CanRenameComponent(source))
            {
                return VbaMutationOutcome.Error(
                    "Only StdModule, ClassModule, and blank code-only MSForm components can be renamed through RNAssistant.",
                    new JObject
                    {
                        ["moduleName"] = sourceName,
                        ["componentType"] = source.ComponentType
                    },
                    "vba_component_type_read_only",
                    false);
            }

            var requestedTargetName = request.Guard == null ||
                string.IsNullOrWhiteSpace(request.Guard.RequestedTargetModuleName)
                    ? targetName
                    : request.Guard.RequestedTargetModuleName;
            var sourceHash = CodeSha256(source.Code);
            var operationData = new JObject
            {
                ["previousModuleName"] = sourceName,
                ["moduleName"] = targetName,
                ["requestedNewModuleName"] = requestedTargetName,
                ["nameNormalized"] = !string.Equals(
                    requestedTargetName,
                    targetName,
                    StringComparison.Ordinal),
                ["componentType"] = source.ComponentType,
                ["mode"] = "rename",
                ["codeSha256"] = sourceHash
            };
            if (request.DryRun)
            {
                return VbaMutationOutcome.Ok(
                    "Dry run: would rename VBA " + source.ComponentType + " " +
                    sourceName + " to " + targetName + ".",
                    operationData);
            }

            var correlation = CorrelationFrom(request.Guard, request.Correlation);
            var preparation = PrepareJournaledRename(
                sourceName,
                targetName,
                source,
                correlation);
            if (!preparation.Success) return preparation.Error;

            return ExecuteJournaledRename(
                preparation.Preparation,
                operationData,
                delegate
                {
                    return _backend.RenameModule(new VbaRenameBackendRequest
                    {
                        ModuleName = sourceName,
                        NewModuleName = targetName,
                        ExpectedCodeSha256 = sourceHash,
                        ExpectedComponentType = source.ComponentType
                    });
                },
                correlation.SessionId,
                sourceHash,
                cancellationToken);
        }

        private VbaMutationOutcome ValidateRenameGuard(
            VbaRenameRequest request,
            string sourceName,
            bool sourceExists,
            VbaModuleState source,
            string targetName,
            bool targetExists,
            VbaModuleState target)
        {
            var guard = request.Guard;
            if (guard == null || guard.Version != RenameGuardVersion ||
                string.IsNullOrWhiteSpace(guard.ModuleName) ||
                string.IsNullOrWhiteSpace(guard.TargetModuleName) ||
                string.IsNullOrWhiteSpace(guard.ComponentType))
            {
                return SnapshotRequired(sourceName);
            }
            if (!GuardContextMatches(guard, request.Correlation, sourceName) ||
                !string.Equals(
                    guard.TargetModuleName,
                    targetName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return VbaMutationOutcome.Error(
                    "The prepared VBA rename belongs to another document, chat, source, or destination. Retry it in the current document.",
                    new JObject
                    {
                        ["moduleName"] = sourceName,
                        ["newModuleName"] = targetName,
                        ["retrySameTool"] = true
                    },
                    "vba_snapshot_context_changed",
                    true);
            }

            var sourceHash = sourceExists && source != null
                ? CodeSha256(source.Code)
                : null;
            var targetHash = targetExists && target != null
                ? CodeSha256(target.Code)
                : null;
            var sourceTypeMatches = sourceExists && source != null &&
                string.Equals(
                    guard.ComponentType,
                    source.ComponentType,
                    StringComparison.OrdinalIgnoreCase) &&
                guard.CodeOnlyUserForm == source.CodeOnlyUserForm;
            if (guard.ModuleExists != sourceExists ||
                sourceExists && (!string.Equals(
                    guard.CodeSha256,
                    sourceHash,
                    StringComparison.OrdinalIgnoreCase) || !sourceTypeMatches) ||
                guard.TargetModuleExists != targetExists ||
                targetExists && !string.Equals(
                    guard.TargetCodeSha256,
                    targetHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                var correlation = request.Correlation ?? new VbaMutationCorrelation();
                RemoveObservation(correlation.SessionId, sourceName);
                return VbaMutationOutcome.Error(
                    "The VBA source or rename destination changed after confirmation was prepared. The rename was not applied; retry it against current state.",
                    new JObject
                    {
                        ["moduleName"] = sourceName,
                        ["newModuleName"] = targetName,
                        ["expectedSourceExists"] = guard.ModuleExists,
                        ["actualSourceExists"] = sourceExists,
                        ["expectedSourceCodeSha256"] = guard.CodeSha256,
                        ["actualSourceCodeSha256"] = sourceHash,
                        ["expectedSourceComponentType"] = guard.ComponentType,
                        ["actualSourceComponentType"] = source == null ? null : source.ComponentType,
                        ["expectedTargetExists"] = guard.TargetModuleExists,
                        ["actualTargetExists"] = targetExists,
                        ["actualTargetCodeSha256"] = targetHash,
                        ["retrySameTool"] = true,
                        ["inspectTool"] = "common.resources_read",
                        ["discoveryScope"] = "vba"
                    },
                    "stale_vba_module",
                    true);
            }
            return null;
        }

        private VbaMutationGuard CreateRenameGuard(
            VbaMutationCorrelation correlation,
            string sourceName,
            string requestedSourceName,
            VbaModuleState source,
            string targetName,
            string requestedTargetName)
        {
            correlation = correlation ?? new VbaMutationCorrelation();
            return new VbaMutationGuard
            {
                Version = RenameGuardVersion,
                Host = _document.HostName ?? string.Empty,
                DocumentKey = _document.DocumentKey ?? string.Empty,
                RuntimeDocumentKey = _document.RuntimeDocumentKey ?? string.Empty,
                SessionId = correlation.SessionId ?? string.Empty,
                RunId = correlation.RunId,
                TurnId = correlation.TurnId,
                StepId = correlation.StepId,
                ToolCallId = correlation.ToolCallId,
                ModuleName = sourceName ?? string.Empty,
                RequestedModuleName = requestedSourceName ?? sourceName ?? string.Empty,
                ModuleExists = true,
                CodeSha256 = CodeSha256(source == null ? string.Empty : source.Code),
                ComponentType = source == null ? string.Empty : source.ComponentType,
                CodeOnlyUserForm = source == null ? null : source.CodeOnlyUserForm,
                TargetModuleName = targetName ?? string.Empty,
                RequestedTargetModuleName = requestedTargetName ?? targetName ?? string.Empty,
                TargetModuleExists = false,
                TargetCodeSha256 = string.Empty
            };
        }

        private static bool CanRenameComponent(VbaModuleState module)
        {
            if (module == null) return false;
            if (string.Equals(
                    module.ComponentType,
                    "StdModule",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    module.ComponentType,
                    "ClassModule",
                    StringComparison.OrdinalIgnoreCase)) return true;
            return string.Equals(
                    module.ComponentType,
                    "MSForm",
                    StringComparison.OrdinalIgnoreCase) &&
                module.CodeOnlyUserForm == true;
        }

        private static VbaRenameGuardPreparation RenameGuardFailure(
            VbaMutationOutcome error)
        {
            return new VbaRenameGuardPreparation { Error = error };
        }
    }
}
