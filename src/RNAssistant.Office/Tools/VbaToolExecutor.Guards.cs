using System;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class VbaToolExecutor
    {
        private bool IsPublicMutation(string toolId)
        {
            return string.Equals(toolId, ToolId("vba_write_module"), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, ToolId("vba_apply_patch"), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, ToolId("vba_delete_module"), StringComparison.OrdinalIgnoreCase);
        }

        private bool IsExistingModuleMutation(string toolId)
        {
            return string.Equals(toolId, ToolId("vba_apply_patch"), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, ToolId("vba_delete_module"), StringComparison.OrdinalIgnoreCase);
        }

        private bool IsPreflightMutation(string toolId)
        {
            return IsPublicMutation(toolId) ||
                string.Equals(toolId, ToolId("vba_restore_backup"), StringComparison.OrdinalIgnoreCase);
        }

        private ToolResult PrepareExistingModuleGuard(ToolCommand command, ChatSession session, string moduleName)
        {
            if (string.IsNullOrWhiteSpace(moduleName)) return ToolResult.Fail("moduleName is required.", null, "vba_module_name_required", true);
            string resolvedName;
            VbaModuleState current;
            ToolResult readError;
            if (!TryReadExistingModule(moduleName, out resolvedName, out current, out readError)) return readError;
            command.Arguments["moduleName"] = resolvedName;
            var currentHash = CodeSha256(current.Code);
            string observedHash;
            if (TryGetObservation(session, resolvedName, out observedHash) &&
                !string.Equals(observedHash, currentHash, StringComparison.OrdinalIgnoreCase))
            {
                RemoveObservation(session, resolvedName);
                var operation = string.Equals(command.ToolId, ToolId("vba_apply_patch"), StringComparison.OrdinalIgnoreCase)
                    ? "patch"
                    : "mutation";
                return StaleSnapshot(resolvedName, true, observedHash, true, currentHash, operation);
            }
            BindGuard(command, session, resolvedName, true, currentHash, moduleName);
            return null;
        }

        private ToolResult PrepareWriteGuard(ToolCommand command, ChatSession session, string moduleName)
        {
            if (string.IsNullOrWhiteSpace(moduleName)) return ToolResult.Fail("moduleName is required.", null, "vba_module_name_required", true);
            var requestedName = moduleName.Trim();
            VbaModuleState existing;
            ToolResult readError;
            if (TryReadVbaModule(requestedName, 1000000, out existing, out readError))
            {
                var existingName = string.IsNullOrWhiteSpace(existing.Name) ? requestedName : existing.Name;
                command.Arguments["moduleName"] = existingName;
                return BindWriteGuard(command, session, existingName, existing, requestedName);
            }
            if (!IsModuleNotFound(readError)) return readError;

            var normalizedName = NormalizeModuleName(requestedName);
            if (!string.Equals(requestedName, normalizedName, StringComparison.OrdinalIgnoreCase) &&
                TryReadVbaModule(normalizedName, 1000000, out existing, out readError))
            {
                var existingName = string.IsNullOrWhiteSpace(existing.Name) ? normalizedName : existing.Name;
                command.Arguments["moduleName"] = existingName;
                return BindWriteGuard(command, session, existingName, existing, requestedName);
            }
            if (!IsModuleNotFound(readError)) return readError;

            command.Arguments["moduleName"] = normalizedName;
            BindGuard(command, session, normalizedName, false, null, requestedName);
            return null;
        }

        private ToolResult BindWriteGuard(
            ToolCommand command,
            ChatSession session,
            string moduleName,
            VbaModuleState existing,
            string requestedName)
        {
            var currentHash = CodeSha256(existing == null ? string.Empty : existing.Code);
            string observedHash;
            if (TryGetObservation(session, moduleName, out observedHash) &&
                !string.Equals(observedHash, currentHash, StringComparison.OrdinalIgnoreCase))
            {
                RemoveObservation(session, moduleName);
                return StaleSnapshot(moduleName, true, observedHash, true, currentHash, "write");
            }
            BindGuard(command, session, moduleName, true, currentHash, requestedName);
            return null;
        }

        private ToolResult PrepareCurrentModuleGuard(ToolCommand command, ChatSession session, string moduleName, string expectedComponentType)
        {
            VbaModuleState current;
            ToolResult readError;
            if (TryReadVbaModule(moduleName, 1000000, out current, out readError))
            {
                if (!string.IsNullOrWhiteSpace(expectedComponentType) &&
                    !string.Equals(expectedComponentType, current.ComponentType, StringComparison.OrdinalIgnoreCase))
                {
                    return ToolResult.Fail(
                        "VBA restore was blocked because the current component type differs from the backup.",
                        JsonConvert.SerializeObject(new { moduleName = moduleName, backupType = expectedComponentType, currentType = current.ComponentType }),
                        "vba_restore_component_type_mismatch",
                        false);
                }
                BindGuard(command, session, moduleName, true, CodeSha256(current.Code), moduleName);
                return null;
            }
            if (!IsModuleNotFound(readError)) return readError;
            BindGuard(command, session, moduleName, false, null, moduleName);
            return null;
        }

        private ToolResult ValidateExistingModuleGuard(ToolCommand command, ChatSession session, string moduleName, VbaModuleState current)
        {
            if (current == null) return ToolResult.Fail("VBA module state is unavailable.", null, "vba_read_invalid", true);
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
                BindGuard(command, session, moduleName, true, observedHash);
                return null;
            }
            return ValidateModuleGuard(command, session, moduleName, true, current);
        }

        private ToolResult ValidateModuleGuard(
            ToolCommand command,
            ChatSession session,
            string moduleName,
            bool moduleExists,
            VbaModuleState current)
        {
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
            var actualHash = moduleExists && current != null ? CodeSha256(current.Code) : null;
            if (guard.ModuleExists != moduleExists ||
                moduleExists && !string.Equals(guard.CodeSha256, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                RemoveObservation(session, moduleName);
                var operation = string.Equals(command.ToolId, ToolId("vba_write_module"), StringComparison.OrdinalIgnoreCase)
                    ? "write"
                    : string.Equals(command.ToolId, ToolId("vba_apply_patch"), StringComparison.OrdinalIgnoreCase)
                        ? "patch"
                        : "mutation";
                return StaleSnapshot(moduleName, guard.ModuleExists, guard.CodeSha256, moduleExists, actualHash, operation);
            }
            return null;
        }

        private bool GuardContextMatches(VbaMutationGuard guard, ChatSession session, string moduleName)
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

        private static VbaMutationGuard ReadGuard(ToolCommand command)
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

        private void BindGuard(
            ToolCommand command,
            ChatSession session,
            string moduleName,
            bool moduleExists,
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
                RequestedModuleName = string.IsNullOrWhiteSpace(requestedModuleName) ? moduleName ?? string.Empty : requestedModuleName,
                ModuleExists = moduleExists,
                CodeSha256 = moduleExists ? hash ?? string.Empty : string.Empty
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
            var wholeWrite = string.Equals(operation, "write", StringComparison.OrdinalIgnoreCase);
            var message = editor
                ? "The VBA module changed after it was loaded in the editor. Reload it and reconcile the changes before saving."
                : wholeWrite
                    ? "The VBA module changed after the source was inspected or this write was prepared. Re-read and reconcile if the complete source was derived from that version; retry the same write only for an intentional complete overwrite."
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
                    reconcileBeforeOverwrite = wholeWrite,
                    inspectTool = ToolId("vba_read_module")
                }),
                "stale_vba_module",
                true);
        }

        private void RecordObservationFromRead(ChatSession session, string moduleName, ToolResult result)
        {
            if (result == null || !result.Success || string.IsNullOrWhiteSpace(result.DataJson)) return;
            try
            {
                var data = JObject.Parse(result.DataJson);
                var hash = (string)data["codeSha256"];
                var actualName = (string)data["name"] ?? moduleName;
                if (!string.IsNullOrWhiteSpace(hash)) RecordObservation(session, actualName, hash);
            }
            catch (JsonException) { }
        }

        private void RecordObservation(ChatSession session, string moduleName, string hash)
        {
            if (string.IsNullOrWhiteSpace(moduleName) || string.IsNullOrWhiteSpace(hash)) return;
            var key = ObservationKey(session, moduleName);
            lock (_observedModulesSync)
            {
                if (_observedModuleHashes.Count >= 1024 && !_observedModuleHashes.ContainsKey(key))
                {
                    _observedModuleHashes.Clear();
                }
                _observedModuleHashes[key] = hash;
            }
        }

        private bool TryGetObservation(ChatSession session, string moduleName, out string hash)
        {
            lock (_observedModulesSync)
            {
                return _observedModuleHashes.TryGetValue(ObservationKey(session, moduleName), out hash);
            }
        }

        private void RemoveObservation(ChatSession session, string moduleName)
        {
            lock (_observedModulesSync)
            {
                _observedModuleHashes.Remove(ObservationKey(session, moduleName));
            }
        }

        private string ObservationKey(ChatSession session, string moduleName)
        {
            var runtimeKey = _adapter.RuntimeDocumentKey ?? string.Empty;
            var documentIdentity = string.IsNullOrWhiteSpace(runtimeKey)
                ? "document:" + (_adapter.DocumentKey ?? string.Empty)
                : "runtime:" + runtimeKey;
            return (session == null ? string.Empty : session.Id ?? string.Empty) + "|" +
                (_adapter.HostName ?? string.Empty) + "|" + documentIdentity + "|" + (moduleName ?? string.Empty);
        }

        internal static string CodeSha256(string code)
        {
            return VbaToolManifestParser.LiveCodeSha256(code);
        }

        private static bool IsModuleNotFound(ToolResult result)
        {
            return result != null &&
                (string.Equals(result.ErrorCode, "vba_module_not_found", StringComparison.OrdinalIgnoreCase) ||
                 (result.Message ?? string.Empty).IndexOf("VBA module not found", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string NormalizeModuleName(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (VbaToolManifestParser.ValidComponentName(value)) return value;

            var normalized = new StringBuilder();
            foreach (var character in value)
            {
                var valid = character >= 'A' && character <= 'Z' ||
                    character >= 'a' && character <= 'z' ||
                    character >= '0' && character <= '9' ||
                    character == '_';
                if (valid)
                {
                    normalized.Append(character);
                }
                else if (normalized.Length > 0 && normalized[normalized.Length - 1] != '_')
                {
                    normalized.Append('_');
                }
            }

            var candidate = normalized.ToString().Trim('_');
            if (string.IsNullOrWhiteSpace(candidate)) candidate = "Module";
            if (!IsAsciiLetter(candidate[0])) candidate = "Module_" + candidate;
            if (string.IsNullOrWhiteSpace(candidate) || !IsAsciiLetter(candidate[0])) candidate = "Module";
            var suffix = "_" + TextPatternEngine.Sha256(value).Substring(0, 8);
            var maxBaseLength = 31 - suffix.Length;
            if (candidate.Length > maxBaseLength) candidate = candidate.Substring(0, maxBaseLength).TrimEnd('_');
            if (string.IsNullOrWhiteSpace(candidate)) candidate = "Module";
            return candidate + suffix;
        }

        private static bool IsAsciiLetter(char value)
        {
            return value >= 'A' && value <= 'Z' || value >= 'a' && value <= 'z';
        }

    }
}
