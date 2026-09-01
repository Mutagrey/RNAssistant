using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Office.Vba;
using RNAssistant.Office.Domains.Vba;

namespace RNAssistant.Office.Tools
{
    // Removed with the remaining controller ToolResult boundary in 11T9B.
    internal static class VbaLegacyResultProjection
    {
        public static ToolResult ToToolResult(VbaMutationOutcome outcome)
        {
            if (outcome == null)
                return ToolResult.PartialFailure(
                    "VBA mutation returned no typed outcome.",
                    null,
                    "vba_mutation_missing_outcome");
            var data = outcome.Data;
            var dataJson = data == null || !data.HasValues
                ? null : data.ToString(Formatting.None);
            if (outcome.Status == VbaMutationOutcomeStatus.Ok)
                return ToolResult.Ok(outcome.Message, dataJson);
            if (outcome.Status == VbaMutationOutcomeStatus.Unknown)
                return ToolResult.PartialFailure(
                    outcome.Message,
                    dataJson,
                    string.IsNullOrWhiteSpace(outcome.ErrorCode)
                        ? "vba_mutation_unknown" : outcome.ErrorCode);
            return ToolResult.Fail(
                outcome.Message,
                dataJson,
                outcome.ErrorCode,
                outcome.Retryable);
        }

        public static ToolResult ToToolResult(VbaBackendActionResult result)
        {
            if (result == null)
                return ToolResult.PartialFailure(
                    "VBA backend returned no typed outcome.",
                    null,
                    "vba_backend_missing_result");
            var data = result.Data;
            var dataJson = data == null || !data.HasValues
                ? null : data.ToString(Formatting.None);
            if (result.Status == VbaBackendActionStatus.Ok)
                return ToolResult.Ok(result.Message, dataJson);
            if (result.Status == VbaBackendActionStatus.Unknown)
                return ToolResult.PartialFailure(
                    result.Message,
                    dataJson,
                    result.ErrorCode);
            return ToolResult.Fail(
                result.Message,
                dataJson,
                result.ErrorCode,
                result.Retryable);
        }
    }
}
