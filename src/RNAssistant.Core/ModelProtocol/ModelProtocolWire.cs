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
            "Return exactly one conversation-response-v8 JSON object directly, without a response wrapper. Order fields: action, reason, action-specific fields, message last. " +
            "reason is a brief decision basis, not private reasoning. Choose execute with nonempty tool_calls when an authorized tool step remains. " +
            "Each call has only name and object arguments; arguments is the tool schema's root object, without another wrapper. Runtime assigns IDs; E1 labels an operation/outcome, R1 a resource and R1@v2 an observed version. Resource labels are not evidence of execution or tool arguments. Historical calls may include runtime-added id E1, but new calls must never include id. Batch only independent local reads; return other calls alone and wait for their results. " +
            "Choose reflection with a nonblank decision only when a useful interpretation is needed before the next model request. Reflection executes nothing, creates no tool result and grants no permissions; it is optional and bounded by REFLECTION_BUDGET; once an approach is chosen, execute the next useful step. " +
            "Choose finish with a results array to end the run; deliver the answer or outcomes now, not a promise. Each result has summary, evidence (short execution references such as E1 from terminal results of this run), and assessment (satisfied, partially_satisfied, unverified or not_satisfied). " +
            "Without accepted calls results may be empty. After accepted calls include results and at least one real terminal reference. Never invent evidence or convert failed/unknown outcomes into verified effects. " +
            "execute forbids decision/results; reflection forbids tool_calls/results; finish forbids tool_calls/decision. No extra root fields. " +
            "Assessments are model claims, not runtime success. Ask indispensable questions through the available question tool; explain unresolved outcomes truthfully. Keep the envelope even when the task cannot be fulfilled.";

        public const string JsonObjectGuidance = ActionGuidance + "\n" +
            "Shape examples only (TOOL_NAME and FIELD are placeholders, not callable names or arguments):\n" +
            "{\"action\":\"execute\",\"reason\":\"Inspect the source.\",\"tool_calls\":[{\"name\":\"TOOL_NAME\",\"arguments\":{\"FIELD\":\"value\"}}],\"message\":\"I will inspect the source first.\"}\n" +
            "{\"action\":\"reflection\",\"reason\":\"Choose the approach.\",\"decision\":\"Read the exact procedure before patching.\",\"message\":\"\"}\n" +
            "{\"action\":\"finish\",\"reason\":\"The direct question is answered.\",\"results\":[],\"message\":\"Answer to the user.\"}\n" +
            "After tool calls, include result claims citing real terminal IDs instead of empty results.";

        public const string SchemaGuidance =
            "Return the supplied json_schema exactly: the response property contains the conversation-response-v8 decision. " +
            "Choose execute when a useful authorized tool step remains; batch only independent local reads. " +
            "Choose reflection only to settle uncertainty within the remaining allowance, then act on the decision. " +
            "Choose finish only to deliver the answer or honest outcomes now. After tool calls cite their short execution references (E1, E2) from terminal results of this run. " +
            "Model assessments do not prove effects; failed or unknown outcomes cannot be reported as verified changes. " +
            "Runtime assigns call IDs; E1 labels an operation/outcome, R1@v2 an observed resource version, never a tool argument or finish evidence. Ask indispensable questions through the question tool. Follow the current REFLECTION_BUDGET. " +
            "Do not repeat accepted calls or invent evidence.";

        public const string StringEscapingGuidance =
            "Use exactly one JSON escaping layer in every string, including nested tool arguments: encode a real line break as \\n and one literal source backslash as \\\\. " +
            "Runtime decodes the envelope once and preserves argument text; never strip or pre-unescape a backslash.";

        public const string MessageGuidance =
            "message is useful user-facing text; an empty string is allowed. On the first execute of a substantial task, state the approach briefly and include the next concrete call. " +
            "On later turns report only new relevant findings, decisions, contradictions, blockers or meaningful stage changes. " +
            "Use message=\"\" for routine continuation, including skill/schema loading. Do not repeat earlier progress, arguments or the plan. " +
            "For finish, deliver the answer or outcomes and verification limits. Give concise decision reasons, not private reasoning or invented findings.";

        public static ConversationResponseParseResult Parse(string content, IEnumerable<ToolCatalogEntry> callableTools,
            IEnumerable<ToolCatalogEntry> runnableCatalog, ModelProtocolCallContext context, bool schemaTransport = false)
        {
            return new ConversationResponseParser().Parse(content, callableTools, runnableCatalog, context, schemaTransport);
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
                ["instruction"] = JsonObjectGuidance + " " + MessageGuidance + " " +
                    "Repair only the rejected proposal against the same current task and callable schemas. No call from the rejected response was accepted or executed. " +
                    "Do not replay previously accepted operations. Never relabel a promise as finish to repair empty_execute_calls. " +
                    "Inside a call, arguments is already the root object described by its schema; do not nest arguments, parameters or a wrapper. " +
                    "For an undeclared property, remove it; if it wrongly wraps declared fields, move those fields up first. " +
                    StringEscapingGuidance + " No Markdown or surrounding prose."
            }.ToString(Formatting.None);
        }
    }
}
