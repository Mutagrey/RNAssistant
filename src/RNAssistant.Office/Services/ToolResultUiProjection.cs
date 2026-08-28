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
                result = LegacyResult.Fail(record.Message, data, ReadErrorCode(data) ??
                    (record.Context.IsConfirmed && !record.MayHaveDispatched && record.Outcome == ToolExecutionOutcome.Error
                        ? "pending_tool_catalog_changed" : "execution_interrupted"), false);
                if (record.Outcome == ToolExecutionOutcome.Unknown) result.Status = "unknown";
                else if (record.Outcome == ToolExecutionOutcome.NotDispatched) result.Status = "cancelled";
            }
            if (record.Result != null) result.ModelResourceRefs = record.Result.Resources;
            result.ToolStepsConsumed = record.ToolStepsConsumed;
            return result;
        }

        private static string ReadErrorCode(string data)
        {
            if (string.IsNullOrWhiteSpace(data)) return null;
            try
            {
                var root = JsonConvert.DeserializeObject<JObject>(data,
                    new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
                return root != null && root["code"] != null && root["code"].Type == JTokenType.String
                    ? (string)root["code"] : null;
            }
            catch (JsonException) { return null; }
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
