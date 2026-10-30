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

        public const string PartialFinishRecheckGuidance =
            "The proposed finish with outcome=partial was not accepted yet. Reassess the unfinished task against the available tools and permissions. " +
            "If a feasible authorized step remains, choose execute now and wait for its result; ask indispensable questions through the question tool. " +
            "If no feasible step remains, finish with outcome=partial and state the concrete obstacle and unfinished result. " +
            "Do not repeat accepted calls or claim that a read proves an unperformed change.";

        public const string FinishGuidance =
            "finish is a final assertion and answer, never a question, request for approval, offer to continue or promise of future work. " +
            "Required outcome is the model's task assessment: completed (all results satisfied), partial (some achieved and some incomplete work: partially_satisfied or a mix), " +
            "or unable_to_complete (no satisfied/partially_satisfied task results). Assess requested outcomes, not incidental successful discovery steps. " +
            "reason briefly explains why finishing is justified now; message must be nonblank and give the actual answer/result, a concise explanation and any unfinished work or verification limits. " +
            "For a simple direct answer, one clear sentence is enough. Use the user's language. " +
            "Outcome and result assessments are model claims; runtime checks evidence independently. " +
            "If indispensable user input is needed, choose execute with the available question tool. " +
            "After finding a fixable defect, take the next authorized action and continue feasible work before finishing. " +
            "For partial or unable_to_complete, explain the concrete obstacle to further work.";

        private const string ReflectionInstruction =
            "Choose reflection with a nonblank decision only when a useful interpretation is needed before the next model request. Reflection executes nothing, creates no tool result and grants no permissions; it is optional and bounded by REFLECTION_BUDGET; once an approach is chosen, execute the next useful step. ";
        private const string SchemaReflectionInstruction =
            "Choose reflection only to settle uncertainty within the remaining allowance, then act on the decision. ";
        private const string ReflectionExample =
            "{\"action\":\"reflection\",\"reason\":\"Choose the approach.\",\"decision\":\"Read the exact procedure before patching.\",\"message\":\"\"}\n";
        private const string DisabledReflectionInstruction =
            "Only execute and finish are available. Reflection is disabled for this run. ";

        public const string ActionGuidance =
            "Return exactly one conversation-response-v8 JSON object directly, without a response wrapper. Order fields: action, reason, action-specific fields, message last. " +
            "reason is a brief decision basis, not private reasoning. Choose execute with nonempty tool_calls when an authorized tool step remains. " +
            "Each call has only name and object arguments; arguments is the tool schema's root object, without another wrapper. Runtime assigns T1 to a call and E1 only to an eligible terminal result; R1@v2 identifies an observed resource version. T/R are not finish evidence; R is not a tool argument. Reference lookup is case-insensitive; targets and source text are unchanged. Historical calls may include runtime-added id T1, but new calls must never include id. Batch only independent local reads; return other calls alone and wait for their results. " +
            ReflectionInstruction +
            "Choose finish with a results array to end the run; deliver the answer or outcomes now, not a promise. Each result has criterion (an original user requirement), evidence (issued eligible references such as E1 from terminal results of this run), and assessment (satisfied, partially_satisfied, unverified or not_satisfied). " +
            "Without eligible evidence results or evidence may be empty, including after discovery/schema/skill loading. When eligible results exist, cite at least one delivered E reference. Never invent evidence or convert failed/unknown outcomes into verified effects. " +
            "execute forbids decision/results/outcome; reflection forbids tool_calls/results/outcome; finish forbids tool_calls/decision. No extra root fields. " +
            FinishGuidance + " Keep the envelope even when the task cannot be fulfilled.";

        public const string JsonObjectGuidance = ActionGuidance + "\n" +
            "Shape examples only (TOOL_NAME and FIELD are placeholders, not callable names or arguments):\n" +
            "{\"action\":\"execute\",\"reason\":\"Inspect the source.\",\"tool_calls\":[{\"name\":\"TOOL_NAME\",\"arguments\":{\"FIELD\":\"value\"}}],\"message\":\"I will inspect the source first.\"}\n" +
            ReflectionExample +
            "{\"action\":\"finish\",\"reason\":\"The direct question is answered.\",\"outcome\":\"completed\",\"results\":[],\"message\":\"Answer to the user.\"}\n" +
            "After eligible terminal results, include criteria citing their delivered E references.";

        public const string SchemaGuidance =
            "Return the supplied json_schema exactly: the response property contains the conversation-response-v8 decision. " +
            "Choose execute when a useful authorized tool step remains; batch only independent local reads. " +
            SchemaReflectionInstruction +
            "Choose finish only to deliver the answer or honest outcomes now. Assess original user criteria; cite issued eligible evidence (E1, E2) from terminal results of this run. Discovery/schema/skill loading can finish with empty evidence. " +
            "Model assessments do not prove effects; failed or unknown outcomes cannot be reported as verified changes. " +
            "Runtime assigns T1 to a call and E1 to an eligible terminal result; R1@v2 is an observed resource version. T/R are never finish evidence; R is never a tool argument. Reference lookup is case-insensitive, without changing targets or source text. Ask indispensable questions through the question tool. Follow the current REFLECTION_BUDGET. " +
            "Do not repeat accepted calls or invent evidence. " + FinishGuidance;

        public const string StringEscapingGuidance =
            "Use exactly one JSON escaping layer in every string, including nested tool arguments: encode a real line break as \\n and one literal source backslash as \\\\. " +
            "Runtime decodes the envelope once and preserves argument text; never strip or pre-unescape a backslash.";

        public const string MessageGuidance =
            "message is useful user-facing text; only execute/reflection may use an empty string. On the first execute of a substantial task, state the approach briefly and include the next concrete call. " +
            "On later turns report only new relevant findings, decisions, contradictions, blockers or meaningful stage changes. " +
            "Use message=\"\" for routine execute/reflection continuation, including skill/schema loading. Do not repeat earlier progress, arguments or the plan. " +
            "For finish, give a nonblank final answer/result, concise explanation of the conclusion and any limitations; no questions to the user or offers to continue. Give decision reasons, not private reasoning or invented findings.";

        public static string ResponseGuidance(bool schemaTransport, bool reflectionEnabled)
        {
            if (reflectionEnabled) return schemaTransport ? SchemaGuidance : JsonObjectGuidance;
            // Transform only these owned constants, never arbitrary user/history text.
            return schemaTransport ? SchemaGuidance.Replace(SchemaReflectionInstruction, DisabledReflectionInstruction) :
                JsonObjectGuidance.Replace(ReflectionInstruction, DisabledReflectionInstruction)
                    .Replace(ReflectionExample, string.Empty).Replace("reflection forbids tool_calls/results/outcome; ", string.Empty);
        }

        public static string MessageGuidanceFor(bool reflectionEnabled)
        { return reflectionEnabled ? MessageGuidance : MessageGuidance.Replace("execute/reflection", "execute"); }

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
            string decision = null, IEnumerable<ConversationResultClaim> results = null, ConversationOutcome? outcome = null)
        {
            if (action == ConversationResponse.ExecuteAction)
                throw new ArgumentException("An accepted no-call decision is required.", nameof(action));
            return new ConversationResponse(action, reason, message,
                decision: decision, results: results, outcome: outcome).ToJson();
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
