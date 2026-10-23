using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.ModelProtocol
{
    // One logical wire contract for model attempts, transcript envelopes and probes.
    // Candidate v8 replaces v7; no aliases or version-selecting decoder.
    public static partial class ModelProtocolWire
    {
        internal const int MaximumFormatRepairErrorCharacters = 1024;

        public const string UnknownEffectGuidance =
            "An unknown effect requires investigation. Before finish, successfully read the affected current state after the unconfirmed operation. " +
            "Compare it with the requested outcome, then continue feasible authorized work. Do not repeat the uncertain call unchanged or disguise a retry by changing arguments. " +
            "A read alone does not prove the intended change, and an unrelated success does not resolve it. " +
            "Report concrete obstacles and unfinished outcomes honestly; unverified does not waive required inspection. " +
            "Do not ask the user to verify state that available read tools can inspect.";

        public const string ActionGuidance =
            "Return exactly one conversation-response-v8 JSON object. Order fields: action, reason, action-specific fields, message last. " +
            "reason is a brief decision basis, not private reasoning. Choose execute with nonempty tool_calls when an authorized tool step remains. " +
            "Each call has only name and object arguments; runtime assigns IDs. Batch only independent local reads; return other calls alone and wait for their results. " +
            "Choose reflection with a nonblank decision only when a useful interpretation is needed before the next model request. Reflection executes nothing, creates no tool result and grants no permissions; it is optional and bounded. " +
            "Choose finish with a results array to end the run; deliver the answer or outcomes now, not a promise. Each result has summary, evidence (terminal runtime tool_call_id strings), and assessment (satisfied, partially_satisfied, unverified or not_satisfied). " +
            "Without accepted calls results may be empty. After accepted calls include results and at least one real terminal reference. Never invent evidence or convert failed/unknown outcomes into verified effects. " +
            "execute forbids decision/results; reflection forbids tool_calls/results; finish forbids tool_calls/decision. No extra root fields. " +
            "Assessments are model claims, not runtime success. Ask indispensable questions through the available question tool; explain unresolved outcomes truthfully.";

        public const string MessageGuidance =
            "message is useful user-facing text; an empty string is allowed. On the first execute of a substantial task, state the approach briefly and include the next concrete call. " +
            "On later turns report only new relevant findings, decisions, contradictions, blockers or meaningful stage changes. " +
            "Use message=\"\" for routine continuation, including skill/schema loading. Do not repeat earlier progress, arguments or the plan. " +
            "For finish, deliver the answer or outcomes and verification limits. Give concise decision reasons, not private reasoning or invented findings.";

        public static ConversationResponseParseResult Parse(string content, IEnumerable<ToolCatalogEntry> callableTools,
            IEnumerable<ToolCatalogEntry> runnableCatalog, ModelProtocolCallContext context)
        {
            return new ConversationResponseParser().Parse(content, callableTools, runnableCatalog, context);
        }

        public static string Write(ConversationResponse response)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            return response.ToJson();
        }

        // Runtime splits a validated batch into per-call history records. Keep the
        // same v8 shape and reason without making call IDs model-owned fields.
        public static string WriteAcceptedCall(string reason, string message, ConversationToolCall call)
        {
            if (call == null) throw new ArgumentNullException(nameof(call));
            return new ConversationResponse(ConversationResponse.ExecuteAction, reason,
                message ?? string.Empty, calls: new[] { call }).ToJson();
        }

        public static string WriteAcceptedDecision(string action, string reason, string message,
            string decision = null, IEnumerable<ConversationResultClaim> results = null)
        {
            if (action == ConversationResponse.ExecuteAction)
                throw new ArgumentException("An accepted no-call decision is required.", nameof(action));
            return new ConversationResponse(action, reason, message,
                decision: decision, results: results).ToJson();
        }

        internal static string FormatRepairContent(ConversationResponseError kind, string error, int attempt, int maxAttempts)
        {
            if (kind == ConversationResponseError.None) throw new ArgumentException("A format error is required.", nameof(kind));
            var boundedError = string.IsNullOrWhiteSpace(error) ? "Invalid conversation response." : error.Trim();
            if (boundedError.Length > MaximumFormatRepairErrorCharacters)
                boundedError = boundedError.Substring(0, MaximumFormatRepairErrorCharacters) + "...[truncated]";
            return "FORMAT_REPAIR:\n" + new JObject
            {
                ["code"] = ConversationResponseParseResult.Code(kind),
                ["error"] = boundedError, ["attempt"] = attempt, ["max_attempts"] = maxAttempts,
                ["instruction"] = ActionGuidance + " " + MessageGuidance + " " +
                    "Repair only the rejected proposal against the same current task and callable schemas. No call from the rejected response was accepted or executed. " +
                    "Do not replay previously accepted operations. Never relabel a promise as finish to repair empty_execute_calls. " +
                    "Inside a call, arguments is already the root object described by its schema; do not nest arguments, parameters or a wrapper. " +
                    "For an undeclared property, remove it; if it wrongly wraps declared fields, move those fields up first. " +
                    "Use one JSON escaping layer: \\n for a real line break and \\\\ for one literal source backslash. No Markdown or surrounding prose."
            }.ToString(Formatting.None);
        }
    }
}
