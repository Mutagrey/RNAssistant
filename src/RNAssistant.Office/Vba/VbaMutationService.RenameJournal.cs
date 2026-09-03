using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Vba
{
    internal sealed partial class VbaMutationService
    {
        public VbaMutationOutcome ReconcilePendingRenames()
        {
            if (_renameJournal == null)
            {
                return VbaMutationOutcome.Error(
                    "The VBA rename journal boundary is unavailable.",
                    null,
                    "vba_journal_unavailable",
                    false);
            }
            try
            {
                foreach (var record in _renameJournal.ListOpenRenames(
                    _document.HostName,
                    _document.DocumentKey))
                {
                    if (record == null || record.Prepared == null) continue;
                    var assessment = InspectRenameMutation(record.Prepared);
                    _renameJournal.CompleteRename(
                        _document.HostName,
                        _document.DocumentKey,
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
                return VbaMutationOutcome.Error(
                    "VBA rename history could not be validated; the operation was blocked. " + ex.Message,
                    null,
                    "vba_journal_unavailable",
                    false);
            }
        }

        private RenamePreparationResult PrepareJournaledRename(
            string sourceName,
            string targetName,
            VbaModuleState source,
            VbaMutationCorrelation correlation)
        {
            if (_renameJournal == null)
            {
                return RenamePreparationResult.Failure(VbaMutationOutcome.Error(
                    "VBA rename was blocked because its journal boundary is unavailable.",
                    null,
                    "vba_journal_unavailable",
                    false));
            }
            try
            {
                correlation = correlation ?? new VbaMutationCorrelation();
                return RenamePreparationResult.Prepared(_renameJournal.PrepareRename(
                    new VbaPackageMutationPreparation
                    {
                        Operation = "rename",
                        PackageId = ToolId("vba_rename_module"),
                        PackageVersion = "1",
                        SessionOnly = false,
                        RetainBackups = false,
                        Host = _document.HostName ?? string.Empty,
                        DocumentKey = _document.DocumentKey ?? string.Empty,
                        RuntimeDocumentKey = _document.RuntimeDocumentKey ?? string.Empty,
                        DocumentTitle = _document.DocumentTitle ?? string.Empty,
                        Components = new List<VbaPackageMutationComponent>
                        {
                            new VbaPackageMutationComponent
                            {
                                ModuleName = sourceName,
                                BeforeExists = true,
                                BeforeComponentType = source.ComponentType,
                                BeforeCode = source.Code,
                                IntendedAfterExists = false
                            },
                            new VbaPackageMutationComponent
                            {
                                ModuleName = targetName,
                                BeforeExists = false,
                                IntendedAfterExists = true,
                                IntendedAfterComponentType = source.ComponentType,
                                IntendedAfterCode = source.Code
                            }
                        },
                        SessionId = correlation.SessionId ?? string.Empty,
                        RunId = correlation.RunId,
                        TurnId = correlation.TurnId,
                        StepId = correlation.StepId,
                        ToolCallId = correlation.ToolCallId
                    }));
            }
            catch (Exception ex)
            {
                return RenamePreparationResult.Failure(VbaMutationOutcome.Error(
                    "VBA rename was blocked because its prepared two-name journal record could not be saved. " + ex.Message,
                    null,
                    "vba_journal_prepare_failed",
                    false));
            }
        }

        private VbaMutationOutcome ExecuteJournaledRename(
            VbaPackageMutationPreparation prepared,
            JObject operationData,
            Func<VbaMutationActionResult> action,
            string sessionId,
            string sourceHash,
            CancellationToken cancellationToken)
        {
            TraceRenameMutation(prepared, SessionEventKind.DomainEffectPrepared, null);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                CompleteRenameCancellationBeforeDispatch(prepared);
                throw;
            }

            VbaMutationActionResult actionResult;
            try
            {
                TraceRenameMutation(prepared, SessionEventKind.DomainEffectDispatched, null);
                actionResult = action == null ? null : action();
            }
            catch (OperationCanceledException ex)
            {
                actionResult = VbaMutationActionResult.Unknown(
                    "VBA rename was cancelled after dispatch. " + ex.Message,
                    null,
                    "vba_rename_cancelled_after_dispatch");
            }
            catch (Exception ex)
            {
                actionResult = VbaMutationActionResult.Error(
                    "VBA rename threw after its prepared record was persisted. " + ex.Message,
                    null,
                    "vba_rename_exception",
                    false);
            }
            if (actionResult == null)
            {
                actionResult = VbaMutationActionResult.Error(
                    "VBA rename returned no result.",
                    null,
                    "vba_rename_missing_result",
                    false);
            }

            var assessment = InspectRenameMutation(prepared);
            if (string.Equals(
                    assessment.Status,
                    VbaMutationStatuses.NotApplied,
                    StringComparison.Ordinal) &&
                actionResult.Disposition == VbaMutationDisposition.RolledBack)
            {
                assessment.Status = VbaMutationStatuses.RolledBack;
                assessment.Message =
                    "The backend explicitly reported rollback and both component identities match the recorded before state.";
            }
            TraceRenameMutation(prepared, SessionEventKind.DomainEffectVerified, assessment.Status);
            try
            {
                _renameJournal.CompleteRename(
                    prepared.Host,
                    prepared.DocumentKey,
                    prepared.MutationId,
                    assessment.Status,
                    assessment.Components,
                    actionResult.ErrorCode ?? assessment.ErrorCode,
                    assessment.Message);
            }
            catch (Exception ex)
            {
                var terminalData = RenameJournalData(
                    actionResult.Data,
                    operationData,
                    prepared,
                    assessment);
                terminalData["terminalRecorded"] = false;
                RemoveObservation(sessionId, prepared.Components[0].ModuleName);
                RemoveObservation(sessionId, prepared.Components[1].ModuleName);
                return VbaMutationOutcome.Unknown(
                    "The VBA rename effect was inspected, but its terminal journal record could not be saved. Inspect both component identities before retrying. " + ex.Message,
                    terminalData,
                    "vba_rename_journal_terminal_failed");
            }

            var data = RenameJournalData(
                actionResult.Data,
                operationData,
                prepared,
                assessment);
            var sourceName = prepared.Components[0].ModuleName;
            var targetName = prepared.Components[1].ModuleName;
            if (string.Equals(
                assessment.Status,
                VbaMutationStatuses.Committed,
                StringComparison.Ordinal))
            {
                RemoveObservation(sessionId, sourceName);
                MarkObservationStale(sessionId, targetName, sourceHash);
                if (actionResult.Status == VbaMutationActionStatus.Error ||
                    actionResult.Status == VbaMutationActionStatus.Unknown)
                {
                    data["backendReportedError"] = true;
                    data["backendErrorCode"] = actionResult.ErrorCode;
                }
                return VbaMutationOutcome.Ok(
                    "VBA module renamed: " + sourceName + " -> " + targetName + "." +
                    (actionResult.Status == VbaMutationActionStatus.Succeeded ||
                     actionResult.Status == VbaMutationActionStatus.Verified
                        ? string.Empty
                        : " Live identities match the intended result and terminal evidence was recorded."),
                    data);
            }

            if (string.Equals(
                assessment.Status,
                VbaMutationStatuses.Unknown,
                StringComparison.Ordinal))
            {
                RemoveObservation(sessionId, sourceName);
                RemoveObservation(sessionId, targetName);
                return VbaMutationOutcome.Unknown(
                    (actionResult.Message ?? "VBA rename failed.") +
                    " Final component identity state is mixed or unknown; inspect both names before retrying.",
                    data,
                    "vba_rename_unknown");
            }

            var errorCode = actionResult.ErrorCode;
            if (string.IsNullOrWhiteSpace(errorCode))
            {
                errorCode = actionResult.Status == VbaMutationActionStatus.Succeeded ||
                    actionResult.Status == VbaMutationActionStatus.Verified
                        ? "vba_rename_verify_failed"
                        : string.Equals(
                            assessment.Status,
                            VbaMutationStatuses.RolledBack,
                            StringComparison.Ordinal)
                                ? "vba_rename_rolled_back"
                                : "vba_rename_not_applied";
            }
            return VbaMutationOutcome.Error(
                actionResult.Message,
                data,
                errorCode,
                actionResult.Retryable);
        }

        private void CompleteRenameCancellationBeforeDispatch(
            VbaPackageMutationPreparation prepared)
        {
            var assessment = InspectRenameMutation(prepared);
            TraceRenameMutation(prepared, SessionEventKind.DomainEffectVerified, assessment.Status);
            try
            {
                _renameJournal.CompleteRename(
                    prepared.Host,
                    prepared.DocumentKey,
                    prepared.MutationId,
                    assessment.Status,
                    assessment.Components,
                    "vba_rename_cancelled_before_dispatch",
                    "Cancellation was observed after preparation and before dispatch. " +
                    assessment.Message);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "VBA rename was cancelled before dispatch, but its terminal journal record could not be saved.",
                    ex);
            }
        }

        private RenameMutationAssessment InspectRenameMutation(
            VbaPackageMutationPreparation prepared)
        {
            var components = new List<VbaPackageMutationComponentAssessment>();
            foreach (var expected in prepared.Components ??
                new List<VbaPackageMutationComponent>())
            {
                components.Add(InspectRenameComponent(expected));
            }
            var allIntended = components.Count == 2 &&
                components.All(item => item.MatchesIntendedAfter);
            var allBefore = components.Count == 2 &&
                components.All(item => item.MatchesBefore);
            var failed = components.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(item.ErrorCode));
            return new RenameMutationAssessment
            {
                Status = allIntended
                    ? VbaMutationStatuses.Committed
                    : allBefore
                        ? VbaMutationStatuses.NotApplied
                        : VbaMutationStatuses.Unknown,
                Components = components,
                ErrorCode = failed == null ? null : failed.ErrorCode,
                Message = allIntended
                    ? "The old and new VBA identities match the recorded intended rename state."
                    : allBefore
                        ? "The old and new VBA identities match the recorded state before rename."
                        : "The old and new VBA identities match neither the complete before nor intended rename state."
            };
        }

        private VbaPackageMutationComponentAssessment InspectRenameComponent(
            VbaPackageMutationComponent expected)
        {
            VbaMutationReadResult read;
            try
            {
                read = _reader.ReadModule(expected.ModuleName, 1000000);
            }
            catch (Exception ex)
            {
                return new VbaPackageMutationComponentAssessment
                {
                    ModuleName = expected.ModuleName,
                    ActualExists = null,
                    ErrorCode = "vba_rename_component_read_exception",
                    Message = "Live component inspection threw an exception. " + ex.Message
                };
            }

            if (read != null && read.Success)
            {
                var actual = read.Module;
                var hash = VbaTextCanonicalizer.PackageCodeSha256(actual.Code);
                var comparableHash = VbaTextCanonicalizer.VbeComparableCodeSha256(actual.Code);
                var matchesBefore = expected.BeforeExists &&
                    MatchesRenameComponent(
                        expected.BeforeCodeSha256,
                        expected.BeforeComparableCodeSha256,
                        expected.BeforeComponentType,
                        actual,
                        hash,
                        comparableHash);
                var matchesIntended = expected.IntendedAfterExists &&
                    MatchesRenameComponent(
                        expected.IntendedAfterCodeSha256,
                        expected.IntendedAfterComparableCodeSha256,
                        expected.IntendedAfterComponentType,
                        actual,
                        hash,
                        comparableHash);
                return new VbaPackageMutationComponentAssessment
                {
                    ModuleName = expected.ModuleName,
                    ActualExists = true,
                    ActualComponentType = actual.ComponentType,
                    ActualCodeSha256 = hash,
                    ActualComparableCodeSha256 = comparableHash,
                    MatchesBefore = matchesBefore,
                    MatchesIntendedAfter = matchesIntended,
                    ErrorCode = matchesBefore || matchesIntended
                        ? null
                        : "vba_rename_component_diverged",
                    Message = matchesIntended
                        ? "Live component matches intended rename state."
                        : matchesBefore
                            ? "Live component matches before rename state."
                            : "Live component matches neither recorded rename state."
                };
            }

            if (read != null && read.IsNotFound)
            {
                var matchesBefore = !expected.BeforeExists;
                var matchesIntended = !expected.IntendedAfterExists;
                return new VbaPackageMutationComponentAssessment
                {
                    ModuleName = expected.ModuleName,
                    ActualExists = false,
                    MatchesBefore = matchesBefore,
                    MatchesIntendedAfter = matchesIntended,
                    ErrorCode = matchesBefore || matchesIntended
                        ? null
                        : "vba_rename_component_diverged",
                    Message = matchesIntended
                        ? "Live component absence matches intended rename state."
                        : matchesBefore
                            ? "Live component absence matches before rename state."
                            : "Live component is unexpectedly absent."
                };
            }

            return new VbaPackageMutationComponentAssessment
            {
                ModuleName = expected.ModuleName,
                ActualExists = null,
                ErrorCode = read == null
                    ? "vba_rename_component_read_failed"
                    : read.ErrorCode,
                Message = "Live component could not be inspected. " +
                    (read == null ? string.Empty : read.Message)
            };
        }

        private static bool MatchesRenameComponent(
            string expectedHash,
            string expectedComparableHash,
            string expectedComponentType,
            VbaModuleState actual,
            string actualHash,
            string actualComparableHash)
        {
            return actual != null &&
                VbaVerifier.MatchesRecordedState(
                    actualHash,
                    actualComparableHash,
                    expectedHash,
                    expectedComparableHash) &&
                string.Equals(
                    actual.ComponentType,
                    expectedComponentType,
                    StringComparison.OrdinalIgnoreCase) &&
                (!string.Equals(
                    expectedComponentType,
                    "MSForm",
                    StringComparison.OrdinalIgnoreCase) ||
                 actual.CodeOnlyUserForm == true);
        }

        private static JObject RenameJournalData(
            JObject actionData,
            JObject operationData,
            VbaPackageMutationPreparation prepared,
            RenameMutationAssessment assessment)
        {
            var data = VbaMutationData.Clone(actionData);
            foreach (var property in (operationData ?? new JObject()).Properties())
            {
                data[property.Name] = property.Value.DeepClone();
            }
            data.Remove("journalStatus");
            data.Remove("packageJournalStatus");
            data.Remove("terminalRecorded");
            data.Remove("componentAssessments");
            data.Remove("backendReportedError");
            data.Remove("backendErrorCode");
            data["journaled"] = true;
            data["mutationId"] = prepared == null ? null : prepared.MutationId;
            data["componentAssessments"] = assessment == null
                ? new JArray()
                : JArray.FromObject(
                    assessment.Components ??
                    new List<VbaPackageMutationComponentAssessment>());
            return data;
        }

        private static void TraceRenameMutation(
            VbaPackageMutationPreparation prepared,
            SessionEventKind kind,
            string status)
        {
            if (prepared == null) return;
            RunCausalTrace.Record(new CausalTraceRecord(kind)
            {
                StepId = prepared.StepId,
                ToolCallId = prepared.ToolCallId,
                DocumentRuntimeId = prepared.RuntimeDocumentKey,
                MutationId = prepared.MutationId,
                JournalRunId = prepared.RunId,
                Status = status,
                Boundary = "vba_rename_mutation"
            });
        }

        private sealed class RenamePreparationResult
        {
            public VbaPackageMutationPreparation Preparation { get; private set; }
            public VbaMutationOutcome Error { get; private set; }
            public bool Success { get { return Preparation != null && Error == null; } }

            public static RenamePreparationResult Prepared(
                VbaPackageMutationPreparation preparation)
            {
                return new RenamePreparationResult { Preparation = preparation };
            }

            public static RenamePreparationResult Failure(VbaMutationOutcome error)
            {
                return new RenamePreparationResult { Error = error };
            }
        }

        private sealed class RenameMutationAssessment
        {
            public string Status { get; set; }
            public List<VbaPackageMutationComponentAssessment> Components { get; set; }
            public string ErrorCode { get; set; }
            public string Message { get; set; }
        }
    }
}
