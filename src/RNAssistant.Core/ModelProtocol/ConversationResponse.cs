using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.ModelProtocol
{
    // Status-free wire contract, separate from runtime lifecycle/effect projections.
    public sealed class ConversationResponse
    {
        public const int ProtocolVersion = 3;

        public string Message { get; private set; }
        public IReadOnlyList<AgentToolCall> ToolCalls { get; private set; }

        internal ConversationResponse(string message, IEnumerable<AgentToolCall> calls)
        {
            Message = message;
            ToolCalls = new List<AgentToolCall>(calls).AsReadOnly();
        }

        // Use this canonical writer for model envelopes, not serialization of a runtime DTO.
        public string ToJson()
        {
            return new JObject
            {
                ["message"] = Message,
                ["tool_calls"] = new JArray(ToolCalls.Select(call => new JObject
                {
                    ["id"] = call.Id,
                    ["name"] = call.Name,
                    ["arguments"] = JObject.FromObject(call.Arguments)
                }))
            }.ToString(Formatting.None);
        }
    }

    public sealed class ConversationResponseParseResult
    {
        public ConversationResponse Response { get; private set; }
        public string Error { get; private set; }
        public bool Success { get { return Response != null; } }

        private ConversationResponseParseResult() { }

        internal static ConversationResponseParseResult Ok(ConversationResponse response)
        {
            return new ConversationResponseParseResult { Response = response };
        }

        internal static ConversationResponseParseResult Fail(string error)
        {
            return new ConversationResponseParseResult { Error = error };
        }
    }
}
