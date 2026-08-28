using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Office.Services;
using LegacyResult = RNAssistant.Core.Models.ToolResult;
using TerminalResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Runtime
{
    // Active domain executors still own their richer internal results. This one-way
    // boundary uses the runtime's outcome, never message text, to create model data.
    // Remove with the last domain handler migration; it does not read legacy history.
    internal static class LegacyToolResultAdapter
    {
        internal static ToolResultMaterialization Materialize(LegacyResult legacy, ToolExecutionOutcome outcome)
        {
            if (legacy == null) throw new ArgumentNullException(nameof(legacy));
            if (outcome == ToolExecutionOutcome.AwaitingConfirmation ||
                AgentTranscript.IsWaitingResult(legacy) || AgentTranscript.IsAwaitingUserResult(legacy))
                throw new InvalidOperationException("Runtime pauses are not terminal model results.");
            var status = outcome == ToolExecutionOutcome.Ok ? ToolResultStatus.Ok :
                outcome == ToolExecutionOutcome.Unknown ? ToolResultStatus.Unknown : ToolResultStatus.Error;
            var data = NormalizeData(legacy.DataJson);
            if (status != ToolResultStatus.Ok)
            {
                var code = string.IsNullOrWhiteSpace(legacy.ErrorCode)
                    ? (status == ToolResultStatus.Unknown ? "tool_effect_uncertain" : "tool_failed") : legacy.ErrorCode;
                var body = data as JObject;
                // Preserve a domain code or scalar payload as details if it conflicts
                // with the runtime error code, rather than silently overwriting it.
                if (body == null || body["code"] != null && (body["code"].Type != JTokenType.String ||
                    !string.Equals((string)body["code"], code, StringComparison.Ordinal)))
                    body = new JObject { ["details"] = data };
                body["code"] = code;
                data = body;
            }
            var terminal = new TerminalResult(status, legacy.Message, data.ToString(Formatting.None), legacy.ModelResourceRefs);
            return new ToolResultMaterialization(terminal, legacy.ModelAttachments,
                legacy.ModelResultResourceRef, legacy.ModelResultResourceKind);
        }

        private static JToken NormalizeData(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return JValue.CreateNull();
            try
            {
                return JsonConvert.DeserializeObject<JToken>(value,
                    new JsonSerializerSettings { DateParseHandling = DateParseHandling.None }) ?? JValue.CreateNull();
            }
            catch (JsonException) { return new JValue(value); }
        }
    }
}
