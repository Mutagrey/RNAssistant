using RNAssistant.Core.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.ModelProtocol
{
    // One active wire contract for model attempts, transcript envelopes and probes.
    // No version selection or historical fallback: all active responses use v7.
    public static class ModelProtocolWire
    {
        public const string ActionGuidance =
            "Use action=tool with calls to act and continue after their results. " +
            "Without calls, end the turn: done when the requested answer or work is complete, " +
            "needs_input when a required user answer is missing, or blocked when work cannot proceed. " +
            "Actions do not prove execution or effects.";

        public const string MessageGuidance =
            "Message is for useful user-facing updates, independently of tool-call or task-note frequency. " +
            "At the start of a substantial task, briefly state the approach once. On later tool turns, report only a new relevant finding, decision, contradiction, blocker or meaningful stage change. " +
            "Connect the changed understanding to its evidence or uncertainty and the next needed action; include only relevant parts. " +
            "Use message=\"\" when continuing an already explained operation without useful new information, including routine skill/schema loading or task-note updates. " +
            "Do not repeat or paraphrase earlier progress, tool arguments or the full plan. Reconcile new evidence with earlier assumptions; your own prior wording is not evidence. " +
            "Give concise decision reasons, not private reasoning or invented findings. " +
            "For done, report the answer or delivered outcomes and verification limits. For blocked or needs_input, state the unfinished outcome and concrete blocker or question.";

        public static LlmRequestOptions CreateRequestOptions(string responseMode, IEnumerable<ToolCatalogEntry> tools,
            ModelProtocolCallContext context)
        {
            var jsonSchema = string.Equals(AgentResponseModes.Normalize(responseMode),
                AgentResponseModes.JsonSchema, StringComparison.Ordinal);
            return new LlmRequestOptions
            {
                ResponseFormat = jsonSchema ? LlmResponseFormats.JsonSchema : LlmResponseFormats.JsonObject,
                ResponseSchemaName = jsonSchema ? ConversationResponseSchemaBuilder.SchemaName : null,
                ResponseSchemaJson = jsonSchema ? ConversationResponseSchemaBuilder.Build(tools, context) : null
            };
        }

        public static ConversationResponseParseResult Parse(string content, IEnumerable<ToolCatalogEntry> callableTools,
            IEnumerable<ToolCatalogEntry> runnableCatalog, ModelProtocolCallContext context)
        {
            return new ConversationResponseParser().Parse(content, callableTools, runnableCatalog, context);
        }

        public static string Write(string message, IEnumerable<ConversationToolCall> calls)
        {
            var snapshot = (calls ?? new ConversationToolCall[0]).ToArray();
            var action = snapshot.Length > 0 ? ConversationResponse.ToolAction : ConversationResponse.DoneAction;
            return new ConversationResponse(message ?? string.Empty, snapshot, action).ToJson();
        }

        public static string WriteAction(string message, string action)
        {
            return new ConversationResponse(message ?? string.Empty, new ConversationToolCall[0], action).ToJson();
        }
    }
}
