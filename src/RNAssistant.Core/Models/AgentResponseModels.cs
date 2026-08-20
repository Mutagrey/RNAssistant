using System;
using System.Collections.Generic;

namespace RNAssistant.Core.Models
{
    public sealed class AgentResponse
    {
        public string Message { get; set; }
        public AgentToolCall ToolCall { get; set; }
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
