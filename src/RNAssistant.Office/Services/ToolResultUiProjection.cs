using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Tools;
using LegacyResult = RNAssistant.Core.Models.ToolResult;

namespace RNAssistant.Office.Services
{
    // Existing activity/manual-command consumers use this DTO until their own
    // switch. This projection is never fed back to Tool Result v1 serialization.
    internal static class ToolResultUiProjection
    {
        internal static LegacyResult Create(ToolExecutionRecord record)
        {
            var data = record.Result == null ? null : record.Result.DataJson;
            LegacyResult result;
            if (record.Outcome == ToolExecutionOutcome.AwaitingConfirmation)
            {
                result = LegacyResult.WaitingConfirmation(record.Message);
                result.PendingId = record.PendingId;
            }
            else if (record.AwaitingUser) result = LegacyResult.AwaitingUser(record.Message, data);
            else if (record.Outcome == ToolExecutionOutcome.Ok) result = LegacyResult.Ok(record.Message, data);
            else
            {
                string errorCode;
                bool? retryable;
                ReadErrorMetadata(data, out errorCode, out retryable);
                result = LegacyResult.Fail(record.Message, data, errorCode ??
                    (record.Context.IsConfirmed && !record.MayHaveDispatched && record.Outcome == ToolExecutionOutcome.Error
                        ? "pending_tool_catalog_changed" : "execution_interrupted"), retryable ?? false);
                if (record.Outcome == ToolExecutionOutcome.Unknown) result.Status = "unknown";
                else if (record.Outcome == ToolExecutionOutcome.NotDispatched) result.Status = "cancelled";
            }
            if (record.Result != null) result.ModelResourceRefs = record.Result.Resources;
            result.ToolStepsConsumed = record.ToolStepsConsumed;
            return result;
        }

        private static void ReadErrorMetadata(string data, out string code, out bool? retryable)
        {
            code = null;
            retryable = null;
            if (string.IsNullOrWhiteSpace(data)) return;
            try
            {
                var root = JsonConvert.DeserializeObject<JObject>(data,
                    new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
                if (root == null) return;
                if (root["code"] != null && root["code"].Type == JTokenType.String)
                    code = (string)root["code"];
                if (root["retryable"] != null && root["retryable"].Type == JTokenType.Boolean)
                    retryable = (bool)root["retryable"];
            }
            catch (JsonException) { }
        }

        internal static void IncludeResources(LegacyResult uiResult, ToolResultMaterialization materialized)
        {
            if (uiResult == null || materialized == null) return;
            uiResult.ModelResourceRefs = materialized.Result.Resources;
            uiResult.ModelResultResourceRef = materialized.ResultResource;
            uiResult.ModelResultResourceKind = materialized.ResultResourceKind;
        }
    }
}
