using System;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Vba
{
    internal sealed partial class VbaMutationService
    {
        public ToolResult PrepareApplyPatchGuard(
            ToolCommand command,
            ChatSession session,
            string requestedModuleName)
        {
            var requestedName = (requestedModuleName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(requestedName))
            {
                return ToolResult.Fail("moduleName is required.", null, "vba_module_name_required", true);
            }

            string resolvedName;
            VbaModuleState current;
            ToolResult readError;
            if (!TryReadExistingModule(requestedName, out resolvedName, out current, out readError)) return readError;
            command.Arguments["moduleName"] = resolvedName;
            var currentHash = CodeSha256(current.Code);
            string observedHash;
            if (TryGetObservation(session, resolvedName, out observedHash) &&
                !string.Equals(observedHash, currentHash, StringComparison.OrdinalIgnoreCase))
            {
                RemoveObservation(session, resolvedName);
                return StaleSnapshot(resolvedName, true, observedHash, true, currentHash, "patch");
            }
            BindGuard(command, session, resolvedName, currentHash, requestedName);
            return null;
        }

        public static VbaMutationGuard ReadGuard(ToolCommand command)
        {
            try
            {
                return JsonConvert.DeserializeObject<VbaMutationGuard>(command == null ? null : command.RuntimeGuardJson);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private ToolResult ValidateApplyPatchGuard(
            ToolCommand command,
            ChatSession session,
            string moduleName,
            VbaModuleState current)
        {
            if (current == null)
            {
                return ToolResult.Fail("VBA module state is unavailable.", null, "vba_read_invalid", true);
            }
            if (string.IsNullOrWhiteSpace(command == null ? null : command.RuntimeGuardJson))
            {
                string observedHash;
                if (!TryGetObservation(session, moduleName, out observedHash)) return SnapshotRequired(moduleName);
                var actualHash = CodeSha256(current.Code);
                if (!string.Equals(observedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                {
                    RemoveObservation(session, moduleName);
                    return StaleSnapshot(moduleName, true, observedHash, true, actualHash, "editor");
                }
                BindGuard(command, session, moduleName, observedHash);
                return null;
            }

            var guard = ReadGuard(command);
            if (guard == null || guard.Version != 2 || string.IsNullOrWhiteSpace(guard.ModuleName))
            {
                return SnapshotRequired(moduleName);
            }
            if (!GuardContextMatches(guard, session, moduleName))
            {
                return ToolResult.Fail(
                    "The prepared VBA action belongs to another document, chat, or module. Retry the same tool in the current document.",
                    JsonConvert.SerializeObject(new { moduleName = moduleName, retrySameTool = true }),
                    "vba_snapshot_context_changed",
                    true);
            }
            var currentHash = CodeSha256(current.Code);
            if (!guard.ModuleExists ||
                !string.Equals(guard.CodeSha256, currentHash, StringComparison.OrdinalIgnoreCase))
            {
                RemoveObservation(session, moduleName);
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
            ChatSession session,
            string moduleName)
        {
            if (!string.Equals(guard.Host, _adapter.HostName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(guard.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase)) return false;
            var sessionId = session == null ? string.Empty : session.Id ?? string.Empty;
            if (!string.Equals(guard.SessionId ?? string.Empty, sessionId, StringComparison.OrdinalIgnoreCase)) return false;
            var documentKey = _adapter.DocumentKey ?? string.Empty;
            if (OfficeDocumentExecutionGuardState.IdentityMatches(
                guard.DocumentKey,
                string.Empty,
                documentKey,
                string.Empty)) return true;
            var runtimeKey = _adapter.RuntimeDocumentKey ?? string.Empty;
            return OfficeDocumentExecutionGuardState.IdentityMatches(
                string.Empty,
                guard.RuntimeDocumentKey,
                string.Empty,
                runtimeKey);
        }

        private void BindGuard(
            ToolCommand command,
            ChatSession session,
            string moduleName,
            string hash,
            string requestedModuleName = null)
        {
            if (command == null) return;
            command.RuntimeGuardJson = JsonConvert.SerializeObject(new VbaMutationGuard
            {
                Version = 2,
                Host = _adapter.HostName ?? string.Empty,
                DocumentKey = _adapter.DocumentKey ?? string.Empty,
                RuntimeDocumentKey = _adapter.RuntimeDocumentKey ?? string.Empty,
                SessionId = session == null ? string.Empty : session.Id ?? string.Empty,
                RunId = session == null || session.LastRun == null ? null : session.LastRun.RunId,
                TurnId = session == null || session.LastRun == null ? null : session.LastRun.TurnId,
                StepId = command.RuntimeStepId,
                ToolCallId = command.ToolCallId,
                ModuleName = moduleName ?? string.Empty,
                RequestedModuleName = string.IsNullOrWhiteSpace(requestedModuleName)
                    ? moduleName ?? string.Empty
                    : requestedModuleName,
                ModuleExists = true,
                CodeSha256 = hash ?? string.Empty
            });
        }

        private ToolResult SnapshotRequired(string moduleName)
        {
            return ToolResult.Fail(
                "The internal VBA snapshot is missing. Retry the same public VBA tool, or reload the VBA editor before retrying an editor save.",
                JsonConvert.SerializeObject(new
                {
                    moduleName = moduleName ?? string.Empty,
                    retrySameTool = true,
                    reloadEditor = true
                }),
                "vba_internal_snapshot_missing",
                true);
        }

        private ToolResult StaleSnapshot(
            string moduleName,
            bool observedExists,
            string observedHash,
            bool actualExists,
            string actualHash,
            string operation)
        {
            var editor = string.Equals(operation, "editor", StringComparison.OrdinalIgnoreCase);
            var message = editor
                ? "The VBA module changed after it was loaded in the editor. Reload it and reconcile the changes before saving."
                : "The VBA module changed after this action was prepared. Retry the same tool so runtime can bind the current state; read it only if the intended action may no longer match.";
            return ToolResult.Fail(
                message,
                JsonConvert.SerializeObject(new
                {
                    moduleName = moduleName ?? string.Empty,
                    observedExists = observedExists,
                    observedCodeSha256 = string.IsNullOrWhiteSpace(observedHash) ? null : observedHash,
                    actualExists = actualExists,
                    actualCodeSha256 = string.IsNullOrWhiteSpace(actualHash) ? null : actualHash,
                    retrySameTool = !editor,
                    reloadEditor = editor,
                    reconcileBeforeOverwrite = false,
                    inspectTool = "common.resources_read",
                    resourceProvider = VbaResourceProvider.ProviderName,
                    resourceKind = VbaResourceProvider.ComponentKind
                }),
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
        public string TargetModuleName { get; set; }
        public string RequestedTargetModuleName { get; set; }
        public bool TargetModuleExists { get; set; }
        public string TargetCodeSha256 { get; set; }
    }
}
