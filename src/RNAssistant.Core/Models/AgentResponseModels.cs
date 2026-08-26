using System;
using System.Collections.Generic;

namespace RNAssistant.Core.Models
{
    public static class AgentResponseProtocol
    {
        public const int CurrentVersion = 3;
    }

    public static class AgentResponseStatuses
    {
        public const string InProgress = "in_progress";
        public const string Completed = "completed";
        public const string AwaitingUser = "awaiting_user";
        public const string Blocked = "blocked";
        public const string Refused = "refused";
        public const string Planned = "planned";

        public static bool IsKnown(string value)
        {
            return string.Equals(value, InProgress, StringComparison.Ordinal) ||
                string.Equals(value, Completed, StringComparison.Ordinal) ||
                string.Equals(value, AwaitingUser, StringComparison.Ordinal) ||
                string.Equals(value, Blocked, StringComparison.Ordinal) ||
                string.Equals(value, Refused, StringComparison.Ordinal) ||
                string.Equals(value, Planned, StringComparison.Ordinal);
        }

        public static bool IsTerminal(string value)
        {
            return IsKnown(value) && !string.Equals(value, InProgress, StringComparison.Ordinal);
        }
    }

    public sealed class AgentResponse
    {
        // Runtime projection derived only from tool_calls. It is not a model-facing field.
        public string Status { get; set; }
        public string Message { get; set; }
        public List<AgentToolCall> ToolCalls { get; set; }

        public AgentResponse()
        {
            ToolCalls = new List<AgentToolCall>();
        }
    }

    public sealed class AgentToolCall
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public Dictionary<string, object> Arguments { get; set; }

        public AgentToolCall()
        {
            Arguments = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public sealed class AgentResponseParseResult
    {
        public AgentResponse Response { get; set; }
        public string Error { get; set; }

        public bool Success
        {
            get { return Response != null && string.IsNullOrWhiteSpace(Error); }
        }

        public static AgentResponseParseResult Ok(AgentResponse response)
        {
            return new AgentResponseParseResult { Response = response };
        }

        public static AgentResponseParseResult Fail(string error)
        {
            return new AgentResponseParseResult { Error = error };
        }
    }
}
