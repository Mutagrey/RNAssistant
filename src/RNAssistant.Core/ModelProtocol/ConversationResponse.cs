using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Core.ModelProtocol
{
    // Status-free wire contract, separate from runtime lifecycle/effect projections.
    public sealed class ConversationResponse
    {
        public const int ProtocolVersion = 4;

        public string Message { get; private set; }
        public IReadOnlyList<ConversationToolCall> ToolCalls { get; private set; }

        internal ConversationResponse(string message, IEnumerable<ConversationToolCall> calls)
        {
            Message = message;
            ToolCalls = new List<ConversationToolCall>(calls).AsReadOnly();
        }

        // Use this canonical writer for model envelopes, not serialization of a runtime DTO.
        public string ToJson()
        {
            return new JObject
            {
                ["message"] = Message,
                ["tool_calls"] = new JArray(ToolCalls.Select(call => new JObject
                {
                    ["name"] = call.Name,
                    ["arguments"] = JObject.FromObject(call.Arguments)
                }))
            }.ToString(Formatting.None);
        }
    }

    // A validated model proposal has no execution identity. The runtime assigns
    // IDs when accepting the response, before persistence or tool dispatch.
    public sealed class ConversationToolCall
    {
        public string Name { get; set; }
        public Dictionary<string, object> Arguments { get; set; }

        public ConversationToolCall()
        {
            Arguments = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
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
