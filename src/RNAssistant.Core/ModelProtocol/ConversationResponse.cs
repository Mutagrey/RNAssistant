using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Core.ModelProtocol
{
    // Model intent is separate from runtime lifecycle/effect projections.
    public sealed class ConversationResponse
    {
        public const int ProtocolVersion = 8;
        public const int ReferenceContractVersion = 2;
        public const string ExecuteAction = "execute";
        public const string ReflectionAction = "reflection";
        public const string FinishAction = "finish";

        private readonly ConversationToolCall[] _calls;
        public string Action { get; private set; }
        public string Reason { get; private set; }
        public string Message { get; private set; }
        public string Decision { get; private set; }
        public ConversationOutcome? Outcome { get; private set; }
        public string OutcomeName { get { return Outcome.HasValue ? ConversationFinishContract.Name(Outcome.Value) : null; } }
        public bool Final { get { return Action == FinishAction; } }
        public IReadOnlyList<ConversationToolCall> ToolCalls { get { return Array.AsReadOnly(_calls.Select(CloneCall).ToArray()); } }
        public IReadOnlyList<ConversationResultClaim> Results { get; private set; }

        internal ConversationResponse(string action, string reason, string message,
            IEnumerable<ConversationToolCall> calls = null, string decision = null,
            IEnumerable<ConversationResultClaim> results = null, ConversationOutcome? outcome = null)
        {
            var snapshot = (calls ?? new ConversationToolCall[0]).ToArray();
            if (!IsValidAction(action, snapshot.Length)) throw new ArgumentException("Action and tool calls disagree.", nameof(action));
            if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reason is required.", nameof(reason));
            if (message == null) throw new ArgumentNullException(nameof(message));
            if (action == ExecuteAction && (decision != null || results != null) ||
                action == ReflectionAction && (calls != null || results != null || string.IsNullOrWhiteSpace(decision)) ||
                action == FinishAction && (calls != null || decision != null || results == null))
                throw new ArgumentException("Fields do not match the selected action.");
            if (snapshot.Any(call => call == null || string.IsNullOrWhiteSpace(call.Name) || call.Arguments == null))
                throw new ArgumentException("A call requires a name and object arguments.", nameof(calls));
            var claims = results == null ? null : results.ToArray();
            if (claims != null && claims.Any(claim => claim == null))
                throw new ArgumentException("Results cannot contain null claims.", nameof(results));
            ConversationFinishContract.Validate(action, message, outcome, claims);
            Action = action;
            Reason = reason;
            Message = message;
            Decision = decision;
            Outcome = outcome;
            _calls = snapshot.Select(CloneCall).ToArray();
            Results = claims == null ? null : Array.AsReadOnly(claims);
        }

        public static bool IsValidAction(string action, int callCount)
        {
            return action == ExecuteAction ? callCount > 0 && callCount <= ConversationResponseSchemaBuilder.MaximumToolCalls :
                (action == ReflectionAction || action == FinishAction) && callCount == 0;
        }

        // Use this canonical writer for model envelopes, not serialization of a runtime DTO.
        public string ToJson()
        {
            var root = new JObject { ["action"] = Action, ["reason"] = Reason };
            if (Action == ExecuteAction)
                root["tool_calls"] = new JArray(_calls.Select(call => new JObject
                {
                    ["name"] = call.Name,
                    ["arguments"] = call.Arguments.DeepClone()
                }));
            else if (Action == ReflectionAction)
                root["decision"] = Decision;
            else
            {
                root["outcome"] = OutcomeName;
                root["results"] = new JArray(Results.Select(claim => new JObject
                {
                    ["summary"] = claim.Summary,
                    ["evidence"] = new JArray(claim.Evidence),
                    ["assessment"] = claim.AssessmentName
                }));
            }
            root["message"] = Message;
            return root.ToString(Formatting.None);
        }

        private static ConversationToolCall CloneCall(ConversationToolCall call)
        {
            return new ConversationToolCall { Name = call.Name, Arguments = (JObject)call.Arguments.DeepClone() };
        }
    }

    // A claim remains model interpretation; it does not upgrade runtime evidence.
    public enum ConversationAssessment { Satisfied, PartiallySatisfied, Unverified, NotSatisfied }

    // Overall task assessment by the model, never a runtime lifecycle/effect verdict.
    public enum ConversationOutcome { Completed, Partial, UnableToComplete }

    public static class ConversationFinishContract
    {
        public static string Name(ConversationOutcome outcome)
        {
            switch (outcome)
            {
                case ConversationOutcome.Completed: return "completed";
                case ConversationOutcome.Partial: return "partial";
                case ConversationOutcome.UnableToComplete: return "unable_to_complete";
                default: throw new ArgumentOutOfRangeException(nameof(outcome));
            }
        }

        public static bool TryRead(string value, out ConversationOutcome outcome)
        {
            switch (value)
            {
                case "completed": outcome = ConversationOutcome.Completed; return true;
                case "partial": outcome = ConversationOutcome.Partial; return true;
                case "unable_to_complete": outcome = ConversationOutcome.UnableToComplete; return true;
                default: outcome = default(ConversationOutcome); return false;
            }
        }

        public static string Contradiction(ConversationOutcome outcome, IEnumerable<ConversationAssessment> assessments)
        {
            var values = assessments.ToArray();
            switch (outcome)
            {
                case ConversationOutcome.Completed:
                    return values.Any(value => value != ConversationAssessment.Satisfied)
                        ? "completed requires every result to be satisfied; report unfinished or unverified work honestly." : null;
                case ConversationOutcome.Partial:
                    return !values.Any(value => value == ConversationAssessment.Satisfied || value == ConversationAssessment.PartiallySatisfied) ||
                        !values.Any(value => value != ConversationAssessment.Satisfied)
                        ? "partial requires some achieved work and some incomplete work: partially_satisfied or a mix of satisfied and unsatisfied results." : null;
                case ConversationOutcome.UnableToComplete:
                    return values.Any(value => value == ConversationAssessment.Satisfied || value == ConversationAssessment.PartiallySatisfied)
                        ? "unable_to_complete cannot claim satisfied or partially_satisfied results; use partial for achieved task outcomes." : null;
                default: throw new ArgumentOutOfRangeException(nameof(outcome));
            }
        }

        internal static void Validate(string action, string message, ConversationOutcome? outcome,
            IReadOnlyList<ConversationResultClaim> results)
        {
            if (action != ConversationResponse.FinishAction)
            {
                if (outcome.HasValue) throw new ArgumentException("Only finish may contain outcome.", nameof(outcome));
                return;
            }
            if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("finish requires a nonblank answer.", nameof(message));
            if (!outcome.HasValue) throw new ArgumentException("finish requires an explicit model outcome.", nameof(outcome));
            if (results == null) throw new ArgumentNullException(nameof(results));
            var error = Contradiction(outcome.Value, results.Select(claim => claim.Assessment));
            if (error != null) throw new ArgumentException(error, nameof(results));
        }
    }

    public sealed class ConversationResultClaim
    {
        public string Summary { get; private set; }
        public IReadOnlyList<string> Evidence { get; private set; }
        public ConversationAssessment Assessment { get; private set; }
        public string AssessmentName
        {
            get
            {
                switch (Assessment)
                {
                    case ConversationAssessment.Satisfied: return "satisfied";
                    case ConversationAssessment.PartiallySatisfied: return "partially_satisfied";
                    case ConversationAssessment.Unverified: return "unverified";
                    case ConversationAssessment.NotSatisfied: return "not_satisfied";
                    default: throw new InvalidOperationException("Unknown assessment.");
                }
            }
        }

        public ConversationResultClaim(string summary, IEnumerable<string> evidence, ConversationAssessment assessment)
        {
            if (string.IsNullOrWhiteSpace(summary)) throw new ArgumentException("A summary is required.", nameof(summary));
            if (evidence == null) throw new ArgumentNullException(nameof(evidence));
            var refs = evidence.ToArray();
            if (refs.Any(string.IsNullOrWhiteSpace) || refs.Distinct(StringComparer.Ordinal).Count() != refs.Length)
                throw new ArgumentException("Evidence references must be nonblank and unique within a claim.", nameof(evidence));
            if (!Enum.IsDefined(typeof(ConversationAssessment), assessment)) throw new ArgumentOutOfRangeException(nameof(assessment));
            Summary = summary;
            Evidence = Array.AsReadOnly(refs);
            Assessment = assessment;
        }

        internal static bool TryReadAssessment(string value, out ConversationAssessment assessment)
        {
            switch (value)
            {
                case "satisfied": assessment = ConversationAssessment.Satisfied; return true;
                case "partially_satisfied": assessment = ConversationAssessment.PartiallySatisfied; return true;
                case "unverified": assessment = ConversationAssessment.Unverified; return true;
                case "not_satisfied": assessment = ConversationAssessment.NotSatisfied; return true;
                default: assessment = default(ConversationAssessment); return false;
            }
        }
    }

    // A validated model proposal has no execution identity. The runtime assigns
    // IDs when accepting the response, before persistence or tool dispatch.
    public sealed class ConversationToolCall
    {
        public string Name { get; set; }
        public JObject Arguments { get; set; }

        public ConversationToolCall()
        {
            Arguments = new JObject();
        }
    }

    public enum ConversationResponseError
    {
        None, InvalidJson, InvalidAction, WrongActionFields, MissingReason, MissingMessage,
        EmptyExecuteCalls, TooManyCalls, InvalidToolCall, MissingDecision, MissingFinishResults,
        InvalidFinishResults, InvalidAssessment, DuplicateEvidence, InvalidContext,
        UnknownTool, SchemaNotLoaded, InvalidToolSchema, InvalidArguments, InvalidBatch,
        EmptyFinishMessage, InvalidOutcome, ContradictoryOutcome, ReflectionDisabled
    }

    public sealed class ConversationResponseParseResult
    {
        public ConversationResponse Response { get; private set; }
        public ConversationResponseError ErrorKind { get; private set; }
        public string ErrorCode { get { return Code(ErrorKind); } }
        public string Error { get; private set; }
        public bool Success { get { return Response != null; } }

        private ConversationResponseParseResult() { }

        internal static ConversationResponseParseResult Ok(ConversationResponse response)
        {
            return new ConversationResponseParseResult { Response = response };
        }

        internal static ConversationResponseParseResult Fail(ConversationResponseError kind, string error)
        {
            if (kind == ConversationResponseError.None) throw new ArgumentException("An error kind is required.", nameof(kind));
            return new ConversationResponseParseResult { ErrorKind = kind, Error = error };
        }

        internal static string Code(ConversationResponseError kind)
        {
            switch (kind)
            {
                case ConversationResponseError.None: return null;
                case ConversationResponseError.InvalidJson: return "invalid_json";
                case ConversationResponseError.InvalidAction: return "invalid_action";
                case ConversationResponseError.WrongActionFields: return "wrong_action_fields";
                case ConversationResponseError.MissingReason: return "missing_reason";
                case ConversationResponseError.MissingMessage: return "missing_message";
                case ConversationResponseError.EmptyFinishMessage: return "empty_finish_message";
                case ConversationResponseError.InvalidOutcome: return "invalid_outcome";
                case ConversationResponseError.ContradictoryOutcome: return "contradictory_outcome";
                case ConversationResponseError.ReflectionDisabled: return "reflection_disabled";
                case ConversationResponseError.EmptyExecuteCalls: return "empty_execute_calls";
                case ConversationResponseError.TooManyCalls: return "too_many_calls";
                case ConversationResponseError.InvalidToolCall: return "invalid_tool_call";
                case ConversationResponseError.MissingDecision: return "missing_decision";
                case ConversationResponseError.MissingFinishResults: return "missing_finish_results";
                case ConversationResponseError.InvalidFinishResults: return "invalid_finish_results";
                case ConversationResponseError.InvalidAssessment: return "invalid_assessment";
                case ConversationResponseError.DuplicateEvidence: return "duplicate_evidence";
                case ConversationResponseError.InvalidContext: return "invalid_context";
                case ConversationResponseError.UnknownTool: return "unknown_tool";
                case ConversationResponseError.SchemaNotLoaded: return "schema_not_loaded";
                case ConversationResponseError.InvalidToolSchema: return "invalid_tool_schema";
                case ConversationResponseError.InvalidArguments: return "invalid_arguments";
                case ConversationResponseError.InvalidBatch: return "invalid_batch";
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }
    }
}
