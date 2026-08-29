using System;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RNAssistant.Office.Vba;

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
                string.Equals(toolId, ToolId("office_run_macro"), StringComparison.OrdinalIgnoreCase) ||
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
            if (_reader.TryReadModule(requestedName, 1000000, out existing, out readError))
            {
                var existingName = string.IsNullOrWhiteSpace(existing.Name) ? requestedName : existing.Name;
                command.Arguments["moduleName"] = existingName;
                return BindWriteGuard(command, session, existingName, existing, requestedName);
            }
            if (!VbaReader.IsModuleNotFound(readError)) return readError;

            var normalizedName = VbaReader.NormalizeModuleName(requestedName);
            if (!string.Equals(requestedName, normalizedName, StringComparison.OrdinalIgnoreCase) &&
                _reader.TryReadModule(normalizedName, 1000000, out existing, out readError))
            {
                var existingName = string.IsNullOrWhiteSpace(existing.Name) ? normalizedName : existing.Name;
                command.Arguments["moduleName"] = existingName;
                return BindWriteGuard(command, session, existingName, existing, requestedName);
            }
            if (!VbaReader.IsModuleNotFound(readError)) return readError;

            command.Arguments["moduleName"] = normalizedName;
            BindGuard(command, session, normalizedName, false, null, requestedName);
            return null;
        }

        private ToolResult PrepareRenameGuard(
            ToolCommand command,
            ChatSession session,
            string moduleName,
            string newModuleName)
        {
            if (string.IsNullOrWhiteSpace(moduleName) || string.IsNullOrWhiteSpace(newModuleName))
            {
                return ToolResult.Fail(
                    "moduleName and newModuleName are required for mode=rename.",
                    null,
                    "vba_module_name_required",
                    true);
            }

            var requestedSourceName = moduleName.Trim();
            string resolvedSourceName;
            VbaModuleState source;
            ToolResult sourceError;
            if (!TryReadExistingModule(requestedSourceName, out resolvedSourceName, out source, out sourceError))
            {
                return sourceError;
            }
            if (!CanRenameComponent(source))
            {
                return ToolResult.Fail(
                    "Only StdModule, ClassModule, and blank code-only MSForm components can be renamed through RNAssistant.",
                    JsonConvert.SerializeObject(new { moduleName = resolvedSourceName, componentType = source.ComponentType }),
                    "vba_component_type_read_only",
                    false);
            }

            var requestedTargetName = newModuleName.Trim();
            var normalizedTargetName = VbaReader.NormalizeModuleName(requestedTargetName);
            if (string.Equals(resolvedSourceName, normalizedTargetName, StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Fail(
                    "The rename destination resolves to the current VBA component name.",
                    JsonConvert.SerializeObject(new
                    {
                        moduleName = resolvedSourceName,
                        requestedNewModuleName = requestedTargetName,
                        normalizedNewModuleName = normalizedTargetName
                    }),
                    "vba_rename_noop",
                    true);
            }

            VbaModuleState target;
            ToolResult targetError;
            if (_reader.TryReadModule(normalizedTargetName, 1000000, out target, out targetError))
            {
                return ToolResult.Fail(
                    "VBA rename destination already exists: " + normalizedTargetName + ".",
                    JsonConvert.SerializeObject(new
                    {
                        moduleName = resolvedSourceName,
                        newModuleName = normalizedTargetName,
                        targetComponentType = target.ComponentType
                    }),
                    "vba_module_exists",
                    true);
            }
            if (!VbaReader.IsModuleNotFound(targetError)) return targetError;

            var sourceHash = CodeSha256(source.Code);
            string observedHash;
            if (TryGetObservation(session, resolvedSourceName, out observedHash) &&
                !string.Equals(observedHash, sourceHash, StringComparison.OrdinalIgnoreCase))
            {
                RemoveObservation(session, resolvedSourceName);
                return StaleSnapshot(resolvedSourceName, true, observedHash, true, sourceHash, "rename");
            }

            command.Arguments["moduleName"] = resolvedSourceName;
            command.Arguments["newModuleName"] = normalizedTargetName;
            BindRenameGuard(
                command,
                session,
                resolvedSourceName,
                requestedSourceName,
                sourceHash,
                normalizedTargetName,
                requestedTargetName);
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
            if (_reader.TryReadModule(moduleName, 1000000, out current, out readError))
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
            if (!VbaReader.IsModuleNotFound(readError)) return readError;
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

        private ToolResult ValidateRenameGuard(
            ToolCommand command,
            ChatSession session,
            string moduleName,
            bool sourceExists,
            VbaModuleState source,
            string newModuleName,
            bool targetExists,
            VbaModuleState target)
        {
            var guard = ReadGuard(command);
            if (guard == null || guard.Version != 3 || string.IsNullOrWhiteSpace(guard.ModuleName) ||
                string.IsNullOrWhiteSpace(guard.TargetModuleName))
            {
                return SnapshotRequired(moduleName);
            }
            if (!GuardContextMatches(guard, session, moduleName) ||
                !string.Equals(guard.TargetModuleName, newModuleName, StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Fail(
                    "The prepared VBA rename belongs to another document, chat, source, or destination. Retry it in the current document.",
                    JsonConvert.SerializeObject(new
                    {
                        moduleName = moduleName,
                        newModuleName = newModuleName,
                        retrySameTool = true
                    }),
                    "vba_snapshot_context_changed",
                    true);
            }

            var sourceHash = sourceExists && source != null ? CodeSha256(source.Code) : null;
            var targetHash = targetExists && target != null ? CodeSha256(target.Code) : null;
            if (guard.ModuleExists != sourceExists ||
                sourceExists && !string.Equals(guard.CodeSha256, sourceHash, StringComparison.OrdinalIgnoreCase) ||
                guard.TargetModuleExists != targetExists ||
                targetExists && !string.Equals(guard.TargetCodeSha256, targetHash, StringComparison.OrdinalIgnoreCase))
            {
                RemoveObservation(session, moduleName);
                return ToolResult.Fail(
                    "The VBA source or rename destination changed after confirmation was prepared. The rename was not applied; retry it against current state.",
                    JsonConvert.SerializeObject(new
                    {
                        moduleName = moduleName,
                        newModuleName = newModuleName,
                        expectedSourceExists = guard.ModuleExists,
                        actualSourceExists = sourceExists,
                        expectedSourceCodeSha256 = guard.CodeSha256,
                        actualSourceCodeSha256 = sourceHash,
                        expectedTargetExists = guard.TargetModuleExists,
                        actualTargetExists = targetExists,
                        actualTargetCodeSha256 = targetHash,
                        retrySameTool = true,
                        inspectTool = "common.resources_read",
                        resourceProvider = VbaResourceProvider.ProviderName,
                        resourceKind = VbaResourceProvider.ComponentKind
                    }),
                    "stale_vba_module",
                    true);
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

        private void BindRenameGuard(
            ToolCommand command,
            ChatSession session,
            string moduleName,
            string requestedModuleName,
            string sourceHash,
            string targetModuleName,
            string requestedTargetModuleName)
        {
            if (command == null) return;
            command.RuntimeGuardJson = JsonConvert.SerializeObject(new VbaMutationGuard
            {
                Version = 3,
                Host = _adapter.HostName ?? string.Empty,
                DocumentKey = _adapter.DocumentKey ?? string.Empty,
                RuntimeDocumentKey = _adapter.RuntimeDocumentKey ?? string.Empty,
                SessionId = session == null ? string.Empty : session.Id ?? string.Empty,
                RunId = session == null || session.LastRun == null ? null : session.LastRun.RunId,
                TurnId = session == null || session.LastRun == null ? null : session.LastRun.TurnId,
                StepId = command.RuntimeStepId,
                ToolCallId = command.ToolCallId,
                ModuleName = moduleName ?? string.Empty,
                RequestedModuleName = requestedModuleName ?? moduleName ?? string.Empty,
                ModuleExists = true,
                CodeSha256 = sourceHash ?? string.Empty,
                TargetModuleName = targetModuleName ?? string.Empty,
                RequestedTargetModuleName = requestedTargetModuleName ?? targetModuleName ?? string.Empty,
                TargetModuleExists = false,
                TargetCodeSha256 = string.Empty
            });
        }

        private static bool CanRenameComponent(VbaModuleState module)
        {
            if (module == null) return false;
            if (string.Equals(module.ComponentType, "StdModule", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(module.ComponentType, "ClassModule", StringComparison.OrdinalIgnoreCase)) return true;
            return string.Equals(module.ComponentType, "MSForm", StringComparison.OrdinalIgnoreCase) &&
                module.CodeOnlyUserForm == true;
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
                    inspectTool = "common.resources_read",
                    resourceProvider = VbaResourceProvider.ProviderName,
                    resourceKind = VbaResourceProvider.ComponentKind
                }),
                "stale_vba_module",
                true);
        }

        private void RecordObservationFromModule(
            ChatSession session,
            string moduleName,
            VbaModuleState module)
        {
            if (module == null || string.IsNullOrWhiteSpace(module.CodeSha256)) return;
            RecordObservation(
                session,
                string.IsNullOrWhiteSpace(module.Name) ? moduleName : module.Name,
                module.CodeSha256);
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
            return VbaTextCanonicalizer.LiveCodeSha256(code);
        }

    }
}
