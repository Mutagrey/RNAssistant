using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

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
                    var assessment = InspectMutation(record.Prepared);
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
                    var assessment = InspectPackageMutation(record.Prepared, null, null);
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

        private bool TryPrepareJournaledMutation(
            ToolCommand command,
            ChatSession session,
            string operation,
            string moduleName,
            VbaModuleState before,
            bool intendedAfterExists,
            string intendedAfterCode,
            string intendedComponentType,
            out VbaMutationPreparation prepared,
            out ToolResult error)
        {
            prepared = null;
            error = null;
            try
            {
                var guard = ReadGuard(command);
                var beforeExists = before != null;
                prepared = _vbaJournalStore.PrepareMutation(new VbaMutationPreparation
                {
                    Operation = operation,
                    Host = _adapter.HostName ?? string.Empty,
                    DocumentKey = _adapter.DocumentKey ?? string.Empty,
                    RuntimeDocumentKey = _adapter.RuntimeDocumentKey ?? string.Empty,
                    DocumentTitle = _adapter.DocumentTitle ?? string.Empty,
                    ModuleName = moduleName ?? string.Empty,
                    ComponentType = beforeExists
                        ? before.ComponentType ?? string.Empty
                        : intendedComponentType ?? string.Empty,
                    BeforeExists = beforeExists,
                    BeforeCodeSha256 = beforeExists ? CodeSha256(before.Code) : null,
                    BeforeComparableCodeSha256 = beforeExists
                        ? VbaToolManifestParser.VbeComparableCodeSha256(before.Code)
                        : null,
                    IntendedAfterExists = intendedAfterExists,
                    IntendedAfterCodeSha256 = intendedAfterExists ? CodeSha256(intendedAfterCode) : null,
                    IntendedAfterComparableCodeSha256 = intendedAfterExists
                        ? VbaToolManifestParser.VbeComparableCodeSha256(intendedAfterCode)
                        : null,
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
                }, beforeExists ? before.Code : null, intendedAfterExists ? intendedAfterCode : null);
                return true;
            }
            catch (Exception ex)
            {
                error = ToolResult.Fail(
                    "VBA " + operation + " was blocked because its prepared journal record could not be saved. " + ex.Message,
                    null,
                    "vba_journal_prepare_failed",
                    false);
                return false;
            }
        }

        private ToolResult ExecuteJournaledMutation(VbaMutationPreparation prepared, Func<ToolResult> action)
        {
            ToolResult result;
            Exception actionException = null;
            try
            {
                result = action == null ? null : action();
            }
            catch (Exception ex)
            {
                actionException = ex;
                result = ToolResult.Fail(
                    "VBA mutation threw after its prepared record was persisted. " + ex.Message,
                    null,
                    "vba_mutation_exception",
                    false);
            }
            if (result == null)
            {
                result = ToolResult.Fail("VBA mutation returned no result.", null, "vba_mutation_missing_result", false);
            }

            MutationAssessment assessment;
            if (result.Success)
            {
                assessment = CommittedAssessment(prepared, result);
            }
            else
            {
                assessment = InspectMutation(prepared);
                if (string.Equals(assessment.Status, VbaMutationStatuses.NotApplied, StringComparison.Ordinal) &&
                    ReportsRollback(result, actionException))
                {
                    assessment.Status = VbaMutationStatuses.RolledBack;
                    assessment.Message = "The runtime reported rollback and live state matches the recorded before state.";
                }
            }

            try
            {
                _vbaJournalStore.CompleteMutation(
                    prepared.Host,
                    prepared.DocumentKey,
                    prepared.MutationId,
                    assessment.Status,
                    assessment.ActualExists,
                    assessment.ActualCodeSha256,
                    assessment.ActualComparableCodeSha256,
                    result.ErrorCode ?? assessment.ErrorCode,
                    assessment.Message);
            }
            catch (Exception ex)
            {
                return ToolResult.PartialFailure(
                    "The VBA effect was inspected as " + assessment.Status +
                    ", but its terminal journal record could not be saved. Inspect the module before retrying. " + ex.Message,
                    JournalData(result.DataJson, prepared, "unknown", assessment),
                    "vba_journal_terminal_failed");
            }

            result.DataJson = JournalData(result.DataJson, prepared, assessment.Status, assessment);
            if (string.Equals(assessment.Status, VbaMutationStatuses.Unknown, StringComparison.Ordinal))
            {
                result.Success = false;
                result.Status = "partial_failure";
                result.ErrorCode = "vba_mutation_unknown";
                result.Retryable = false;
                result.Message = (result.Message ?? "VBA mutation failed.") +
                    " Final VBA state is unknown; inspect it or explicitly restore a backup before retrying.";
            }
            else if (!result.Success && string.Equals(assessment.Status, VbaMutationStatuses.Committed, StringComparison.Ordinal))
            {
                return ToolResult.PartialFailure(
                    (result.Message ?? "VBA mutation reported failure.") +
                    " Live state matches the intended result, so the journal classified it as committed.",
                    result.DataJson,
                    "vba_mutation_committed_after_error");
            }
            return result;
        }

        private MutationAssessment InspectMutation(VbaMutationPreparation prepared)
        {
            VbaModuleState actual;
            ToolResult readError;
            bool readSucceeded;
            try
            {
                readSucceeded = TryReadVbaModule(prepared.ModuleName, 1000000, out actual, out readError);
            }
            catch (Exception ex)
            {
                return new MutationAssessment
                {
                    Status = VbaMutationStatuses.Unknown,
                    ActualExists = null,
                    ErrorCode = "vba_mutation_read_exception",
                    Message = "Live module inspection threw an exception. " + ex.Message
                };
            }
            if (readSucceeded)
            {
                var rawHash = CodeSha256(actual.Code);
                var comparableHash = VbaToolManifestParser.VbeComparableCodeSha256(actual.Code);
                if (prepared.IntendedAfterExists && MatchesRecordedState(
                    rawHash,
                    comparableHash,
                    prepared.IntendedAfterCodeSha256,
                    prepared.IntendedAfterComparableCodeSha256))
                {
                    return new MutationAssessment
                    {
                        Status = VbaMutationStatuses.Committed,
                        ActualExists = true,
                        ActualCodeSha256 = rawHash,
                        ActualComparableCodeSha256 = comparableHash,
                        Message = "Live module matches the recorded intended state."
                    };
                }
                if (prepared.BeforeExists && MatchesRecordedState(
                    rawHash,
                    comparableHash,
                    prepared.BeforeCodeSha256,
                    prepared.BeforeComparableCodeSha256))
                {
                    return new MutationAssessment
                    {
                        Status = VbaMutationStatuses.NotApplied,
                        ActualExists = true,
                        ActualCodeSha256 = rawHash,
                        ActualComparableCodeSha256 = comparableHash,
                        Message = "Live module matches the recorded before state."
                    };
                }
                return new MutationAssessment
                {
                    Status = VbaMutationStatuses.Unknown,
                    ActualExists = true,
                    ActualCodeSha256 = rawHash,
                    ActualComparableCodeSha256 = comparableHash,
                    ErrorCode = "vba_mutation_diverged",
                    Message = "Live module matches neither the recorded before nor intended state."
                };
            }

            if (IsModuleNotFound(readError))
            {
                if (!prepared.IntendedAfterExists)
                {
                    return new MutationAssessment
                    {
                        Status = VbaMutationStatuses.Committed,
                        ActualExists = false,
                        Message = "Live module absence matches the recorded intended state."
                    };
                }
                if (!prepared.BeforeExists)
                {
                    return new MutationAssessment
                    {
                        Status = VbaMutationStatuses.NotApplied,
                        ActualExists = false,
                        Message = "Live module absence matches the recorded before state."
                    };
                }
                return new MutationAssessment
                {
                    Status = VbaMutationStatuses.Unknown,
                    ActualExists = false,
                    ErrorCode = "vba_mutation_diverged",
                    Message = "Live module is absent but neither recorded state expected absence."
                };
            }

            return new MutationAssessment
            {
                Status = VbaMutationStatuses.Unknown,
                ActualExists = null,
                ErrorCode = readError == null ? "vba_mutation_read_failed" : readError.ErrorCode,
                Message = "Live module could not be inspected. " + (readError == null ? string.Empty : readError.Message)
            };
        }

        private static MutationAssessment CommittedAssessment(VbaMutationPreparation prepared, ToolResult result)
        {
            var actualHash = prepared.IntendedAfterCodeSha256;
            if (result != null && !string.IsNullOrWhiteSpace(result.DataJson))
            {
                try
                {
                    actualHash = (string)JObject.Parse(result.DataJson)["codeSha256"] ?? actualHash;
                }
                catch (JsonException)
                {
                }
            }
            return new MutationAssessment
            {
                Status = VbaMutationStatuses.Committed,
                ActualExists = prepared.IntendedAfterExists,
                ActualCodeSha256 = prepared.IntendedAfterExists ? actualHash : null,
                ActualComparableCodeSha256 = prepared.IntendedAfterExists
                    ? prepared.IntendedAfterComparableCodeSha256
                    : null,
                Message = "The VBA operation completed and its read-back verification succeeded."
            };
        }

        private static bool MatchesRecordedState(string actualRaw, string actualComparable, string expectedRaw, string expectedComparable)
        {
            if (!string.IsNullOrWhiteSpace(expectedComparable))
            {
                return string.Equals(actualComparable, expectedComparable, StringComparison.OrdinalIgnoreCase);
            }
            return !string.IsNullOrWhiteSpace(expectedRaw) &&
                string.Equals(actualRaw, expectedRaw, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ReportsRollback(ToolResult result, Exception exception)
        {
            var message = ((result == null ? null : result.Message) ?? string.Empty) + " " +
                (exception == null ? string.Empty : exception.Message ?? string.Empty);
            return message.IndexOf("was restored", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("was removed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("rolled back", StringComparison.OrdinalIgnoreCase) >= 0;
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
                var beforeExists = TryReadVbaModule(component.Name, 1000000, out before, out readError);
                if (!beforeExists && !IsModuleNotFound(readError))
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
            ToolResult result;
            Exception actionException = null;
            try
            {
                result = action == null ? null : action();
            }
            catch (Exception ex)
            {
                actionException = ex;
                result = ToolResult.Fail(
                    "VBA package mutation threw after its prepared record was persisted. " + ex.Message,
                    null,
                    "vba_package_mutation_exception",
                    false);
            }
            if (result == null)
            {
                result = ToolResult.Fail("VBA package mutation returned no result.", null, "vba_package_mutation_missing_result", false);
            }

            var assessment = InspectPackageMutation(prepared, result, actionException);
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
                    "The VBA package effect was inspected as " + assessment.Status +
                    ", but its terminal journal record could not be saved. Inspect all package components before retrying. " + ex.Message,
                    PackageJournalData(result.DataJson, prepared, "unknown", assessment),
                    "vba_package_journal_terminal_failed");
            }

            result.DataJson = PackageJournalData(result.DataJson, prepared, assessment.Status, assessment);
            if (string.Equals(assessment.Status, VbaMutationStatuses.Unknown, StringComparison.Ordinal))
            {
                result.Success = false;
                result.Status = "partial_failure";
                result.ErrorCode = "vba_package_mutation_unknown";
                result.Retryable = false;
                result.Message = (result.Message ?? "VBA package mutation failed.") +
                    " Final component state is mixed or unknown; inspect it before retrying.";
            }
            else if (result.Success && !string.Equals(assessment.Status, VbaMutationStatuses.Committed, StringComparison.Ordinal))
            {
                return ToolResult.PartialFailure(
                    (result.Message ?? "VBA package mutation reported success.") +
                    " Read-back did not match the complete intended package state.",
                    result.DataJson,
                    "vba_package_verify_failed");
            }
            else if (!result.Success && string.Equals(assessment.Status, VbaMutationStatuses.Committed, StringComparison.Ordinal))
            {
                return ToolResult.PartialFailure(
                    (result.Message ?? "VBA package mutation reported failure.") +
                    " Live components match the intended result, so the journal classified it as committed.",
                    result.DataJson,
                    "vba_package_mutation_committed_after_error");
            }
            return result;
        }

        private PackageMutationAssessment InspectPackageMutation(
            VbaPackageMutationPreparation prepared,
            ToolResult result,
            Exception exception)
        {
            var components = new System.Collections.Generic.List<VbaPackageMutationComponentAssessment>();
            foreach (var expected in prepared.Components ?? new System.Collections.Generic.List<VbaPackageMutationComponent>())
            {
                components.Add(InspectPackageComponent(expected));
            }
            var allIntended = components.Count > 0 && components.All(item => item.MatchesIntendedAfter);
            var allBefore = components.Count > 0 && components.All(item => item.MatchesBefore);
            var status = allIntended
                ? VbaMutationStatuses.Committed
                : allBefore
                    ? ReportsRollback(result, exception) ? VbaMutationStatuses.RolledBack : VbaMutationStatuses.NotApplied
                    : VbaMutationStatuses.Unknown;
            var failed = components.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.ErrorCode));
            return new PackageMutationAssessment
            {
                Status = status,
                Components = components,
                ErrorCode = failed == null ? null : failed.ErrorCode,
                Message = allIntended
                    ? "Every live package component matches the recorded intended state."
                    : allBefore
                        ? "Every live package component matches the recorded before state."
                        : "Package components do not collectively match either the complete before or intended state."
            };
        }

        private VbaPackageMutationComponentAssessment InspectPackageComponent(VbaPackageMutationComponent expected)
        {
            VbaModuleState actual;
            ToolResult readError;
            try
            {
                if (TryReadVbaModule(expected.ModuleName, 1000000, out actual, out readError))
                {
                    var hash = VbaToolManifestParser.CodeSha256(actual.Code);
                    var matchesBefore = expected.BeforeExists &&
                        string.Equals(hash, expected.BeforeCodeSha256, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(actual.ComponentType, expected.BeforeComponentType, StringComparison.OrdinalIgnoreCase) &&
                        (!string.Equals(expected.BeforeComponentType, "MSForm", StringComparison.OrdinalIgnoreCase) || actual.CodeOnlyUserForm == true);
                    var matchesIntended = expected.IntendedAfterExists &&
                        string.Equals(hash, expected.IntendedAfterCodeSha256, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(actual.ComponentType, expected.IntendedAfterComponentType, StringComparison.OrdinalIgnoreCase) &&
                        (!string.Equals(expected.IntendedAfterComponentType, "MSForm", StringComparison.OrdinalIgnoreCase) || actual.CodeOnlyUserForm == true);
                    return new VbaPackageMutationComponentAssessment
                    {
                        ModuleName = expected.ModuleName,
                        ActualExists = true,
                        ActualComponentType = actual.ComponentType,
                        ActualCodeSha256 = hash,
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

            if (IsModuleNotFound(readError))
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
            string status,
            PackageMutationAssessment assessment)
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
            data["packageJournaled"] = true;
            data["packageMutationId"] = prepared == null ? null : prepared.MutationId;
            data["packageJournalStatus"] = status;
            data["componentAssessments"] = assessment == null
                ? new JArray()
                : JArray.FromObject(assessment.Components ?? new System.Collections.Generic.List<VbaPackageMutationComponentAssessment>());
            return data.ToString(Formatting.None);
        }

        private static string JournalData(
            string dataJson,
            VbaMutationPreparation prepared,
            string status,
            MutationAssessment assessment)
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
            data["journaled"] = true;
            data["mutationId"] = prepared == null ? null : prepared.MutationId;
            data["rollbackBackupId"] = prepared == null || string.IsNullOrWhiteSpace(prepared.BackupId) ? null : prepared.BackupId;
            data["journalStatus"] = status;
            data["actualExists"] = assessment == null ? null : assessment.ActualExists;
            if (assessment != null && !string.IsNullOrWhiteSpace(assessment.ActualCodeSha256))
            {
                data["actualCodeSha256"] = assessment.ActualCodeSha256;
            }
            return data.ToString(Formatting.None);
        }

        private sealed class MutationAssessment
        {
            public string Status { get; set; }
            public bool? ActualExists { get; set; }
            public string ActualCodeSha256 { get; set; }
            public string ActualComparableCodeSha256 { get; set; }
            public string ErrorCode { get; set; }
            public string Message { get; set; }
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
