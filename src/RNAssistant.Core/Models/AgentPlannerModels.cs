using System;
using System.Collections.Generic;

namespace RNAssistant.Core.Models
{
    public static class AgentResponseKinds
    {
        public const string ToolPlan = "tool_plan";
        public const string Final = "final";
        public const string Clarify = "clarify";
        public const string CannotDo = "cannot_do";
    }

    public static class AgentIntents
    {
        public const string Read = "read";
        public const string Analyze = "analyze";
        public const string Mutate = "mutate";
        public const string Verify = "verify";
        public const string Answer = "answer";
        public const string Clarify = "clarify";
    }

    public static class AgentPhases
    {
        public const string ReadOnly = "read_only_phase";
        public const string Mutation = "mutation_phase";
        public const string Verification = "verification_phase";
        public const string Final = "final_phase";
    }

    public sealed class AgentPlannerResponse
    {
        public string Kind { get; set; }
        public string Intent { get; set; }
        public string Message { get; set; }
        public List<AgentPlannerStep> Steps { get; set; }
        public string ExpectedOutcome { get; set; }

        public AgentPlannerResponse()
        {
            Steps = new List<AgentPlannerStep>();
        }
    }

    public sealed class AgentPlannerStep
    {
        public string ToolId { get; set; }
        public Dictionary<string, object> Arguments { get; set; }
        public string Reason { get; set; }

        public AgentPlannerStep()
        {
            Arguments = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public sealed class AgentPlannerParseResult
    {
        public AgentPlannerResponse Response { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }
        public string SourceFormat { get; set; }
        public string NormalizedText { get; set; }

        public bool Success
        {
            get { return Response != null && string.IsNullOrWhiteSpace(ErrorCode); }
        }

        public static AgentPlannerParseResult Ok(AgentPlannerResponse response, string sourceFormat = "strict_json", string normalizedText = null)
        {
            return new AgentPlannerParseResult
            {
                Response = response,
                SourceFormat = sourceFormat,
                NormalizedText = normalizedText
            };
        }

        public static AgentPlannerParseResult Fail(string code, string message, string sourceFormat = "unknown", string normalizedText = null)
        {
            return new AgentPlannerParseResult
            {
                ErrorCode = code,
                ErrorMessage = message,
                SourceFormat = sourceFormat,
                NormalizedText = normalizedText
            };
        }
    }
}
