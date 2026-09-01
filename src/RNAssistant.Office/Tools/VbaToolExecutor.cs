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
using RNAssistant.Office.Vba;
using RNAssistant.Office.Domains.Vba;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class VbaToolExecutor : IVbaResourceSource
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly VbaJournalStore _vbaJournalStore;
        private readonly VbaReader _reader;
        private readonly VbaMutationService _mutationService;
        private readonly VbaPackageService _packageService;
        private readonly IVbaHostBackend _backend;

        public VbaToolExecutor(IOfficeApplicationAdapter adapter, VbaJournalStore vbaJournalStore)
        {
            _adapter = adapter;
            _vbaJournalStore = vbaJournalStore;
            _backend = VbaBackendProvider.Resolve(adapter);
            if (_backend == null) return;
            _reader = new VbaReader(_backend);
            _mutationService = new VbaMutationService(
                new VbaMutationHostDocumentContext(_backend),
                new VbaMutationJournalStoreAdapter(vbaJournalStore),
                new VbaMutationHostReader(_reader),
                new VbaMutationHostBackend(_backend),
                new VbaRenameJournalStoreAdapter(vbaJournalStore));
            _packageService = new VbaPackageService(
                new VbaMutationHostDocumentContext(_backend),
                new VbaPackageJournalStoreAdapter(vbaJournalStore),
                new VbaMutationHostReader(_reader),
                new VbaPackageHostBackend(_backend));
        }

        internal VbaReader Reader { get { return _reader; } }

        public IEnumerable<ToolDefinition> GetControllerTools()
        {
            if (!HostSupportsVba())
            {
                yield break;
            }

            yield return ControllerToolDefinition.Create(ToolId("vba_restore_backup"), "Common", "Mutates document: Restore a VBA module from an exact backupId, or resolve the latest backup for moduleName when backupId is omitted. Runtime pins the exact backup and current target state before confirmation.", RestoreBackupSchema(), mutatesDocument: true, agentCanRun: true, requiresConfirmation: true, riskLevel: 3);
            yield return ControllerToolDefinition.Create(ToolId("vba_write_module"), "Common", "Mutates document with two strict branches. Whole-source write requires moduleName+code and uses mode=upsert/createOnly/updateOnly; componentType applies only on creation. Atomic rename requires moduleName+newModuleName+mode=rename and accepts no code/componentType. Runtime guards both names, normalizes a new destination, rejects collisions, journals both identities, and verifies read-back. Rename preserves the component but does not rewrite textual references to its old name.", WriteModuleSchema(), mutatesDocument: true, agentCanRun: true, requiresConfirmation: true, riskLevel: 3);
            yield return ControllerToolDefinition.Create(ToolId("vba_apply_patch"), "Common", "Mutates document: Apply ordered exact unique source-block replacements to an existing VBA component. There are no line-number, fuzzy, first-match, regex, or implicit insertion modes. Runtime patches one current full-module snapshot in memory, then performs one guarded whole-module write. Exact replacements already satisfied are skipped; an all-no-op patch succeeds without writing. Use common.vba_write_module with complete source when the module is missing.", ApplyPatchSchema(), mutatesDocument: true, agentCanRun: true, requiresConfirmation: true, riskLevel: 3);
            yield return ControllerToolDefinition.Create(ToolId("vba_delete_module"), "Common", "Mutates document: Delete an existing StdModule or ClassModule. Runtime reads it, validates the type, and creates a rollback backup; no separate read call is required. Document modules and UserForms are not deleted.", ModuleNameSchema(), mutatesDocument: true, agentCanRun: true, requiresConfirmation: true, riskLevel: 3);
            yield return ControllerToolDefinition.Create(ToolId("office_run_macro"), "Common", "Mutates document and may execute arbitrary VBA code: Run any existing macro by its exact Office Application.Run name without a manifest or allowlist. Available in Excel, Word, and PowerPoint. The macro may affect files or external state; use only when execution is requested and inspect the returned result.", RunMacroSchema(), mutatesDocument: true, agentCanRun: true, requiresConfirmation: true, riskLevel: 3);
        }

        public string ToolId(string suffix)
        {
            return "common." + suffix;
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
                var outcome = _mutationService.RestoreBackup(
                    new VbaRestoreRequest
                    {
                        BackupId = ToolArgumentReader.String(
                            command.Arguments,
                            "backupId",
                            string.Empty),
                        ModuleName = ToolArgumentReader.String(
                            command.Arguments,
                            "moduleName",
                            string.Empty),
                        DryRun = dryRun,
                        Guard = ReadRestoreGuard(command),
                        Correlation = MutationCorrelation(command, session)
                    },
                    cancellationToken);
                return VbaLegacyResultProjection.ToToolResult(outcome);
            }

            if (string.Equals(command.ToolId, ToolId("vba_apply_patch"), StringComparison.OrdinalIgnoreCase))
            {
                object patchValue;
                command.Arguments.TryGetValue("patch", out patchValue);
                var outcome = _mutationService.ApplyPatch(
                    new VbaApplyPatchRequest
                    {
                        RequestedModuleName = ToolArgumentReader.String(
                            command.Arguments,
                            "moduleName",
                            string.Empty),
                        Operations = ParsePatchOperations(patchValue as JArray),
                        DryRun = dryRun,
                        Guard = ReadGuard(command),
                        Correlation = MutationCorrelation(command, session)
                    },
                    cancellationToken);
                return VbaLegacyResultProjection.ToToolResult(outcome);
            }

            if (string.Equals(command.ToolId, ToolId("vba_write_module"), StringComparison.OrdinalIgnoreCase))
            {
                var mode = ToolArgumentReader.String(command.Arguments, "mode", "upsert");
                if (string.Equals(mode, "rename", StringComparison.OrdinalIgnoreCase))
                {
                    var renameOutcome = _mutationService.RenameModule(
                        new VbaRenameRequest
                        {
                            ModuleName = ToolArgumentReader.String(
                                command.Arguments,
                                "moduleName",
                                string.Empty),
                            NewModuleName = ToolArgumentReader.String(
                                command.Arguments,
                                "newModuleName",
                                string.Empty),
                            DryRun = dryRun,
                            Guard = ReadGuard(command),
                            Correlation = MutationCorrelation(command, session)
                        },
                        cancellationToken);
                    return VbaLegacyResultProjection.ToToolResult(renameOutcome);
                }
                var outcome = _mutationService.WriteWholeModule(
                    new VbaWholeModuleWriteRequest
                    {
                        ModuleName = ToolArgumentReader.String(
                            command.Arguments,
                            "moduleName",
                            string.Empty),
                        Code = ToolArgumentReader.String(command.Arguments, "code", string.Empty),
                        ComponentType = ToolArgumentReader.String(
                            command.Arguments,
                            "componentType",
                            "StdModule"),
                        Mode = WholeModuleWriteMode(mode),
                        DryRun = dryRun,
                        Guard = ReadGuard(command),
                        Correlation = MutationCorrelation(command, session)
                    },
                    cancellationToken);
                return VbaLegacyResultProjection.ToToolResult(outcome);
            }
            if (string.Equals(command.ToolId, ToolId("vba_delete_module"), StringComparison.OrdinalIgnoreCase))
            {
                var outcome = _mutationService.DeleteModule(
                    new RNAssistant.Office.Vba.VbaDeleteModuleRequest
                    {
                        ModuleName = ToolArgumentReader.String(
                            command.Arguments,
                            "moduleName",
                            string.Empty),
                        DryRun = dryRun,
                        Guard = ReadGuard(command),
                        Correlation = MutationCorrelation(command, session)
                    },
                    cancellationToken);
                return VbaLegacyResultProjection.ToToolResult(outcome);
            }
            if (string.Equals(command.ToolId, ToolId("office_run_macro"), StringComparison.OrdinalIgnoreCase)) return RunMacro(command, dryRun);

            return ToolResult.Fail("Unknown VBA controller tool: " + command.ToolId);
        }

        public ToolResult PrepareControllerTool(ToolCommand command, ChatSession session)
        {
            if (command == null || !string.IsNullOrWhiteSpace(command.RuntimeGuardJson)) return null;
            var moduleName = ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty);
            if (string.Equals(command.ToolId, ToolId("vba_apply_patch"), StringComparison.OrdinalIgnoreCase))
            {
                var preparation = _mutationService.PrepareApplyPatchGuard(
                    new VbaApplyPatchGuardRequest
                    {
                        RequestedModuleName = moduleName,
                        Correlation = MutationCorrelation(command, session)
                    });
                if (!preparation.Success)
                {
                    return VbaLegacyResultProjection.ToToolResult(preparation.Error);
                }
                command.Arguments["moduleName"] = preparation.ResolvedModuleName;
                command.RuntimeGuardJson = JsonConvert.SerializeObject(preparation.Guard);
                return null;
            }
            if (string.Equals(command.ToolId, ToolId("vba_delete_module"), StringComparison.OrdinalIgnoreCase))
            {
                var preparation = _mutationService.PrepareDeleteModuleGuard(
                    new VbaDeleteModuleGuardRequest
                    {
                        RequestedModuleName = moduleName,
                        Correlation = MutationCorrelation(command, session)
                    });
                if (!preparation.Success)
                {
                    return VbaLegacyResultProjection.ToToolResult(preparation.Error);
                }
                command.Arguments["moduleName"] = preparation.ResolvedModuleName;
                command.RuntimeGuardJson = JsonConvert.SerializeObject(preparation.Guard);
                return null;
            }
            if (string.Equals(command.ToolId, ToolId("vba_write_module"), StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(ToolArgumentReader.String(command.Arguments, "mode", "upsert"), "rename", StringComparison.OrdinalIgnoreCase))
                {
                    var renamePreparation = _mutationService.PrepareRenameGuard(
                        new VbaRenameGuardRequest
                        {
                            RequestedModuleName = moduleName,
                            RequestedTargetModuleName = ToolArgumentReader.String(
                                command.Arguments,
                                "newModuleName",
                                string.Empty),
                            Correlation = MutationCorrelation(command, session)
                        });
                    if (!renamePreparation.Success)
                    {
                        return VbaLegacyResultProjection.ToToolResult(renamePreparation.Error);
                    }
                    command.Arguments["moduleName"] = renamePreparation.ResolvedModuleName;
                    command.Arguments["newModuleName"] = renamePreparation.ResolvedTargetModuleName;
                    command.RuntimeGuardJson = JsonConvert.SerializeObject(renamePreparation.Guard);
                    return null;
                }
                var preparation = _mutationService.PrepareWholeModuleWriteGuard(
                    new VbaWholeModuleWriteGuardRequest
                    {
                        RequestedModuleName = moduleName,
                        Correlation = MutationCorrelation(command, session)
                    });
                if (!preparation.Success)
                {
                    return VbaLegacyResultProjection.ToToolResult(preparation.Error);
                }
                command.Arguments["moduleName"] = preparation.ResolvedModuleName;
                command.RuntimeGuardJson = JsonConvert.SerializeObject(preparation.Guard);
                return null;
            }
            if (string.Equals(command.ToolId, ToolId("vba_restore_backup"), StringComparison.OrdinalIgnoreCase))
            {
                var preparation = _mutationService.PrepareRestoreGuard(
                    new VbaRestoreGuardRequest
                    {
                        BackupId = ToolArgumentReader.String(
                            command.Arguments,
                            "backupId",
                            string.Empty),
                        ModuleName = moduleName,
                        Correlation = MutationCorrelation(command, session)
                    });
                if (!preparation.Success)
                {
                    return VbaLegacyResultProjection.ToToolResult(preparation.Error);
                }
                command.Arguments["backupId"] = preparation.BackupId;
                command.Arguments["moduleName"] = preparation.ModuleName;
                command.RuntimeGuardJson = JsonConvert.SerializeObject(preparation.Guard);
                return null;
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
            var command = new ToolCommand { ToolId = ToolId("office_run_macro") };
            command.Arguments["macroName"] = macroName;
            return RunMacro(command, false);
        }

        private ToolResult RunMacro(ToolCommand command, bool dryRun)
        {
            var macroName = ToolArgumentReader.String(command == null ? null : command.Arguments, "macroName", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(macroName))
            {
                return ToolResult.Fail("macroName is required.", null, "vba_macro_name_required", true);
            }

            JArray arguments;
            try
            {
                object raw;
                if (command.Arguments != null && command.Arguments.TryGetValue("arguments", out raw) && raw != null)
                {
                    var token = raw as JToken ?? JToken.FromObject(raw);
                    arguments = token as JArray;
                    if (arguments == null) return ToolResult.Fail("Macro arguments must be a native JSON array.", null, "vba_macro_arguments_invalid", true);
                }
                else
                {
                    arguments = new JArray();
                }
            }
            catch (JsonException ex)
            {
                return ToolResult.Fail("Macro arguments are invalid: " + ex.Message, null, "vba_macro_arguments_invalid", true);
            }
            if (dryRun)
            {
                return ToolResult.Ok("Dry run: would run Office macro " + macroName + ".", JsonConvert.SerializeObject(new
                {
                    macroName = macroName,
                    arguments = arguments
                }));
            }

            return VbaLegacyResultProjection.ToToolResult(
                _backend.RunMacro(new VbaRunMacroRequest
                {
                    MacroName = macroName,
                    Arguments = VbaPackageHostBackend.ToArguments(arguments)
                }));
        }

        ToolResult IVbaResourceSource.ListResourceModules()
        {
            var reconciliationError = ReconcilePendingMutations();
            if (reconciliationError != null) return reconciliationError;
            IReadOnlyList<VbaModuleState> project;
            ToolResult error;
            if (!_reader.TryReadProject(out project, out error)) return error;
            var modules = new JArray(project.Select(module => new JObject
            {
                ["name"] = module.Name,
                ["type"] = module.ComponentType,
                ["lineCount"] = module.LineCount
            }));
            return ToolResult.Ok(
                "VBA modules listed: " + modules.Count + ".",
                JsonConvert.SerializeObject(new { modules = modules }));
        }

        ToolResult IVbaResourceSource.ReadResourceModule(
            ChatSession session,
            string moduleName,
            int maxChars)
        {
            var reconciliationError = ReconcilePendingMutations();
            if (reconciliationError != null) return reconciliationError;
            VbaModuleState module;
            ToolResult result;
            if (!_reader.TryReadResourceModule(moduleName, maxChars, out module, out result)) return result;
            RecordObservationFromModule(session, moduleName, module);
            return result;
        }

        private static IReadOnlyList<VbaPatchOperationRequest> ParsePatchOperations(JArray patch)
        {
            var operations = new List<VbaPatchOperationRequest>();
            if (patch == null) return operations;
            foreach (var token in patch)
            {
                var item = token as JObject;
                operations.Add(item == null
                    ? null
                    : new VbaPatchOperationRequest
                    {
                        Operation = (string)item["op"],
                        Find = (string)item["find"],
                        Text = (string)item["text"]
                    });
            }
            return operations;
        }

        private static VbaWholeModuleWriteMode WholeModuleWriteMode(string mode)
        {
            if (string.Equals(mode, "createOnly", StringComparison.OrdinalIgnoreCase))
            {
                return VbaWholeModuleWriteMode.CreateOnly;
            }
            if (string.Equals(mode, "updateOnly", StringComparison.OrdinalIgnoreCase))
            {
                return VbaWholeModuleWriteMode.UpdateOnly;
            }
            if (string.IsNullOrWhiteSpace(mode) ||
                string.Equals(mode, "upsert", StringComparison.OrdinalIgnoreCase))
            {
                return VbaWholeModuleWriteMode.Upsert;
            }
            return VbaWholeModuleWriteMode.Unknown;
        }

        private static VbaMutationCorrelation MutationCorrelation(
            ToolCommand command,
            ChatSession session)
        {
            return new VbaMutationCorrelation
            {
                SessionId = SessionId(session),
                RunId = session == null || session.LastRun == null ? null : session.LastRun.RunId,
                TurnId = session == null || session.LastRun == null ? null : session.LastRun.TurnId,
                StepId = command == null ? null : command.RuntimeStepId,
                ToolCallId = command == null ? null : command.ToolCallId
            };
        }

        private static string SessionId(ChatSession session)
        {
            return session == null ? string.Empty : session.Id ?? string.Empty;
        }

        private bool HostSupportsVba()
        {
            return _backend != null &&
                (string.Equals(_adapter.HostName, "Excel", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_adapter.HostName, "Word", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_adapter.HostName, "PowerPoint", StringComparison.OrdinalIgnoreCase));
        }


    }

}
