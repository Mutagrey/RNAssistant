using System;
using Newtonsoft.Json.Linq;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Vba
{
    internal sealed partial class VbaMutationService
    {
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

        private static VbaMutationOutcome SnapshotRefreshRequired(string moduleName)
        {
            return VbaMutationOutcome.Error(
                "Whole-module replacement requires the complete current source in model context. That observation is missing or stale; it may have been omitted by the context budget. This is a source-observation guard, not a VBA access denial. Read the complete current source with core.read and reconcile before replacing; diagnostic currentContent in this error is not a resource-read observation. If the complete source does not fit, read the relevant source lines with startLine/lineCount and use core.patch for a focused edit. Do not repeat an unchanged whole-source read/write loop or create another module to bypass this guard.",
                new JObject
                {
                    ["moduleName"] = moduleName ?? string.Empty,
                    ["retrySameTool"] = false,
                    ["retryAfterRefresh"] = true,
                    ["inspectTool"] = "core.read",
                    ["discoveryScope"] = "vba",
                    ["completeSourceRequired"] = true,
                    ["requiredRepresentation"] = "source"
                },
                "vba_snapshot_refresh_required",
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
                    ? "The VBA module changed after the source was inspected or this write was prepared. Obtain its complete current source and reconcile before replacing it. If the complete source does not fit in context, read the relevant source lines with startLine/lineCount and use core.patch. This conflict does not mean VBA access was denied."
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
                    ["retrySameTool"] = !editor && !wholeWrite,
                    ["retryAfterRefresh"] = wholeWrite,
                    ["reloadEditor"] = editor,
                    ["reconcileBeforeOverwrite"] = wholeWrite,
                    ["inspectTool"] = "core.read",
                    ["discoveryScope"] = "vba",
                    ["completeSourceRequired"] = wholeWrite,
                    ["requiredRepresentation"] = wholeWrite ? "source" : null
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
