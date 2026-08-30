using System;
using Newtonsoft.Json.Linq;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Vba
{
    internal sealed partial class VbaMutationService
    {
        public VbaApplyPatchGuardPreparation PrepareApplyPatchGuard(
            VbaApplyPatchGuardRequest request)
        {
            var requestedName = (request == null ? null : request.RequestedModuleName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(requestedName))
            {
                return new VbaApplyPatchGuardPreparation
                {
                    Error = VbaMutationOutcome.Error(
                        "moduleName is required.",
                        null,
                        "vba_module_name_required",
                        true)
                };
            }

            string resolvedName;
            VbaModuleState current;
            var readError = TryReadExistingModule(requestedName, out resolvedName, out current);
            if (readError != null)
            {
                return new VbaApplyPatchGuardPreparation { Error = readError };
            }

            var correlation = request.Correlation ?? new VbaMutationCorrelation();
            var currentHash = CodeSha256(current.Code);
            string observedHash;
            if (TryGetObservation(correlation.SessionId, resolvedName, out observedHash) &&
                !string.Equals(observedHash, currentHash, StringComparison.OrdinalIgnoreCase))
            {
                RemoveObservation(correlation.SessionId, resolvedName);
                return new VbaApplyPatchGuardPreparation
                {
                    Error = StaleSnapshot(
                        resolvedName,
                        true,
                        observedHash,
                        true,
                        currentHash,
                        "patch")
                };
            }

            return new VbaApplyPatchGuardPreparation
            {
                ResolvedModuleName = resolvedName,
                Guard = CreateGuard(
                    correlation,
                    resolvedName,
                    currentHash,
                    requestedName)
            };
        }

        private VbaMutationOutcome ValidateApplyPatchGuard(
            VbaApplyPatchRequest request,
            string moduleName,
            VbaModuleState current)
        {
            if (current == null)
            {
                return VbaMutationOutcome.Error(
                    "VBA module state is unavailable.",
                    null,
                    "vba_read_invalid",
                    true);
            }

            var correlation = request.Correlation ?? new VbaMutationCorrelation();
            var guard = request.Guard;
            if (guard == null)
            {
                string observedHash;
                if (!TryGetObservation(correlation.SessionId, moduleName, out observedHash))
                {
                    return SnapshotRequired(moduleName);
                }
                var actualHash = CodeSha256(current.Code);
                if (!string.Equals(observedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                {
                    RemoveObservation(correlation.SessionId, moduleName);
                    return StaleSnapshot(
                        moduleName,
                        true,
                        observedHash,
                        true,
                        actualHash,
                        "editor");
                }
                request.Guard = CreateGuard(correlation, moduleName, observedHash, moduleName);
                return null;
            }

            if (guard.Version != 2 || string.IsNullOrWhiteSpace(guard.ModuleName))
            {
                return SnapshotRequired(moduleName);
            }
            if (!GuardContextMatches(guard, correlation, moduleName))
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

            var currentHash = CodeSha256(current.Code);
            if (!guard.ModuleExists ||
                !string.Equals(guard.CodeSha256, currentHash, StringComparison.OrdinalIgnoreCase))
            {
                RemoveObservation(correlation.SessionId, moduleName);
                return StaleSnapshot(
                    moduleName,
                    guard.ModuleExists,
                    guard.CodeSha256,
                    true,
                    currentHash,
                    "patch");
            }
            return null;
        }

        private bool GuardContextMatches(
            VbaMutationGuard guard,
            VbaMutationCorrelation correlation,
            string moduleName)
        {
            if (!string.Equals(guard.Host, _document.HostName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(guard.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase)) return false;
            var sessionId = correlation == null ? string.Empty : correlation.SessionId ?? string.Empty;
            if (!string.Equals(guard.SessionId ?? string.Empty, sessionId, StringComparison.OrdinalIgnoreCase)) return false;

            var documentKey = _document.DocumentKey ?? string.Empty;
            if (OfficeDocumentExecutionGuardState.IdentityMatches(
                guard.DocumentKey,
                string.Empty,
                documentKey,
                string.Empty)) return true;
            var runtimeKey = _document.RuntimeDocumentKey ?? string.Empty;
            return OfficeDocumentExecutionGuardState.IdentityMatches(
                string.Empty,
                guard.RuntimeDocumentKey,
                string.Empty,
                runtimeKey);
        }

        private VbaMutationGuard CreateGuard(
            VbaMutationCorrelation correlation,
            string moduleName,
            string hash,
            string requestedModuleName,
            bool moduleExists = true)
        {
            correlation = correlation ?? new VbaMutationCorrelation();
            return new VbaMutationGuard
            {
                Version = 2,
                Host = _document.HostName ?? string.Empty,
                DocumentKey = _document.DocumentKey ?? string.Empty,
                RuntimeDocumentKey = _document.RuntimeDocumentKey ?? string.Empty,
                SessionId = correlation.SessionId ?? string.Empty,
                RunId = correlation.RunId,
                TurnId = correlation.TurnId,
                StepId = correlation.StepId,
                ToolCallId = correlation.ToolCallId,
                ModuleName = moduleName ?? string.Empty,
                RequestedModuleName = string.IsNullOrWhiteSpace(requestedModuleName)
                    ? moduleName ?? string.Empty
                    : requestedModuleName,
                ModuleExists = moduleExists,
                CodeSha256 = moduleExists ? hash ?? string.Empty : string.Empty
            };
        }

        private static VbaMutationOutcome SnapshotRequired(string moduleName)
        {
            return VbaMutationOutcome.Error(
                "The internal VBA snapshot is missing. Retry the same public VBA tool, or reload the VBA editor before retrying an editor save.",
                new JObject
                {
                    ["moduleName"] = moduleName ?? string.Empty,
                    ["retrySameTool"] = true,
                    ["reloadEditor"] = true
                },
                "vba_internal_snapshot_missing",
                true);
        }

        private static VbaMutationOutcome StaleSnapshot(
            string moduleName,
            bool observedExists,
            string observedHash,
            bool actualExists,
            string actualHash,
            string operation)
        {
            var editor = string.Equals(operation, "editor", StringComparison.OrdinalIgnoreCase);
            var wholeWrite = string.Equals(operation, "write", StringComparison.OrdinalIgnoreCase);
            var message = editor
                ? "The VBA module changed after it was loaded in the editor. Reload it and reconcile the changes before saving."
                : wholeWrite
                    ? "The VBA module changed after the source was inspected or this write was prepared. Re-read and reconcile if the complete source was derived from that version; retry the same write only for an intentional complete overwrite."
                    : "The VBA module changed after this action was prepared. Retry the same tool so runtime can bind the current state; read it only if the intended action may no longer match.";
            return VbaMutationOutcome.Error(
                message,
                new JObject
                {
                    ["moduleName"] = moduleName ?? string.Empty,
                    ["observedExists"] = observedExists,
                    ["observedCodeSha256"] = string.IsNullOrWhiteSpace(observedHash) ? null : observedHash,
                    ["actualExists"] = actualExists,
                    ["actualCodeSha256"] = string.IsNullOrWhiteSpace(actualHash) ? null : actualHash,
                    ["retrySameTool"] = !editor,
                    ["reloadEditor"] = editor,
                    ["reconcileBeforeOverwrite"] = wholeWrite,
                    ["inspectTool"] = "common.resources_read",
                    ["resourceProvider"] = VbaResourceProvider.ProviderName,
                    ["resourceKind"] = VbaResourceProvider.ComponentKind
                },
                "stale_vba_module",
                true);
        }
    }

    internal sealed class VbaMutationGuard
    {
        public int Version { get; set; }
        public string Host { get; set; }
        public string DocumentKey { get; set; }
        public string RuntimeDocumentKey { get; set; }
        public string SessionId { get; set; }
        public string RunId { get; set; }
        public string TurnId { get; set; }
        public string StepId { get; set; }
        public string ToolCallId { get; set; }
        public string ModuleName { get; set; }
        public string RequestedModuleName { get; set; }
        public bool ModuleExists { get; set; }
        public string CodeSha256 { get; set; }
        public string ComponentType { get; set; }
        public bool? CodeOnlyUserForm { get; set; }
        public string TargetModuleName { get; set; }
        public string RequestedTargetModuleName { get; set; }
        public bool TargetModuleExists { get; set; }
        public string TargetCodeSha256 { get; set; }
    }
}
