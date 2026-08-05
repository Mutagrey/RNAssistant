using System;
using System.Collections.Generic;

namespace RNAssistant.Core.Models
{
    public static class AgentDecisionProtocol
    {
        public const int Version = 1;
        public const string SchemaName = "rnassistant_agent_decision_v1";
    }

    public static class AgentResponseKinds
    {
        public const string Plan = "plan";
        public const string Tool = "tool";
        public const string Clarify = "clarify";
        public const string Final = "final";
        public const string CannotComplete = "cannot_complete";
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
        public int ProtocolVersion { get; set; }
        public string Kind { get; set; }
        public string DecisionSummary { get; set; }
        public string Goal { get; set; }
        public List<AgentPlanStep> Plan { get; set; }
        public AgentPlannerStep Tool { get; set; }
        public string Message { get; set; }

        public AgentPlannerResponse()
        {
            ProtocolVersion = AgentDecisionProtocol.Version;
            Plan = new List<AgentPlanStep>();
        }
    }

    public sealed class AgentPlanStep
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Status { get; set; }
    }

    public sealed class AgentPlannerStep
    {
        public string ToolId { get; set; }
        public Dictionary<string, object> Arguments { get; set; }
        public string Reason { get; set; }
        public string ToolCallId { get; set; }

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

        public bool Success
        {
            get { return Response != null && string.IsNullOrWhiteSpace(ErrorCode); }
        }

        public static AgentPlannerParseResult Ok(AgentPlannerResponse response)
        {
            return new AgentPlannerParseResult { Response = response };
        }

        public static AgentPlannerParseResult Fail(string code, string message)
        {
            return new AgentPlannerParseResult { ErrorCode = code, ErrorMessage = message };
        }
    }
}
