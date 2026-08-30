using System;
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
        public VbaMutationPreparationResult PrepareJournaledMutation(
            VbaModuleMutationRequest request)
        {
            if (request == null)
            {
                return new VbaMutationPreparationResult
                {
                    Error = VbaMutationOutcome.Error(
                        "VBA mutation preparation is missing.",
                        null,
                        "vba_mutation_preparation_missing",
                        false)
                };
            }

            try
            {
                var beforeExists = request.Before != null;
                var correlation = request.Correlation ?? new VbaMutationCorrelation();
                var prepared = _journal.PrepareMutation(new VbaMutationPreparation
                {
                    Operation = request.Operation,
                    Host = _document.HostName ?? string.Empty,
                    DocumentKey = _document.DocumentKey ?? string.Empty,
                    RuntimeDocumentKey = _document.RuntimeDocumentKey ?? string.Empty,
                    DocumentTitle = _document.DocumentTitle ?? string.Empty,
                    ModuleName = request.ModuleName ?? string.Empty,
                    ComponentType = beforeExists
                        ? request.Before.ComponentType ?? string.Empty
                        : request.IntendedComponentType ?? string.Empty,
                    BeforeExists = beforeExists,
                    BeforeCodeSha256 = beforeExists ? CodeSha256(request.Before.Code) : null,
                    BeforeComparableCodeSha256 = beforeExists
                        ? VbaTextCanonicalizer.VbeComparableCodeSha256(request.Before.Code)
                        : null,
                    IntendedAfterExists = request.IntendedAfterExists,
                    IntendedAfterCodeSha256 = request.IntendedAfterExists
                        ? CodeSha256(request.IntendedAfterCode)
                        : null,
                    IntendedAfterComparableCodeSha256 = request.IntendedAfterExists
                        ? VbaTextCanonicalizer.VbeComparableCodeSha256(request.IntendedAfterCode)
                        : null,
                    SessionId = correlation.SessionId ?? string.Empty,
                    RunId = correlation.RunId,
                    TurnId = correlation.TurnId,
                    StepId = correlation.StepId,
                    ToolCallId = correlation.ToolCallId
                }, beforeExists ? request.Before.Code : null,
                   request.IntendedAfterExists ? request.IntendedAfterCode : null);
                return new VbaMutationPreparationResult { Preparation = prepared };
            }
            catch (Exception ex)
            {
                return new VbaMutationPreparationResult
                {
                    Error = VbaMutationOutcome.Error(
                        "VBA " + (request.Operation ?? "mutation") +
                        " was blocked because its prepared journal record could not be saved. " + ex.Message,
                        null,
                        "vba_journal_prepare_failed",
                        false)
                };
            }
        }

        public VbaMutationOutcome ExecuteJournaledMutation(
            VbaMutationPreparation prepared,
            Func<VbaMutationActionResult> action,
            CancellationToken cancellationToken)
        {
            if (prepared == null)
            {
                return VbaMutationOutcome.Error(
                    "VBA mutation preparation is missing.",
                    null,
                    "vba_mutation_preparation_missing",
                    false);
            }

            TraceMutation(prepared, SessionEventKind.DomainEffectPrepared, null);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                CompleteCancelledBeforeDispatch(prepared);
                throw;
            }

            VbaMutationActionResult actionResult;
            try
            {
                TraceMutation(prepared, SessionEventKind.DomainEffectDispatched, null);
                actionResult = action == null ? null : action();
            }
            catch (OperationCanceledException ex)
            {
                actionResult = VbaMutationActionResult.Unknown(
                    "VBA mutation was cancelled after dispatch. " + ex.Message,
                    null,
                    "vba_mutation_cancelled_after_dispatch");
            }
            catch (Exception ex)
            {
                actionResult = VbaMutationActionResult.Error(
                    "VBA mutation threw after its prepared record was persisted. " + ex.Message,
                    null,
                    "vba_mutation_exception",
                    false);
            }
            if (actionResult == null)
            {
                actionResult = VbaMutationActionResult.Error(
                    "VBA mutation returned no result.",
                    null,
                    "vba_mutation_missing_result",
                    false);
            }

            VbaMutationAssessment assessment;
            if (actionResult.Status == VbaMutationActionStatus.Verified)
            {
                assessment = VbaVerifier.CommittedAssessment(prepared, actionResult);
            }
            else
            {
                assessment = _verifier.InspectMutation(prepared);
                if (string.Equals(
                        assessment.Status,
                        VbaMutationStatuses.NotApplied,
                        StringComparison.Ordinal) &&
                    actionResult.Disposition == VbaMutationDisposition.RolledBack)
                {
                    assessment.Status = VbaMutationStatuses.RolledBack;
                    assessment.Message =
                        "The backend explicitly reported rollback and live state matches the recorded before state.";
                }
            }

            TraceMutation(prepared, SessionEventKind.DomainEffectVerified, assessment.Status);
            try
            {
                _journal.CompleteMutation(
                    prepared.Host,
                    prepared.DocumentKey,
                    prepared.MutationId,
                    assessment.Status,
                    assessment.ActualExists,
                    assessment.ActualCodeSha256,
                    assessment.ActualComparableCodeSha256,
                    actionResult.ErrorCode ?? assessment.ErrorCode,
                    assessment.Message);
            }
            catch (Exception ex)
            {
                var terminalFailureData = JournalData(actionResult.Data, prepared, assessment);
                terminalFailureData["terminalRecorded"] = false;
                return VbaMutationOutcome.Unknown(
                    "The VBA effect was inspected, but its terminal journal record could not be saved. " +
                    "Inspect the module before retrying. " + ex.Message,
                    terminalFailureData,
                    "vba_journal_terminal_failed");
            }

            var data = JournalData(actionResult.Data, prepared, assessment);
            if (string.Equals(assessment.Status, VbaMutationStatuses.Committed, StringComparison.Ordinal))
            {
                if (actionResult.Status != VbaMutationActionStatus.Verified)
                {
                    data["backendReportedError"] =
                        actionResult.Status == VbaMutationActionStatus.Error ||
                        actionResult.Status == VbaMutationActionStatus.Unknown;
                    if (!string.IsNullOrWhiteSpace(actionResult.ErrorCode))
                    {
                        data["backendErrorCode"] = actionResult.ErrorCode;
                    }
                }
                var message = actionResult.Status == VbaMutationActionStatus.Verified
                    ? actionResult.Message
                    : (actionResult.Message ?? "VBA mutation reported an error.") +
                      " Live state matches the intended result and terminal evidence was recorded.";
                return VbaMutationOutcome.Ok(
                    message,
                    data);
            }

            if (string.Equals(assessment.Status, VbaMutationStatuses.Unknown, StringComparison.Ordinal))
            {
                return VbaMutationOutcome.Unknown(
                    (actionResult.Message ?? "VBA mutation failed.") +
                    " Final VBA state is unknown; inspect it or explicitly restore a backup before retrying.",
                    data,
                    "vba_mutation_unknown");
            }

            var errorCode = actionResult.ErrorCode;
            if (string.IsNullOrWhiteSpace(errorCode))
            {
                errorCode = string.Equals(
                    assessment.Status,
                    VbaMutationStatuses.RolledBack,
                    StringComparison.Ordinal)
                    ? "vba_mutation_rolled_back"
                    : "vba_mutation_not_applied";
            }
            return VbaMutationOutcome.Error(
                actionResult.Message,
                data,
                errorCode,
                actionResult.Retryable);
        }

        public VbaMutationAssessment InspectMutation(VbaMutationPreparation prepared)
        {
            return _verifier.InspectMutation(prepared);
        }

        internal static VbaMutationCorrelation CorrelationFrom(
            VbaMutationGuard guard,
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

        private void CompleteCancelledBeforeDispatch(VbaMutationPreparation prepared)
        {
            var assessment = _verifier.InspectMutation(prepared);
            TraceMutation(prepared, SessionEventKind.DomainEffectVerified, assessment.Status);
            try
            {
                _journal.CompleteMutation(
                    prepared.Host,
                    prepared.DocumentKey,
                    prepared.MutationId,
                    assessment.Status,
                    assessment.ActualExists,
                    assessment.ActualCodeSha256,
                    assessment.ActualComparableCodeSha256,
                    "vba_mutation_cancelled_before_dispatch",
                    "Cancellation was observed after preparation and before dispatch. " +
                    assessment.Message);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "VBA mutation was cancelled before dispatch, but the terminal journal record could not be saved. " +
                    "The prepared record must be reconciled on the next safe access.",
                    ex);
            }
        }

        private static JObject JournalData(
            JObject actionData,
            VbaMutationPreparation prepared,
            VbaMutationAssessment assessment)
        {
            var data = VbaMutationData.Clone(actionData);
            data.Remove("journalStatus");
            data.Remove("packageJournalStatus");
            data.Remove("terminalRecorded");
            data.Remove("actualExists");
            data.Remove("actualCodeSha256");
            data.Remove("backendReportedError");
            data.Remove("backendErrorCode");
            data.Remove("compileValidation");
            data["journaled"] = true;
            data["mutationId"] = prepared == null ? null : prepared.MutationId;
            data["rollbackBackupId"] = prepared == null || string.IsNullOrWhiteSpace(prepared.BackupId)
                ? null
                : prepared.BackupId;
            data["actualExists"] = assessment == null ? null : assessment.ActualExists;
            if (assessment != null && !string.IsNullOrWhiteSpace(assessment.ActualCodeSha256))
            {
                data["actualCodeSha256"] = assessment.ActualCodeSha256;
            }
            return data;
        }

        private static void TraceMutation(
            VbaMutationPreparation prepared,
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
                Boundary = "vba_mutation"
            });
        }
    }
}
