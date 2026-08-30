using System;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class VbaToolExecutor
    {
        private ToolResult ReconcilePendingMutations()
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
                    return VbaMutationToolResultMapper.ToToolResult(packageReconciliation);
                }
                var renameReconciliation = _mutationService.ReconcilePendingRenames();
                return renameReconciliation == null
                    ? null
                    : VbaMutationToolResultMapper.ToToolResult(renameReconciliation);
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
    }
}
