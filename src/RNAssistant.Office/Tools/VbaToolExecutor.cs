using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class VbaToolExecutor : IVbaResourceSource
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly VbaJournalStore _vbaJournalStore;
        private readonly object _observedModulesSync = new object();
        private readonly Dictionary<string, string> _observedModuleHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public VbaToolExecutor(IOfficeApplicationAdapter adapter, VbaJournalStore vbaJournalStore)
        {
            _adapter = adapter;
            _vbaJournalStore = vbaJournalStore;
        }

        public IEnumerable<ToolDefinition> GetControllerTools()
        {
            if (!HostSupportsVba())
            {
                yield break;
            }

            yield return ControllerToolDefinition.Create(ToolId("vba_restore_backup"), "Common", "Mutates document: Restore a VBA module from an exact backupId, or restore the latest backup for moduleName when backupId is omitted. Runtime snapshots current state before confirmation.", RestoreBackupSchema(), mutatesDocument: true, agentCanRun: true, requiresConfirmation: true, riskLevel: 3);
            yield return ControllerToolDefinition.Create(ToolId("vba_write_module"), "Common", "Mutates document with two strict branches. Whole-source write requires moduleName+code and uses mode=upsert/createOnly/updateOnly; componentType applies only on creation. Atomic rename requires moduleName+newModuleName+mode=rename and accepts no code/componentType. Runtime guards both names, normalizes a new destination, rejects collisions, journals both identities, and verifies read-back. Rename preserves the component but does not rewrite textual references to its old name.", WriteModuleSchema(), mutatesDocument: true, agentCanRun: true, requiresConfirmation: true, riskLevel: 3);
            yield return ControllerToolDefinition.Create(ToolId("vba_apply_patch"), "Common", "Mutates document: Apply ordered exact unique source-block replacements to an existing VBA component. There are no line-number, fuzzy, first-match, regex, or implicit insertion modes. Runtime patches one current full-module snapshot in memory, then performs one guarded whole-module write. Exact replacements already satisfied are skipped; an all-no-op patch succeeds without writing. Use common.vba_write_module with complete source when the module is missing.", ApplyPatchSchema(), mutatesDocument: true, agentCanRun: true, requiresConfirmation: true, riskLevel: 3);
            yield return ControllerToolDefinition.Create(ToolId("vba_delete_module"), "Common", "Mutates document: Delete an existing StdModule or ClassModule. Runtime reads it, validates the type, and creates a rollback backup; no separate read call is required. Document modules and UserForms are not deleted.", ModuleNameSchema(), mutatesDocument: true, agentCanRun: true, requiresConfirmation: true, riskLevel: 3);
        }

        public string ToolId(string suffix)
        {
            return "common." + suffix;
        }

        public string BackendToolId(string suffix)
        {
            return HostToolPrefix() + "." + suffix;
        }

        internal bool IsInternalToolId(string id)
        {
            return !string.IsNullOrWhiteSpace(id) &&
                id.StartsWith(BackendToolId("vba_"), StringComparison.OrdinalIgnoreCase);
        }

        public ToolResult ExecuteControllerTool(ToolCommand command, bool dryRun, ChatSession session, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!dryRun)
            {
                var reconciliationError = ReconcilePendingMutations();
                if (reconciliationError != null) return reconciliationError;
            }
            if (string.Equals(command.ToolId, ToolId("vba_restore_backup"), StringComparison.OrdinalIgnoreCase))
            {
                return RestoreVbaBackup(command, dryRun, session, cancellationToken);
            }

            if (string.Equals(command.ToolId, ToolId("vba_apply_patch"), StringComparison.OrdinalIgnoreCase))
            {
                return ApplyVbaPatch(command, dryRun, session, cancellationToken);
            }

            if (string.Equals(command.ToolId, ToolId("vba_write_module"), StringComparison.OrdinalIgnoreCase)) return WriteVbaModule(command, dryRun, session);
            if (string.Equals(command.ToolId, ToolId("vba_delete_module"), StringComparison.OrdinalIgnoreCase)) return DeleteModule(command, dryRun, session);

            return ToolResult.Fail("Unknown VBA controller tool: " + command.ToolId);
        }

        public ToolResult PrepareControllerTool(ToolCommand command, ChatSession session)
        {
            if (command == null || !string.IsNullOrWhiteSpace(command.RuntimeGuardJson)) return null;
            var moduleName = ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty);
            if (IsExistingModuleMutation(command.ToolId))
            {
                return PrepareExistingModuleGuard(command, session, moduleName);
            }
            if (string.Equals(command.ToolId, ToolId("vba_write_module"), StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(ToolArgumentReader.String(command.Arguments, "mode", "upsert"), "rename", StringComparison.OrdinalIgnoreCase))
                {
                    return PrepareRenameGuard(
                        command,
                        session,
                        moduleName,
                        ToolArgumentReader.String(command.Arguments, "newModuleName", string.Empty));
                }
                return PrepareWriteGuard(command, session, moduleName);
            }
            if (string.Equals(command.ToolId, ToolId("vba_restore_backup"), StringComparison.OrdinalIgnoreCase))
            {
                VbaModuleBackup backup;
                try
                {
                    backup = _vbaJournalStore.Find(
                        _adapter.HostName,
                        _adapter.DocumentKey,
                        ToolArgumentReader.String(command.Arguments, "backupId", string.Empty),
                        moduleName);
                }
                catch (VbaJournalException ex)
                {
                    return ToolResult.Fail(ex.Message, null, "vba_backup_unavailable", false);
                }
                if (backup == null) return ToolResult.Fail("VBA backup not found.", null, "vba_backup_not_found", false);
                command.Arguments["backupId"] = backup.BackupId;
                command.Arguments["moduleName"] = backup.ModuleName;
                return PrepareCurrentModuleGuard(command, session, backup.ModuleName, backup.ComponentType);
            }
            return null;
        }

        public ToolResult PreviewPreparedControllerTool(ToolCommand command, ChatSession session, CancellationToken cancellationToken)
        {
            if (command == null || !IsPreflightMutation(command.ToolId)) return null;
            return ExecuteControllerTool(command, true, session, cancellationToken) ??
                ToolResult.Fail("VBA preflight returned no result.");
        }

        public void ObserveExpectedHash(ChatSession session, string moduleName, string codeSha256)
        {
            if (!string.IsNullOrWhiteSpace(moduleName) && !string.IsNullOrWhiteSpace(codeSha256))
            {
                RecordObservation(session, moduleName, codeSha256);
            }
        }

        public ToolResult RunMacro(string macroName)
        {
            if (string.IsNullOrWhiteSpace(macroName))
            {
                return ToolResult.Fail("macroName is required.", null, "vba_macro_name_required", true);
            }
            var command = new ToolCommand { ToolId = BackendToolId("run_macro") };
            command.Arguments["macroName"] = macroName.Trim();
            return _adapter.ExecuteTool(command) ??
                ToolResult.Fail("VBA macro returned no result.", null, "vba_macro_missing_result", true);
        }

        ToolResult IVbaResourceSource.ListResourceModules()
        {
            var reconciliationError = ReconcilePendingMutations();
            if (reconciliationError != null) return reconciliationError;
            return ListModules();
        }

        ToolResult IVbaResourceSource.ReadResourceModule(
            ChatSession session,
            string moduleName,
            int maxChars)
        {
            var reconciliationError = ReconcilePendingMutations();
            if (reconciliationError != null) return reconciliationError;
            var command = new ToolCommand();
            command.Arguments["moduleName"] = moduleName;
            command.Arguments["maxChars"] = Math.Max(1, Math.Min(1000000, maxChars));
            return ReadModule(command, session);
        }

        private ToolResult ReadModule(ToolCommand command, ChatSession session)
        {
            var requestedModuleName = ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty);
            var moduleName = (requestedModuleName ?? string.Empty).Trim();
            var exactLines = ToolArgumentReader.Int32(command.Arguments, "startLine", 0) > 0 ||
                command.Arguments.ContainsKey("lineCount");
            var result = ExecuteModuleRead(command, moduleName, exactLines);
            var normalizedName = NormalizeModuleName(moduleName);
            if (IsModuleNotFound(result) && !string.Equals(moduleName, normalizedName, StringComparison.OrdinalIgnoreCase))
            {
                moduleName = normalizedName;
                command.Arguments["moduleName"] = moduleName;
                result = ExecuteModuleRead(command, moduleName, exactLines);
            }
            RecordObservationFromRead(session, moduleName, result);
            return result ?? ToolResult.Fail("VBA module read returned no result.", null, "vba_read_missing_result", true);
        }

        private ToolResult ExecuteModuleRead(ToolCommand command, string moduleName, bool exactLines)
        {
            var read = new ToolCommand
            {
                ToolId = BackendToolId("vba_read_module")
            };
            read.Arguments["moduleName"] = moduleName;
            if (exactLines)
            {
                read.Arguments["startLine"] = Math.Max(1, ToolArgumentReader.Int32(command.Arguments, "startLine", 1));
                read.Arguments["lineCount"] = ToolArgumentReader.Int32(command.Arguments, "lineCount", 200);
            }
            else
            {
                read.Arguments["maxChars"] = ToolArgumentReader.Int32(command.Arguments, "maxChars", 30000);
            }
            return _adapter.ExecuteTool(read);
        }

        private ToolResult ListModules()
        {
            var read = new ToolCommand { ToolId = BackendToolId("vba_list_project_components_internal") };
            var result = _adapter.ExecuteTool(read);
            if (result == null || !result.Success) return result ?? ToolResult.Fail("VBA project returned no result.");
            try
            {
                var data = JObject.Parse(result.DataJson ?? "{}");
                var modules = new JArray();
                foreach (var module in (data["modules"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    modules.Add(new JObject
                    {
                        ["name"] = module["name"], ["type"] = module["type"], ["lineCount"] = module["lineCount"]
                    });
                }
                return ToolResult.Ok("VBA modules listed: " + modules.Count + ".", JsonConvert.SerializeObject(new { modules = modules }));
            }
            catch (JsonException ex) { return ToolResult.Fail("Could not parse VBA project: " + ex.Message, null, "vba_read_invalid", true); }
        }

        private ToolResult WriteVbaModule(ToolCommand command, bool dryRun, ChatSession session)
        {
            var moduleName = ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty);
            var componentType = ToolArgumentReader.String(command.Arguments, "componentType", "StdModule");
            var code = ToolArgumentReader.String(command.Arguments, "code", string.Empty);
            var mode = ToolArgumentReader.String(command.Arguments, "mode", "upsert");
            if (string.Equals(mode, "rename", StringComparison.OrdinalIgnoreCase))
            {
                return RenameVbaModule(
                    command,
                    dryRun,
                    session,
                    moduleName,
                    ToolArgumentReader.String(command.Arguments, "newModuleName", string.Empty));
            }
            VbaModuleState existing;
            ToolResult readError;
            var exists = TryReadVbaModule(moduleName, 1000000, out existing, out readError);
            if (!exists && !IsModuleNotFound(readError)) return readError;
            var guardError = ValidateModuleGuard(command, session, moduleName, exists, existing);
            if (guardError != null) return guardError;
            if (exists && string.Equals(mode, "createOnly", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Fail(
                    "VBA module already exists: " + moduleName + ". Use mode=upsert to replace its complete source, or common.vba_apply_patch for a targeted edit.",
                    JsonConvert.SerializeObject(new { moduleName = moduleName, suggestedMode = "upsert", patchTool = ToolId("vba_apply_patch") }),
                    "vba_module_exists",
                    true);
            }
            if (!exists && string.Equals(mode, "updateOnly", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Fail(
                    "VBA module does not exist: " + moduleName + ". Use mode=upsert to create it automatically.",
                    JsonConvert.SerializeObject(new { moduleName = moduleName, suggestedMode = "upsert" }),
                    "vba_module_not_found",
                    true);
            }
            var guard = ReadGuard(command);
            var operationData = JsonConvert.SerializeObject(new
            {
                requestedModuleName = guard == null ? moduleName : guard.RequestedModuleName,
                moduleName = moduleName,
                nameNormalized = guard != null && !string.Equals(guard.RequestedModuleName, moduleName, StringComparison.Ordinal),
                componentType = exists ? existing.ComponentType : componentType,
                mode = mode,
                created = !exists,
                codeSha256 = CodeSha256(code)
            });
            if (dryRun)
            {
                return ToolResult.Ok(
                    "Dry run: would " + (exists ? "update" : "create") + " VBA " +
                    (exists ? existing.ComponentType : componentType) + " " + moduleName + ".",
                    operationData);
            }

            var expectedComponentType = exists ? existing.ComponentType : componentType;
            VbaMutationPreparation prepared;
            ToolResult journalError;
            if (!TryPrepareJournaledMutation(
                command,
                session,
                "write",
                moduleName,
                exists ? existing : null,
                true,
                code,
                expectedComponentType,
                out prepared,
                out journalError))
            {
                return journalError;
            }

            return ExecuteJournaledMutation(prepared, () =>
            {
                ToolResult written;
                if (exists)
                {
                    written = WriteModule(moduleName, code, false, CodeSha256(existing.Code));
                }
                else
                {
                    var create = new ToolCommand { ToolId = BackendToolId("vba_create_module_internal") };
                    create.Arguments["moduleName"] = moduleName;
                    create.Arguments["componentType"] = componentType;
                    create.Arguments["code"] = code;
                    written = _adapter.ExecuteTool(create);
                }
                if (written == null || !written.Success)
                {
                    return written ?? ToolResult.Fail("VBA module write returned no result.", null, "vba_write_failed", false);
                }
                return VerifyModuleWrite(
                    moduleName,
                    code,
                    "VBA module " + (exists ? "updated: " : "created: ") + moduleName,
                    operationData,
                    "vba_write",
                    expectedComponentType,
                    session);
            });
        }

        private ToolResult RenameVbaModule(
            ToolCommand command,
            bool dryRun,
            ChatSession session,
            string moduleName,
            string newModuleName)
        {
            VbaModuleState source;
            ToolResult sourceError;
            var sourceExists = TryReadVbaModule(moduleName, 1000000, out source, out sourceError);
            if (!sourceExists && !IsModuleNotFound(sourceError)) return sourceError;

            VbaModuleState target;
            ToolResult targetError;
            var targetExists = TryReadVbaModule(newModuleName, 1000000, out target, out targetError);
            if (!targetExists && !IsModuleNotFound(targetError)) return targetError;

            var guardError = ValidateRenameGuard(
                command,
                session,
                moduleName,
                sourceExists,
                source,
                newModuleName,
                targetExists,
                target);
            if (guardError != null) return guardError;
            if (!sourceExists) return sourceError;
            if (targetExists)
            {
                return ToolResult.Fail(
                    "VBA rename destination already exists: " + newModuleName + ".",
                    null,
                    "vba_module_exists",
                    true);
            }
            if (!CanRenameComponent(source))
            {
                return ToolResult.Fail(
                    "Only StdModule, ClassModule, and blank code-only MSForm components can be renamed through RNAssistant.",
                    JsonConvert.SerializeObject(new { moduleName = moduleName, componentType = source.ComponentType }),
                    "vba_component_type_read_only",
                    false);
            }

            var guard = ReadGuard(command);
            var requestedNewModuleName = guard == null || string.IsNullOrWhiteSpace(guard.RequestedTargetModuleName)
                ? newModuleName
                : guard.RequestedTargetModuleName;
            var operationData = JsonConvert.SerializeObject(new
            {
                previousModuleName = moduleName,
                moduleName = newModuleName,
                requestedNewModuleName = requestedNewModuleName,
                nameNormalized = !string.Equals(requestedNewModuleName, newModuleName, StringComparison.Ordinal),
                componentType = source.ComponentType,
                mode = "rename",
                codeSha256 = CodeSha256(source.Code)
            });
            if (dryRun)
            {
                return ToolResult.Ok(
                    "Dry run: would rename VBA " + source.ComponentType + " " + moduleName + " to " + newModuleName + ".",
                    operationData);
            }

            VbaPackageMutationPreparation prepared;
            ToolResult journalError;
            if (!TryPrepareJournaledRename(
                command,
                session,
                moduleName,
                newModuleName,
                source,
                out prepared,
                out journalError))
            {
                return journalError;
            }

            return ExecuteJournaledPackageMutation(prepared, () =>
            {
                var rename = new ToolCommand { ToolId = BackendToolId("vba_rename_module_internal") };
                rename.Arguments["moduleName"] = moduleName;
                rename.Arguments["newModuleName"] = newModuleName;
                rename.Arguments["expectedCodeSha256"] = CodeSha256(source.Code);
                var renamed = _adapter.ExecuteTool(rename);
                if (renamed == null || !renamed.Success)
                {
                    return renamed ?? ToolResult.Fail("VBA rename returned no result.", null, "vba_rename_failed", false);
                }

                JObject data;
                try { data = string.IsNullOrWhiteSpace(renamed.DataJson) ? new JObject() : JObject.Parse(renamed.DataJson); }
                catch (JsonException) { data = new JObject(); }
                data["previousModuleName"] = moduleName;
                data["moduleName"] = newModuleName;
                data["requestedNewModuleName"] = requestedNewModuleName;
                data["nameNormalized"] = !string.Equals(requestedNewModuleName, newModuleName, StringComparison.Ordinal);
                data["componentType"] = source.ComponentType;
                data["mode"] = "rename";
                data["codeSha256"] = CodeSha256(source.Code);
                renamed.DataJson = data.ToString(Formatting.None);
                renamed.Message = "VBA module renamed: " + moduleName + " -> " + newModuleName + ".";
                RemoveObservation(session, moduleName);
                RecordObservation(session, newModuleName, CodeSha256(source.Code));
                return renamed;
            });
        }

        private ToolResult DeleteModule(ToolCommand command, bool dryRun, ChatSession session)
        {
            var moduleName = ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty);
            VbaModuleState module;
            ToolResult error;
            if (!TryReadVbaModule(moduleName, 1000000, out module, out error)) return error;
            if (!string.Equals(module.ComponentType, "StdModule", StringComparison.OrdinalIgnoreCase) && !string.Equals(module.ComponentType, "ClassModule", StringComparison.OrdinalIgnoreCase))
                return ToolResult.Fail("Document modules and UserForms cannot be deleted through RNAssistant.", null, "vba_component_type_read_only", false);
            var guardError = ValidateExistingModuleGuard(command, session, moduleName, module);
            if (guardError != null) return guardError;
            if (dryRun)
            {
                return ToolResult.Ok(
                    "Dry run: would delete VBA " + module.ComponentType + " " + moduleName + ".",
                    JsonConvert.SerializeObject(new { moduleName = moduleName, componentType = module.ComponentType }));
            }
            VbaMutationPreparation prepared;
            ToolResult journalError;
            if (!TryPrepareJournaledMutation(
                command,
                session,
                "delete",
                moduleName,
                module,
                false,
                null,
                module.ComponentType,
                out prepared,
                out journalError))
            {
                return journalError;
            }

            return ExecuteJournaledMutation(prepared, () =>
            {
                var delete = new ToolCommand { ToolId = BackendToolId("vba_delete_module_internal") };
                delete.Arguments["moduleName"] = moduleName;
                delete.Arguments["expectedCodeSha256"] = CodeSha256(module.Code);
                var deleted = _adapter.ExecuteTool(delete);
                if (deleted == null || !deleted.Success)
                {
                    return deleted ?? ToolResult.Fail("VBA delete returned no result.", null, "vba_delete_failed", false);
                }
                VbaModuleState remaining;
                ToolResult verifyError;
                if (TryReadVbaModule(moduleName, 1000000, out remaining, out verifyError))
                {
                    return ToolResult.PartialFailure(
                        "VBA delete returned success but the module is still present: " + moduleName + ".",
                        VerificationData(moduleName, null, CodeSha256(remaining.Code), deleted.DataJson),
                        "vba_delete_verify_failed");
                }
                if (!IsModuleNotFound(verifyError))
                {
                    return ToolResult.PartialFailure(
                        "VBA module deletion completed but could not be verified: " + (verifyError == null ? moduleName : verifyError.Message),
                        VerificationData(moduleName, null, null, deleted.DataJson),
                        "vba_delete_verify_failed");
                }
                RemoveObservation(session, moduleName);
                return ToolResult.Ok("VBA module deleted: " + moduleName, deleted.DataJson ?? JsonConvert.SerializeObject(new { moduleName = moduleName }));
            });
        }

        private ToolResult RestoreVbaBackup(ToolCommand command, bool dryRun, ChatSession session, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var backupId = ToolArgumentReader.String(command.Arguments, "backupId", string.Empty);
            var moduleName = ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty);
            VbaModuleBackup backup;
            try
            {
                backup = _vbaJournalStore.Find(_adapter.HostName, _adapter.DocumentKey, backupId, moduleName);
            }
            catch (VbaJournalException ex)
            {
                return ToolResult.Fail(ex.Message, null, "vba_backup_unavailable", false);
            }
            if (backup == null)
            {
                return ToolResult.Fail("VBA backup not found.");
            }

            if (dryRun)
            {
                return ToolResult.Ok(
                    "Dry run: would restore VBA backup " + backup.BackupId + " to " + backup.ModuleName + ".",
                    new JObject
                    {
                        ["backupId"] = backup.BackupId,
                        ["moduleName"] = backup.ModuleName,
                        ["componentType"] = backup.ComponentType,
                        ["createdUtc"] = backup.CreatedUtc,
                        ["codeByteLength"] = backup.CodeByteLength,
                        ["codeSha256"] = backup.CodeSha256
                    }.ToString(Formatting.None));
            }

            VbaModuleState current;
            ToolResult readError;
            var moduleExists = false;
            if (TryReadVbaModule(backup.ModuleName, 1000000, out current, out readError))
            {
                moduleExists = true;
                if (!string.IsNullOrWhiteSpace(backup.ComponentType) &&
                    !string.Equals(backup.ComponentType, current.ComponentType, StringComparison.OrdinalIgnoreCase))
                {
                    return ToolResult.Fail(
                        "VBA restore was blocked because the current component type differs from the backup.",
                        JsonConvert.SerializeObject(new { moduleName = backup.ModuleName, backupType = backup.ComponentType, currentType = current.ComponentType }),
                        "vba_restore_component_type_mismatch",
                        false);
                }
            }
            else if (!IsModuleNotFound(readError))
            {
                return ToolResult.Fail(
                    "VBA restore was blocked because the current module could not be read. " +
                    (readError == null ? string.Empty : readError.Message),
                    readError == null ? null : readError.DataJson,
                    "vba_backup_failed",
                    false);
            }

            var guardError = ValidateModuleGuard(command, session, backup.ModuleName, moduleExists, current);
            if (guardError != null) return guardError;
            var componentType = string.IsNullOrWhiteSpace(backup.ComponentType)
                ? (moduleExists ? current.ComponentType : "StdModule")
                : backup.ComponentType;
            VbaMutationPreparation prepared;
            ToolResult journalError;
            if (!TryPrepareJournaledMutation(
                command,
                session,
                "restore",
                backup.ModuleName,
                moduleExists ? current : null,
                true,
                backup.Code,
                componentType,
                out prepared,
                out journalError))
            {
                return journalError;
            }

            return ExecuteJournaledMutation(prepared, () =>
            {
                ToolResult result;
                if (moduleExists)
                {
                    result = WriteModule(backup.ModuleName, backup.Code, false, CodeSha256(current.Code));
                }
                else
                {
                    var create = new ToolCommand { ToolId = BackendToolId("vba_create_module_internal") };
                    create.Arguments["moduleName"] = backup.ModuleName;
                    create.Arguments["componentType"] = componentType;
                    create.Arguments["code"] = backup.Code ?? string.Empty;
                    result = _adapter.ExecuteTool(create);
                }
                if (result == null || !result.Success)
                {
                    return result ?? ToolResult.Fail("VBA restore write returned no result.", null, "vba_restore_failed", false);
                }

                return VerifyModuleWrite(
                    backup.ModuleName,
                    backup.Code,
                    "VBA backup restored: " + backup.BackupId,
                    JsonConvert.SerializeObject(new
                    {
                        backupId = backup.BackupId,
                        moduleName = backup.ModuleName,
                        codeSha256 = CodeSha256(backup.Code),
                        restore = result
                    }),
                    "vba_restore",
                    componentType,
                    session);
            });
        }

        private ToolResult ApplyVbaPatch(ToolCommand command, bool dryRun, ChatSession session, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var moduleName = ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty);
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                return ToolResult.Fail("moduleName is required.");
            }

            object patchValue;
            command.Arguments.TryGetValue("patch", out patchValue);
            var operations = ParsePatchOperations(patchValue);

            if (operations.Count == 0)
            {
                return ToolResult.Fail("Patch has no operations.");
            }

            VbaModuleState module;
            ToolResult error;
            string resolvedModuleName;
            if (!TryReadExistingModule(moduleName, out resolvedModuleName, out module, out error))
            {
                return error;
            }
            moduleName = resolvedModuleName;

            var code = module.Code;
            var currentHash = CodeSha256(code);
            var guardError = ValidateExistingModuleGuard(command, session, moduleName, module);
            if (guardError != null) return guardError;
            var updated = code;
            var summary = new List<object>();
            foreach (JObject operation in operations.OfType<JObject>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var beforeOperation = updated;
                var result = ApplyPatchOperation(updated, operation, out updated);
                if (!result.Success)
                {
                    return result;
                }

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

            return ExecuteJournaledMutation(prepared, () =>
            {
                var writeResult = WriteModule(moduleName, updated, false, currentHash);
                if (writeResult == null || !writeResult.Success)
                {
                    return writeResult ?? ToolResult.Fail("VBA patch write returned no result.", null, "vba_patch_failed", false);
                }

                return VerifyModuleWrite(
                    moduleName,
                    updated,
                    "VBA patch applied to " + moduleName + ".",
                    preview,
                    "vba_patch",
                    null,
                    session);
            });
        }

        private bool TryReadVbaModule(string moduleName, int maxChars, out VbaModuleState module, out ToolResult error)
        {
            module = null;
            error = null;
            var read = new ToolCommand { ToolId = BackendToolId("vba_read_module") };
            read.Arguments["moduleName"] = moduleName;
            read.Arguments["maxChars"] = maxChars;
            var current = _adapter.ExecuteTool(read);
            if (current == null || !current.Success || string.IsNullOrWhiteSpace(current.DataJson))
            {
                error = current == null
                    ? ToolResult.Fail("VBA module read returned no result.", null, "vba_read_missing_result", true)
                    : current.Success ? ToolResult.Fail("VBA module returned no code.") : current;
                return false;
            }

            try
            {
                var data = JObject.Parse(current.DataJson);
                if (data["code"] == null || data["code"].Type == JTokenType.Null)
                {
                    error = ToolResult.Fail("VBA module data has no code field.", current.DataJson, "vba_read_invalid", true);
                    return false;
                }
                module = new VbaModuleState
                {
                    Name = (string)data["name"] ?? moduleName,
                    Code = (string)data["code"] ?? string.Empty,
                    ComponentType = (string)data["type"] ?? string.Empty,
                    CodeOnlyUserForm = (bool?)data["codeOnlyUserForm"],
                    Truncated = (bool?)data["truncated"] ?? false,
                    LineCount = (int?)data["lineCount"] ?? VbaToolManifestParser.LiveCodeLineCount((string)data["code"] ?? string.Empty)
                };
            }
            catch (JsonException ex)
            {
                error = ToolResult.Fail("Could not parse VBA module data: " + ex.Message);
                return false;
            }

            if (module.Truncated || module.Code.EndsWith("\n...[truncated]", StringComparison.Ordinal))
            {
                error = ToolResult.Fail("VBA module is too large for a safe patch.");
                module = null;
                return false;
            }

            return true;
        }

        private bool TryReadExistingModule(
            string requestedModuleName,
            out string resolvedModuleName,
            out VbaModuleState module,
            out ToolResult error)
        {
            requestedModuleName = (requestedModuleName ?? string.Empty).Trim();
            resolvedModuleName = requestedModuleName;
            if (TryReadVbaModule(requestedModuleName, 1000000, out module, out error))
            {
                resolvedModuleName = string.IsNullOrWhiteSpace(module.Name) ? requestedModuleName : module.Name;
                return true;
            }
            if (!IsModuleNotFound(error)) return false;

            var normalizedName = NormalizeModuleName(requestedModuleName);
            if (!string.Equals(requestedModuleName, normalizedName, StringComparison.OrdinalIgnoreCase) &&
                TryReadVbaModule(normalizedName, 1000000, out module, out error))
            {
                resolvedModuleName = string.IsNullOrWhiteSpace(module.Name) ? normalizedName : module.Name;
                return true;
            }
            if (!IsModuleNotFound(error)) return false;

            resolvedModuleName = normalizedName;
            error = ToolResult.Fail(
                "VBA module not found: " + requestedModuleName +
                (string.Equals(requestedModuleName, normalizedName, StringComparison.Ordinal)
                    ? "."
                    : ". Runtime also tried the normalized name " + normalizedName + ".") +
                " To create it, call common.vba_write_module with moduleName, complete code, and mode=upsert. " +
                "When the existing target name is unknown, list provider vba with kind vba-component.",
                JsonConvert.SerializeObject(new
                {
                    requestedModuleName = requestedModuleName,
                    normalizedModuleName = normalizedName,
                    discoveryTool = "common.resources_list",
                    resourceProvider = VbaResourceProvider.ProviderName,
                    resourceKind = VbaResourceProvider.ComponentKind,
                    creationTool = ToolId("vba_write_module"),
                    creationMode = "upsert"
                }),
                "vba_module_not_found",
                true);
            module = null;
            return false;
        }

        private ToolResult WriteModule(string moduleName, string code, bool createIfMissing, string expectedCodeSha256)
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

        private ToolResult VerifyModuleWrite(
            string moduleName,
            string expectedCode,
            string successMessage,
            string successDataJson,
            string errorPrefix,
            string expectedComponentType = null,
            ChatSession session = null)
        {
            var expectedHash = CodeSha256(expectedCode);
            var expectedComparableHash = VbaToolManifestParser.VbeComparableCodeSha256(expectedCode);
            var expectedLineCount = VbaToolManifestParser.LiveCodeLineCount(expectedCode);
            VbaModuleState actual;
            ToolResult readError;
            if (!TryReadVbaModule(moduleName, 1000000, out actual, out readError))
            {
                return ToolResult.PartialFailure(
                    "VBA write completed but final state could not be read back: " +
                    (readError == null ? moduleName : readError.Message),
                    VerificationData(moduleName, expectedHash, null, successDataJson, expectedComponentType, null, expectedLineCount, null),
                    (errorPrefix ?? "vba_write") + "_verify_failed");
            }

            var actualHash = CodeSha256(actual.Code);
            var actualComparableHash = VbaToolManifestParser.VbeComparableCodeSha256(actual.Code);
            var codeMatches = string.Equals(expectedComparableHash, actualComparableHash, StringComparison.OrdinalIgnoreCase);
            var componentTypeMatches = string.IsNullOrWhiteSpace(expectedComponentType) ||
                string.Equals(expectedComponentType, actual.ComponentType, StringComparison.OrdinalIgnoreCase);
            if (!codeMatches || !componentTypeMatches)
            {
                return ToolResult.PartialFailure(
                    "VBA write verification failed for " + moduleName +
                    ": final code or component type differs from the requested state.",
                    VerificationData(moduleName, expectedHash, actualHash, successDataJson, expectedComponentType, actual.ComponentType, expectedLineCount, actual.LineCount),
                    (errorPrefix ?? "vba_write") + "_verify_mismatch");
            }

            RecordObservation(session, moduleName, actualHash);
            return ToolResult.Ok(successMessage, SuccessfulVerificationData(
                moduleName,
                expectedHash,
                actualHash,
                successDataJson,
                actual.ComponentType,
                actual.LineCount));
        }

        private static string SuccessfulVerificationData(
            string moduleName,
            string requestedHash,
            string actualHash,
            string operationDataJson,
            string actualComponentType,
            int actualLineCount)
        {
            JObject data;
            try { data = string.IsNullOrWhiteSpace(operationDataJson) ? new JObject() : JObject.Parse(operationDataJson); }
            catch (JsonException) { data = new JObject { ["operationData"] = operationDataJson ?? string.Empty }; }
            data["moduleName"] = moduleName ?? string.Empty;
            data["codeSha256"] = actualHash;
            data["lineCount"] = actualLineCount;
            data["componentType"] = actualComponentType ?? string.Empty;
            data["vbeNormalized"] = !string.Equals(requestedHash, actualHash, StringComparison.OrdinalIgnoreCase);
            if (!string.Equals(requestedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                data["requestedCodeSha256"] = requestedHash;
            }
            return data.ToString(Formatting.None);
        }

        private static string VerificationData(
            string moduleName,
            string expectedHash,
            string actualHash,
            string operationDataJson,
            string expectedComponentType = null,
            string actualComponentType = null,
            int? expectedLineCount = null,
            int? actualLineCount = null)
        {
            JToken operationData = null;
            if (!string.IsNullOrWhiteSpace(operationDataJson))
            {
                try { operationData = JToken.Parse(operationDataJson); }
                catch (JsonException) { operationData = new JValue(operationDataJson); }
            }
            return new JObject
            {
                ["moduleName"] = moduleName ?? string.Empty,
                ["expectedCodeSha256"] = string.IsNullOrWhiteSpace(expectedHash) ? null : expectedHash,
                ["actualCodeSha256"] = string.IsNullOrWhiteSpace(actualHash) ? null : actualHash,
                ["expectedComponentType"] = string.IsNullOrWhiteSpace(expectedComponentType) ? null : expectedComponentType,
                ["actualComponentType"] = string.IsNullOrWhiteSpace(actualComponentType) ? null : actualComponentType,
                ["expectedLineCount"] = expectedLineCount,
                ["actualLineCount"] = actualLineCount,
                ["operationData"] = operationData
            }.ToString(Formatting.None);
        }

        private string HostToolPrefix()
        {
            return (_adapter.HostName ?? string.Empty).ToLowerInvariant();
        }

        private bool HostSupportsVba()
        {
            return string.Equals(_adapter.HostName, "Excel", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_adapter.HostName, "Word", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_adapter.HostName, "PowerPoint", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class VbaModuleState
        {
            public string Name { get; set; }
            public string Code { get; set; }
            public string ComponentType { get; set; }
            public bool? CodeOnlyUserForm { get; set; }
            public bool Truncated { get; set; }
            public int LineCount { get; set; }
        }

        private sealed class VbaMutationGuard
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

}
