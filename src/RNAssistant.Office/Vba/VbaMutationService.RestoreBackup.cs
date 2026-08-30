using System;
using System.Threading;
using Newtonsoft.Json.Linq;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Vba
{
    internal sealed partial class VbaMutationService
    {
        public VbaRestoreGuardPreparation PrepareRestoreGuard(
            VbaRestoreGuardRequest request)
        {
            var backupId = (request == null
                ? null
                : request.BackupId ?? string.Empty).Trim();
            var moduleName = (request == null
                ? null
                : request.ModuleName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(backupId) &&
                string.IsNullOrWhiteSpace(moduleName))
            {
                return RestoreGuardFailure(VbaMutationOutcome.Error(
                    "backupId or moduleName is required.",
                    null,
                    "vba_backup_selector_required",
                    true));
            }

            var backupRead = _journal.FindBackup(
                _document.HostName,
                _document.DocumentKey,
                backupId,
                moduleName);
            var backupError = BackupReadFailure(backupRead);
            if (backupError != null) return RestoreGuardFailure(backupError);

            var backup = backupRead.Backup;
            var validationError = ValidateBackup(backup);
            if (validationError != null) return RestoreGuardFailure(validationError);

            var currentRead = _reader.ReadModule(backup.ModuleName, 1000000);
            var moduleExists = currentRead != null && currentRead.Success;
            var current = moduleExists ? currentRead.Module : null;
            if (!moduleExists && (currentRead == null || !currentRead.IsNotFound))
            {
                return RestoreGuardFailure(ReadFailure(currentRead));
            }
            var typeError = ValidateRestoreComponentType(backup, current);
            if (typeError != null) return RestoreGuardFailure(typeError);

            return new VbaRestoreGuardPreparation
            {
                BackupId = backup.BackupId,
                ModuleName = backup.ModuleName,
                Guard = CreateRestoreGuard(
                    request.Correlation,
                    backup,
                    moduleExists,
                    current)
            };
        }

        public VbaMutationOutcome RestoreBackup(
            VbaRestoreRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request == null)
            {
                return VbaMutationOutcome.Error(
                    "VBA restore request is missing.",
                    null,
                    "vba_restore_request_missing",
                    false);
            }

            var backupId = (request.BackupId ?? string.Empty).Trim();
            var moduleName = (request.ModuleName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(backupId) ||
                string.IsNullOrWhiteSpace(moduleName))
            {
                return SnapshotRequired(moduleName);
            }

            var guardError = ValidateRestoreGuardContext(
                request,
                backupId,
                moduleName);
            if (guardError != null) return guardError;

            var backupRead = _journal.FindBackup(
                _document.HostName,
                _document.DocumentKey,
                backupId,
                moduleName);
            var backupError = BackupReadFailure(backupRead);
            if (backupError != null) return backupError;

            var backup = backupRead.Backup;
            var validationError = ValidateBackup(backup);
            if (validationError != null) return validationError;
            var identityError = ValidateBoundBackup(request.Guard, backup);
            if (identityError != null) return identityError;

            var currentRead = _reader.ReadModule(moduleName, 1000000);
            var moduleExists = currentRead != null && currentRead.Success;
            var current = moduleExists ? currentRead.Module : null;
            if (!moduleExists && (currentRead == null || !currentRead.IsNotFound))
            {
                return RestoreReadFailure(currentRead);
            }

            var typeError = ValidateRestoreComponentType(backup, current);
            if (typeError != null) return typeError;
            var currentError = ValidateRestoreCurrentState(
                request,
                moduleName,
                moduleExists,
                current);
            if (currentError != null) return currentError;

            var operationData = RestoreData(backup);
            if (request.DryRun)
            {
                return VbaMutationOutcome.Ok(
                    "Dry run: would restore VBA backup " + backup.BackupId +
                    " to " + backup.ModuleName + ".",
                    operationData);
            }

            var componentType = string.IsNullOrWhiteSpace(backup.ComponentType)
                ? (moduleExists ? current.ComponentType : "StdModule")
                : backup.ComponentType;
            var correlation = CorrelationFrom(request.Guard, request.Correlation);
            var preparation = PrepareJournaledMutation(new VbaModuleMutationRequest
            {
                Operation = "restore",
                ModuleName = backup.ModuleName,
                Before = moduleExists ? current : null,
                IntendedAfterExists = true,
                IntendedAfterCode = backup.Code,
                IntendedComponentType = componentType,
                Correlation = correlation
            });
            if (!preparation.Success) return preparation.Error;

            return ExecuteJournaledMutation(
                preparation.Preparation,
                delegate
                {
                    var action = _backend.RestoreModule(new VbaRestoreBackendRequest
                    {
                        ModuleName = backup.ModuleName,
                        Code = backup.Code,
                        ComponentType = componentType,
                        ModuleExists = moduleExists,
                        ExpectedCodeSha256 = moduleExists
                            ? CodeSha256(current.Code)
                            : null
                    });
                    if (action == null ||
                        action.Status != VbaMutationActionStatus.Succeeded)
                    {
                        return action ?? VbaMutationActionResult.Error(
                            "VBA restore write returned no result.",
                            null,
                            "vba_restore_failed",
                            false);
                    }

                    return _verifier.VerifyModuleWrite(
                        backup.ModuleName,
                        backup.Code,
                        "VBA backup restored: " + backup.BackupId,
                        new JObject
                        {
                            ["backupId"] = backup.BackupId,
                            ["moduleName"] = backup.ModuleName,
                            ["codeSha256"] = CodeSha256(backup.Code),
                            ["restore"] = action.Data
                        },
                        "vba_restore",
                        componentType,
                        correlation.SessionId);
                },
                cancellationToken);
        }

        private VbaMutationOutcome ValidateRestoreGuardContext(
            VbaRestoreRequest request,
            string backupId,
            string moduleName)
        {
            var guard = request.Guard;
            if (guard == null || guard.Version != 1 ||
                string.IsNullOrWhiteSpace(guard.BackupId) ||
                string.IsNullOrWhiteSpace(guard.ModuleName) ||
                string.IsNullOrWhiteSpace(guard.BackupLiveCodeSha256))
            {
                return SnapshotRequired(moduleName);
            }

            var correlation = request.Correlation ?? new VbaMutationCorrelation();
            if (!string.Equals(guard.Host, _document.HostName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(guard.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    guard.SessionId ?? string.Empty,
                    correlation.SessionId ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase) ||
                !RestoreDocumentIdentityMatches(guard))
            {
                return VbaMutationOutcome.Error(
                    "The prepared VBA restore belongs to another document, chat, or module. Retry it in the current document.",
                    new JObject
                    {
                        ["moduleName"] = moduleName,
                        ["backupId"] = backupId,
                        ["retrySameTool"] = true
                    },
                    "vba_snapshot_context_changed",
                    true);
            }
            if (!string.Equals(
                    guard.BackupId,
                    backupId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return BackupChanged(backupId, moduleName);
            }
            return null;
        }

        private bool RestoreDocumentIdentityMatches(VbaRestoreGuard guard)
        {
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

        private VbaMutationOutcome ValidateBoundBackup(
            VbaRestoreGuard guard,
            VbaBackupSnapshot backup)
        {
            if (!string.Equals(guard.BackupId, backup.BackupId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(guard.ModuleName, backup.ModuleName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    guard.BackupComponentType ?? string.Empty,
                    backup.ComponentType ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    guard.BackupLiveCodeSha256,
                    CodeSha256(backup.Code),
                    StringComparison.OrdinalIgnoreCase))
            {
                return BackupChanged(backup.BackupId, backup.ModuleName);
            }
            return null;
        }

        private VbaMutationOutcome ValidateRestoreCurrentState(
            VbaRestoreRequest request,
            string moduleName,
            bool moduleExists,
            VbaModuleState current)
        {
            var guard = request.Guard;
            var actualHash = moduleExists && current != null
                ? CodeSha256(current.Code)
                : null;
            if (guard.ModuleExists != moduleExists ||
                moduleExists && !string.Equals(
                    guard.CurrentCodeSha256,
                    actualHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                var correlation = request.Correlation ?? new VbaMutationCorrelation();
                RemoveObservation(correlation.SessionId, moduleName);
                return StaleSnapshot(
                    moduleName,
                    guard.ModuleExists,
                    guard.CurrentCodeSha256,
                    moduleExists,
                    actualHash,
                    "restore");
            }
            return null;
        }

        private VbaRestoreGuard CreateRestoreGuard(
            VbaMutationCorrelation correlation,
            VbaBackupSnapshot backup,
            bool moduleExists,
            VbaModuleState current)
        {
            correlation = correlation ?? new VbaMutationCorrelation();
            return new VbaRestoreGuard
            {
                Version = 1,
                Host = _document.HostName ?? string.Empty,
                DocumentKey = _document.DocumentKey ?? string.Empty,
                RuntimeDocumentKey = _document.RuntimeDocumentKey ?? string.Empty,
                SessionId = correlation.SessionId ?? string.Empty,
                RunId = correlation.RunId,
                TurnId = correlation.TurnId,
                StepId = correlation.StepId,
                ToolCallId = correlation.ToolCallId,
                BackupId = backup.BackupId,
                ModuleName = backup.ModuleName,
                BackupComponentType = backup.ComponentType ?? string.Empty,
                BackupLiveCodeSha256 = CodeSha256(backup.Code),
                ModuleExists = moduleExists,
                CurrentCodeSha256 = moduleExists && current != null
                    ? CodeSha256(current.Code)
                    : string.Empty
            };
        }

        private static VbaMutationOutcome ValidateBackup(VbaBackupSnapshot backup)
        {
            if (backup == null || string.IsNullOrWhiteSpace(backup.BackupId) ||
                string.IsNullOrWhiteSpace(backup.ModuleName) || backup.Code == null)
            {
                return VbaMutationOutcome.Error(
                    "VBA backup is incomplete and cannot be restored.",
                    null,
                    "vba_backup_unavailable",
                    false);
            }
            return null;
        }

        private static VbaMutationOutcome BackupReadFailure(VbaBackupReadResult read)
        {
            if (read != null && read.Success && read.Backup != null) return null;
            if (read == null || read.Success)
            {
                return VbaMutationOutcome.Error(
                    "VBA backup lookup returned no result.",
                    null,
                    "vba_backup_unavailable",
                    false);
            }
            return VbaMutationOutcome.Error(
                read.Message,
                read.Data,
                read.ErrorCode,
                read.Retryable);
        }

        private static VbaMutationOutcome RestoreReadFailure(
            VbaMutationReadResult read)
        {
            return VbaMutationOutcome.Error(
                "VBA restore was blocked because the current module could not be read. " +
                (read == null ? string.Empty : read.Message),
                read == null ? null : read.Data,
                "vba_backup_failed",
                false);
        }

        private static VbaMutationOutcome ValidateRestoreComponentType(
            VbaBackupSnapshot backup,
            VbaModuleState current)
        {
            if (current == null || string.IsNullOrWhiteSpace(backup.ComponentType) ||
                string.Equals(
                    backup.ComponentType,
                    current.ComponentType,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return VbaMutationOutcome.Error(
                "VBA restore was blocked because the current component type differs from the backup.",
                new JObject
                {
                    ["moduleName"] = backup.ModuleName,
                    ["backupType"] = backup.ComponentType,
                    ["currentType"] = current.ComponentType
                },
                "vba_restore_component_type_mismatch",
                false);
        }

        private static VbaMutationOutcome BackupChanged(
            string backupId,
            string moduleName)
        {
            return VbaMutationOutcome.Error(
                "The selected VBA backup changed after confirmation was prepared. The restore was not applied; retry it against the selected backup.",
                new JObject
                {
                    ["backupId"] = backupId ?? string.Empty,
                    ["moduleName"] = moduleName ?? string.Empty,
                    ["retrySameTool"] = true
                },
                "vba_restore_backup_changed",
                true);
        }

        private static JObject RestoreData(VbaBackupSnapshot backup)
        {
            return new JObject
            {
                ["backupId"] = backup.BackupId,
                ["moduleName"] = backup.ModuleName,
                ["componentType"] = backup.ComponentType,
                ["createdUtc"] = backup.CreatedUtc,
                ["codeByteLength"] = backup.CodeByteLength,
                ["codeSha256"] = backup.CodeSha256
            };
        }

        private static VbaRestoreGuardPreparation RestoreGuardFailure(
            VbaMutationOutcome error)
        {
            return new VbaRestoreGuardPreparation { Error = error };
        }

        private static VbaMutationCorrelation CorrelationFrom(
            VbaRestoreGuard guard,
            VbaMutationCorrelation fallback)
        {
            if (guard == null)
            {
                fallback = fallback ?? new VbaMutationCorrelation();
                return new VbaMutationCorrelation
                {
                    SessionId = fallback.SessionId,
                    RunId = fallback.RunId,
                    TurnId = fallback.TurnId,
                    StepId = fallback.StepId,
                    ToolCallId = fallback.ToolCallId
                };
            }
            return new VbaMutationCorrelation
            {
                SessionId = guard.SessionId,
                RunId = guard.RunId,
                TurnId = guard.TurnId,
                StepId = guard.StepId,
                ToolCallId = guard.ToolCallId
            };
        }
    }
}
