using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    internal static class ToolRunResultFactory
    {
        internal static ToolRunResult Create(ToolExecutionRecord record,
            ToolResultMaterialization materialized = null)
        {
            if (record == null) return ToolRunResult.Error(
                "Tool runtime returned no execution record.", null,
                "missing_execution_record", false);
            var terminal = materialized == null
                ? record.Result : materialized.Result;
            var data = terminal == null ? null : terminal.DataJson;
            ToolRunResult result;
            if (record.Outcome == ToolExecutionOutcome.AwaitingConfirmation)
            {
                result = ToolRunResult.AwaitingConfirmation(
                    record.Message,
                    record.ConfirmationDataJson,
                    record.Context.Policy.Revision);
                result.PendingId = record.PendingId;
            }
            else if (record.AwaitingUser)
            {
                result = ToolRunResult.AwaitingUser(record.Message, data);
            }
            else if (record.Outcome == ToolExecutionOutcome.Ok)
            {
                result = ToolRunResult.Ok(record.Message, data);
            }
            else if (record.Outcome == ToolExecutionOutcome.Unknown)
            {
                string code;
                bool? retryable;
                ToolResultSummaryProjection.ReadErrorMetadata(data, out code, out retryable);
                result = ToolRunResult.Unknown(record.Message, data,
                    code ?? "tool_effect_uncertain");
                result.Retryable = retryable ?? false;
            }
            else if (record.WasCancelled)
            {
                result = ToolRunResult.Cancelled(record.Message);
            }
            else
            {
                string code;
                bool? retryable;
                ToolResultSummaryProjection.ReadErrorMetadata(data, out code, out retryable);
                result = ToolRunResult.Error(record.Message, data, code ??
                    (record.Outcome == ToolExecutionOutcome.NotDispatched
                        ? "tool_not_dispatched"
                        : record.Context.IsConfirmed && !record.MayHaveDispatched
                        ? "pending_tool_catalog_changed"
                        : "tool_execution_failed"), retryable ?? false);
            }
            result.ToolStepsConsumed = record.ToolStepsConsumed;
            result.Recovery = record.Recovery;
            if (record.Outcome == ToolExecutionOutcome.Ok && !record.AwaitingUser && record.Result != null)
                result.ResultSummary = ToolResultSummaryProjection.Summarize(record.Context.Call.Name, record.Result.DataJson);
            if (terminal != null) result.ModelResourceRefs = terminal.Resources;
            if (materialized != null)
            {
                result.ModelAttachments = materialized.ModelAttachments;
                if (result.ResultSummary != null)
                    result.ResultSummary.HydratedForNextModelStep = materialized.ModelAttachments.Count > 0;
                result.ModelResultResourceRef = materialized.ResultResource;
                result.ModelResultResourceKind =
                    materialized.ResultResourceKind;
            }
            return result;
        }

    }
}
