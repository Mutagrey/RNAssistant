using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RNAssistant.Office.Vba;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class VbaToolExecutor
    {
        private ToolResult ReconcilePendingMutations()
        {
            try
            {
                var open = _vbaJournalStore.ListOpenMutations(_adapter.HostName, _adapter.DocumentKey);
                foreach (var record in open)
                {
                    if (record == null || record.Prepared == null) continue;
                    var assessment = _mutationService.InspectMutation(record.Prepared);
                    _vbaJournalStore.CompleteMutation(
                        _adapter.HostName,
                        _adapter.DocumentKey,
                        record.Prepared.MutationId,
                        assessment.Status,
                        assessment.ActualExists,
                        assessment.ActualCodeSha256,
                        assessment.ActualComparableCodeSha256,
                        assessment.ErrorCode,
                        "Recovered on the next safe VBA access. " + assessment.Message);
                }
                var openPackages = _vbaJournalStore.ListOpenPackageMutations(_adapter.HostName, _adapter.DocumentKey);
                foreach (var record in openPackages)
                {
                    if (record == null || record.Prepared == null) continue;
                    var assessment = InspectPackageMutation(record.Prepared);
                    _vbaJournalStore.CompletePackageMutation(
                        _adapter.HostName,
                        _adapter.DocumentKey,
                        record.Prepared.MutationId,
                        assessment.Status,
                        assessment.Components,
                        assessment.ErrorCode,
                        "Recovered on the next safe VBA access. " + assessment.Message);
                }
                return null;
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(
                    "VBA history could not be validated; the operation was blocked. " + ex.Message,
                    null,
                    "vba_journal_unavailable",
                    false);
            }
        }

        private bool TryPrepareJournaledRename(
            ToolCommand command,
            ChatSession session,
            string moduleName,
            string newModuleName,
            VbaModuleState source,
            out VbaPackageMutationPreparation prepared,
            out ToolResult error)
        {
            prepared = null;
            error = null;
            try
            {
                var guard = ReadGuard(command);
                prepared = _vbaJournalStore.PreparePackageMutation(new VbaPackageMutationPreparation
                {
                    Operation = "rename",
                    PackageId = ToolId("vba_write_module") + ":rename",
                    PackageVersion = "1",
                    SessionOnly = false,
                    RetainBackups = false,
                    Host = _adapter.HostName ?? string.Empty,
                    DocumentKey = _adapter.DocumentKey ?? string.Empty,
                    RuntimeDocumentKey = _adapter.RuntimeDocumentKey ?? string.Empty,
                    DocumentTitle = _adapter.DocumentTitle ?? string.Empty,
                    Components = new System.Collections.Generic.List<VbaPackageMutationComponent>
                    {
                        new VbaPackageMutationComponent
                        {
                            ModuleName = moduleName,
                            BeforeExists = true,
                            BeforeComponentType = source.ComponentType,
                            BeforeCode = source.Code,
                            IntendedAfterExists = false
                        },
                        new VbaPackageMutationComponent
                        {
                            ModuleName = newModuleName,
                            BeforeExists = false,
                            IntendedAfterExists = true,
                            IntendedAfterComponentType = source.ComponentType,
                            IntendedAfterCode = source.Code
                        }
                    },
                    SessionId = guard == null
                        ? (session == null ? string.Empty : session.Id ?? string.Empty)
                        : guard.SessionId,
                    RunId = guard == null
                        ? (session == null || session.LastRun == null ? null : session.LastRun.RunId)
                        : guard.RunId,
                    TurnId = guard == null
                        ? (session == null || session.LastRun == null ? null : session.LastRun.TurnId)
                        : guard.TurnId,
                    StepId = guard == null ? command == null ? null : command.RuntimeStepId : guard.StepId,
                    ToolCallId = guard == null ? command == null ? null : command.ToolCallId : guard.ToolCallId
                });
                return true;
            }
            catch (Exception ex)
            {
                error = ToolResult.Fail(
                    "VBA rename was blocked because its prepared two-name journal record could not be saved. " + ex.Message,
                    null,
                    "vba_journal_prepare_failed",
                    false);
                return false;
            }
        }

        private bool TryPrepareJournaledPackageMutation(
            ToolDefinition package,
            ToolCommand command,
            ChatSession session,
            string operation,
            bool sessionOnly,
            bool intendedAfterExists,
            out VbaPackageMutationPreparation prepared,
            out ToolResult error)
        {
            prepared = null;
            error = null;
            if (package == null || package.Components == null || package.Components.Count == 0)
            {
                error = ToolResult.Fail("VBA package mutation has no components.", null, "vba_package_empty", false);
                return false;
            }

            var components = new System.Collections.Generic.List<VbaPackageMutationComponent>();
            foreach (var component in package.Components)
            {
                VbaModuleState before;
                ToolResult readError;
                var beforeExists = _reader.TryReadModule(component.Name, 1000000, out before, out readError);
                if (!beforeExists && !VbaReader.IsModuleNotFound(readError))
                {
                    error = ToolResult.Fail(
                        "VBA package mutation was blocked because component state could not be read: " + component.Name + ".",
                        readError == null ? null : readError.DataJson,
                        "vba_package_probe_failed",
                        false);
                    return false;
                }
                if (beforeExists &&
                    (string.Equals(before.ComponentType, "MSForm", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(component.Type, "MSForm", StringComparison.OrdinalIgnoreCase)) &&
                    (!string.Equals(before.ComponentType, component.Type, StringComparison.OrdinalIgnoreCase) ||
                     before.CodeOnlyUserForm != true))
                {
                    error = ToolResult.Fail(
                        "VBA package cannot replace or remove UserForm state unless the existing component is a blank code-only MSForm: " + component.Name + ".",
                        null,
                        "vba_userform_designer_unsupported",
                        false);
                    return false;
                }
                components.Add(new VbaPackageMutationComponent
                {
                    ModuleName = component.Name,
                    BeforeExists = beforeExists,
                    BeforeComponentType = beforeExists ? before.ComponentType : null,
                    BeforeCode = beforeExists ? before.Code : null,
                    IntendedAfterExists = intendedAfterExists,
                    IntendedAfterComponentType = intendedAfterExists ? component.Type : null,
                    IntendedAfterCode = intendedAfterExists ? component.Code : null
                });
            }

            try
            {
                prepared = _vbaJournalStore.PreparePackageMutation(new VbaPackageMutationPreparation
                {
                    Operation = operation,
                    PackageId = package.Id ?? string.Empty,
                    PackageVersion = package.PackageVersion ?? string.Empty,
                    SessionOnly = sessionOnly,
                    RetainBackups = !sessionOnly,
                    Host = _adapter.HostName ?? string.Empty,
                    DocumentKey = _adapter.DocumentKey ?? string.Empty,
                    RuntimeDocumentKey = _adapter.RuntimeDocumentKey ?? string.Empty,
                    DocumentTitle = _adapter.DocumentTitle ?? string.Empty,
                    Components = components,
                    SessionId = session == null ? string.Empty : session.Id ?? string.Empty,
                    RunId = session == null || session.LastRun == null ? null : session.LastRun.RunId,
                    TurnId = session == null || session.LastRun == null ? null : session.LastRun.TurnId,
                    StepId = command == null ? null : command.RuntimeStepId,
                    ToolCallId = command == null ? null : command.ToolCallId
                });
                return true;
            }
            catch (Exception ex)
            {
                error = ToolResult.Fail(
                    "VBA package " + operation + " was blocked because its prepared journal record could not be saved. " + ex.Message,
                    null,
                    "vba_package_journal_prepare_failed",
                    false);
                return false;
            }
        }

        private ToolResult ExecuteJournaledPackageMutation(VbaPackageMutationPreparation prepared, Func<ToolResult> action)
        {
            TraceMutation(prepared, "domain.effect.prepared", null);
            var rename = prepared != null && string.Equals(prepared.Operation, "rename", StringComparison.OrdinalIgnoreCase);
            var mutationLabel = rename ? "VBA rename" : "VBA package mutation";
            ToolResult result;
            try
            {
                if (action != null) TraceMutation(prepared, "domain.effect.dispatched", null);
                result = action == null ? null : action();
            }
            catch (Exception ex)
            {
                result = ToolResult.Fail(
                    mutationLabel + " threw after its prepared record was persisted. " + ex.Message,
                    null,
                    rename ? "vba_rename_exception" : "vba_package_mutation_exception",
                    false);
            }
            if (result == null)
            {
                result = ToolResult.Fail(
                    mutationLabel + " returned no result.",
                    null,
                    rename ? "vba_rename_missing_result" : "vba_package_mutation_missing_result",
                    false);
            }

            var assessment = InspectPackageMutation(prepared);
            TraceMutation(prepared, "domain.effect.verified", assessment.Status);
            try
            {
                _vbaJournalStore.CompletePackageMutation(
                    prepared.Host,
                    prepared.DocumentKey,
                    prepared.MutationId,
                    assessment.Status,
                    assessment.Components,
                    result.ErrorCode ?? assessment.ErrorCode,
                    assessment.Message);
            }
            catch (Exception ex)
            {
                return ToolResult.PartialFailure(
                    "The " + mutationLabel +
                    " effect was inspected, but its terminal journal record could not be saved. " +
                    "Inspect the affected component identities before retrying. " + ex.Message,
                    PackageJournalData(result.DataJson, prepared, assessment, false),
                    rename ? "vba_rename_journal_terminal_failed" : "vba_package_journal_terminal_failed");
            }

            result.DataJson = PackageJournalData(result.DataJson, prepared, assessment);
            if (string.Equals(assessment.Status, VbaMutationStatuses.Unknown, StringComparison.Ordinal))
            {
                result.Success = false;
                result.Status = "partial_failure";
                result.ErrorCode = rename ? "vba_rename_unknown" : "vba_package_mutation_unknown";
                result.Retryable = false;
                result.Message = (result.Message ?? mutationLabel + " failed.") +
                    " Final component identity state is mixed or unknown; inspect it before retrying.";
            }
            else if (result.Success && !string.Equals(assessment.Status, VbaMutationStatuses.Committed, StringComparison.Ordinal))
            {
                return ToolResult.PartialFailure(
                    (result.Message ?? mutationLabel + " reported success.") +
                    " Read-back did not match the complete intended state.",
                    result.DataJson,
                    rename ? "vba_rename_verify_failed" : "vba_package_verify_failed");
            }
            else if (!result.Success && string.Equals(assessment.Status, VbaMutationStatuses.Committed, StringComparison.Ordinal))
            {
                return ToolResult.PartialFailure(
                    (result.Message ?? mutationLabel + " reported failure.") +
                    " Live components match the intended result despite the backend failure report.",
                    result.DataJson,
                    rename ? "vba_rename_committed_after_error" : "vba_package_mutation_committed_after_error");
            }
            return result;
        }

        private PackageMutationAssessment InspectPackageMutation(
            VbaPackageMutationPreparation prepared)
        {
            var components = new System.Collections.Generic.List<VbaPackageMutationComponentAssessment>();
            var packageOperation = prepared != null &&
                (string.Equals(prepared.Operation, "package_install", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(prepared.Operation, "package_remove", StringComparison.OrdinalIgnoreCase));
            foreach (var expected in prepared.Components ?? new System.Collections.Generic.List<VbaPackageMutationComponent>())
            {
                components.Add(InspectPackageComponent(expected, packageOperation));
            }
            var allIntended = components.Count > 0 && components.All(item => item.MatchesIntendedAfter);
            var allBefore = components.Count > 0 && components.All(item => item.MatchesBefore);
            var status = allIntended
                ? VbaMutationStatuses.Committed
                : allBefore
                    ? VbaMutationStatuses.NotApplied
                    : VbaMutationStatuses.Unknown;
            var failed = components.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.ErrorCode));
            var rename = prepared != null && string.Equals(prepared.Operation, "rename", StringComparison.OrdinalIgnoreCase);
            return new PackageMutationAssessment
            {
                Status = status,
                Components = components,
                ErrorCode = failed == null ? null : failed.ErrorCode,
                Message = allIntended
                    ? rename
                        ? "The old and new VBA identities match the recorded intended rename state."
                        : "Every live package component matches the recorded intended state."
                    : allBefore
                        ? rename
                            ? "The old and new VBA identities match the recorded state before rename."
                            : "Every live package component matches the recorded before state."
                        : rename
                            ? "The old and new VBA identities match neither the complete before nor intended rename state."
                            : "Package components do not collectively match either the complete before or intended state."
            };
        }

        private VbaPackageMutationComponentAssessment InspectPackageComponent(
            VbaPackageMutationComponent expected,
            bool packageOperation)
        {
            VbaModuleState actual;
            ToolResult readError;
            try
            {
                if (_reader.TryReadModule(expected.ModuleName, 1000000, out actual, out readError))
                {
                    var hash = VbaTextCanonicalizer.PackageCodeSha256(actual.Code);
                    var comparableHash = packageOperation
                        ? VbaTextCanonicalizer.PackageComparableCodeSha256(actual.Code)
                        : VbaTextCanonicalizer.VbeComparableCodeSha256(actual.Code);
                    var matchesBefore = expected.BeforeExists &&
                        VbaVerifier.MatchesRecordedState(
                            hash,
                            comparableHash,
                            expected.BeforeCodeSha256,
                            expected.BeforeComparableCodeSha256) &&
                        string.Equals(actual.ComponentType, expected.BeforeComponentType, StringComparison.OrdinalIgnoreCase) &&
                        (!string.Equals(expected.BeforeComponentType, "MSForm", StringComparison.OrdinalIgnoreCase) || actual.CodeOnlyUserForm == true);
                    var matchesIntended = expected.IntendedAfterExists &&
                        VbaVerifier.MatchesRecordedState(
                            hash,
                            comparableHash,
                            expected.IntendedAfterCodeSha256,
                            expected.IntendedAfterComparableCodeSha256) &&
                        string.Equals(actual.ComponentType, expected.IntendedAfterComponentType, StringComparison.OrdinalIgnoreCase) &&
                        (!string.Equals(expected.IntendedAfterComponentType, "MSForm", StringComparison.OrdinalIgnoreCase) || actual.CodeOnlyUserForm == true);
                    return new VbaPackageMutationComponentAssessment
                    {
                        ModuleName = expected.ModuleName,
                        ActualExists = true,
                        ActualComponentType = actual.ComponentType,
                        ActualCodeSha256 = hash,
                        ActualComparableCodeSha256 = comparableHash,
                        MatchesBefore = matchesBefore,
                        MatchesIntendedAfter = matchesIntended,
                        ErrorCode = matchesBefore || matchesIntended ? null : "vba_package_component_diverged",
                        Message = matchesIntended
                            ? "Live component matches intended state."
                            : matchesBefore ? "Live component matches before state." : "Live component matches neither recorded state."
                    };
                }
            }
            catch (Exception ex)
            {
                return new VbaPackageMutationComponentAssessment
                {
                    ModuleName = expected.ModuleName,
                    ActualExists = null,
                    ErrorCode = "vba_package_component_read_exception",
                    Message = "Live component inspection threw an exception. " + ex.Message
                };
            }

            if (VbaReader.IsModuleNotFound(readError))
            {
                return new VbaPackageMutationComponentAssessment
                {
                    ModuleName = expected.ModuleName,
                    ActualExists = false,
                    MatchesBefore = !expected.BeforeExists,
                    MatchesIntendedAfter = !expected.IntendedAfterExists,
                    ErrorCode = expected.BeforeExists && expected.IntendedAfterExists ? "vba_package_component_diverged" : null,
                    Message = !expected.IntendedAfterExists
                        ? "Live component absence matches intended state."
                        : !expected.BeforeExists ? "Live component absence matches before state." : "Live component is unexpectedly absent."
                };
            }
            return new VbaPackageMutationComponentAssessment
            {
                ModuleName = expected.ModuleName,
                ActualExists = null,
                ErrorCode = readError == null ? "vba_package_component_read_failed" : readError.ErrorCode,
                Message = "Live component could not be inspected. " + (readError == null ? string.Empty : readError.Message)
            };
        }

        private static string PackageJournalData(
            string dataJson,
            VbaPackageMutationPreparation prepared,
            PackageMutationAssessment assessment,
            bool terminalRecorded = true)
        {
            JObject data;
            try
            {
                data = string.IsNullOrWhiteSpace(dataJson) ? new JObject() : JObject.Parse(dataJson);
            }
            catch (JsonException)
            {
                data = new JObject { ["operationData"] = dataJson ?? string.Empty };
            }
            data.Remove("journalStatus");
            data.Remove("packageJournalStatus");
            data.Remove("terminalRecorded");
            data.Remove("componentAssessments");
            var rename = prepared != null && string.Equals(prepared.Operation, "rename", StringComparison.OrdinalIgnoreCase);
            if (rename)
            {
                data["journaled"] = true;
                data["mutationId"] = prepared.MutationId;
            }
            else
            {
                data["packageJournaled"] = true;
                data["packageMutationId"] = prepared == null ? null : prepared.MutationId;
            }
            if (!terminalRecorded) data["terminalRecorded"] = false;
            data["componentAssessments"] = assessment == null
                ? new JArray()
                : JArray.FromObject(assessment.Components ?? new System.Collections.Generic.List<VbaPackageMutationComponentAssessment>());
            return data.ToString(Formatting.None);
        }

        private static void TraceMutation(VbaPackageMutationPreparation prepared, string stage, string status)
        {
            if (prepared == null) return;
            RunCausalTrace.Record(new CausalTraceRecord
            {
                Stage = stage,
                StepId = prepared.StepId,
                ToolCallId = prepared.ToolCallId,
                DocumentRuntimeId = prepared.RuntimeDocumentKey,
                MutationId = prepared.MutationId,
                JournalRunId = prepared.RunId,
                Status = status,
                Boundary = "vba_package_mutation"
            });
        }

        private sealed class PackageMutationAssessment
        {
            public string Status { get; set; }
            public System.Collections.Generic.List<VbaPackageMutationComponentAssessment> Components { get; set; }
            public string ErrorCode { get; set; }
            public string Message { get; set; }
        }
    }
}
