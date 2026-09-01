using System;
using Newtonsoft.Json.Linq;
using RNAssistant.Office.Vba;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class VbaToolExecutor
    {
        internal VbaMutationOutcome ReconcilePendingMutationOutcome()
        {
            try
            {
                var open = _vbaJournalStore.ListOpenMutations(
                    _adapter.HostName,
                    _adapter.DocumentKey);
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

                var packageReconciliation = _packageService.ReconcilePendingMutations();
                if (packageReconciliation != null)
                {
                    return packageReconciliation;
                }
                var renameReconciliation = _mutationService.ReconcilePendingRenames();
                return renameReconciliation;
            }
            catch (Exception ex)
            {
                return VbaMutationOutcome.Error(
                    "VBA history could not be validated; the operation was blocked. " + ex.Message,
                    new JObject { ["retryable"] = false },
                    "vba_journal_unavailable",
                    false);
            }
        }

        private RNAssistant.Core.Models.ToolResult ReconcilePendingMutations()
        {
            var outcome = ReconcilePendingMutationOutcome();
            if (outcome == null) return null;
            var data = outcome.Data;
            var dataJson = data == null || !data.HasValues
                ? null : data.ToString(Newtonsoft.Json.Formatting.None);
            return outcome.Status == VbaMutationOutcomeStatus.Unknown
                ? RNAssistant.Core.Models.ToolResult.PartialFailure(
                    outcome.Message, dataJson,
                    string.IsNullOrWhiteSpace(outcome.ErrorCode)
                        ? "vba_mutation_unknown" : outcome.ErrorCode)
                : RNAssistant.Core.Models.ToolResult.Fail(
                    outcome.Message, dataJson, outcome.ErrorCode,
                    outcome.Retryable);
        }
    }
}
