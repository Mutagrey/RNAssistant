using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Vba
{
    internal sealed partial class VbaMutationService
    {
        public bool TryPrepareJournaledMutation(
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
                prepared = _journalStore.PrepareMutation(new VbaMutationPreparation
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
                        ? VbaTextCanonicalizer.VbeComparableCodeSha256(before.Code)
                        : null,
                    IntendedAfterExists = intendedAfterExists,
                    IntendedAfterCodeSha256 = intendedAfterExists ? CodeSha256(intendedAfterCode) : null,
                    IntendedAfterComparableCodeSha256 = intendedAfterExists
                        ? VbaTextCanonicalizer.VbeComparableCodeSha256(intendedAfterCode)
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

        public ToolResult ExecuteJournaledMutation(
            VbaMutationPreparation prepared,
            Func<ToolResult> action)
        {
            TraceMutation(prepared, "domain.effect.prepared", null);
            ToolResult result;
            Exception actionException = null;
            try
            {
                if (action != null) TraceMutation(prepared, "domain.effect.dispatched", null);
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

            VbaMutationAssessment assessment;
            if (result.Success)
            {
                assessment = VbaVerifier.CommittedAssessment(prepared, result);
            }
            else
            {
                assessment = _verifier.InspectMutation(prepared);
                if (string.Equals(assessment.Status, VbaMutationStatuses.NotApplied, StringComparison.Ordinal) &&
                    ReportsRollback(result, actionException))
                {
                    assessment.Status = VbaMutationStatuses.RolledBack;
                    assessment.Message = "The runtime reported rollback and live state matches the recorded before state.";
                }
            }

            TraceMutation(prepared, "domain.effect.verified", assessment.Status);
            try
            {
                _journalStore.CompleteMutation(
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

        public VbaMutationAssessment InspectMutation(VbaMutationPreparation prepared)
        {
            return _verifier.InspectMutation(prepared);
        }

        internal static bool ReportsRollback(ToolResult result, Exception exception)
        {
            var message = ((result == null ? null : result.Message) ?? string.Empty) + " " +
                (exception == null ? string.Empty : exception.Message ?? string.Empty);
            return message.IndexOf("was restored", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("was removed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("rolled back", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string JournalData(
            string dataJson,
            VbaMutationPreparation prepared,
            string status,
            VbaMutationAssessment assessment)
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
            data["rollbackBackupId"] = prepared == null || string.IsNullOrWhiteSpace(prepared.BackupId)
                ? null
                : prepared.BackupId;
            data["journalStatus"] = status;
            data["actualExists"] = assessment == null ? null : assessment.ActualExists;
            if (assessment != null && !string.IsNullOrWhiteSpace(assessment.ActualCodeSha256))
            {
                data["actualCodeSha256"] = assessment.ActualCodeSha256;
            }
            return data.ToString(Formatting.None);
        }

        private static void TraceMutation(
            VbaMutationPreparation prepared,
            string stage,
            string status)
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
                Boundary = "vba_mutation"
            });
        }
    }
}
