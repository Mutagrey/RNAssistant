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
        public const int ProtocolVersion = 6;

        public const string ToolAction = "tool";
        public const string ContinueAction = "continue";
        public const string DoneAction = "done";
        public const string BlockedAction = "blocked";
        public const string NeedsInputAction = "needs_input";

        public string Message { get; private set; }
        public string Action { get; private set; }
        public bool Final { get { return Action == DoneAction || Action == BlockedAction || Action == NeedsInputAction; } }
        public IReadOnlyList<ConversationToolCall> ToolCalls { get; private set; }

        internal ConversationResponse(string message, IEnumerable<ConversationToolCall> calls, string action)
        {
            var snapshot = (calls ?? new ConversationToolCall[0]).ToArray();
            if (!IsValidAction(action, snapshot.Length)) throw new ArgumentException("Action and tool calls disagree.", nameof(action));
            if ((action == ContinueAction || action == BlockedAction || action == NeedsInputAction) &&
                string.IsNullOrWhiteSpace(message))
                throw new ArgumentException("A progress or unfinished action requires a concrete message.", nameof(message));
            Message = message;
            Action = action;
            ToolCalls = Array.AsReadOnly(snapshot);
        }

        public static bool IsValidAction(string action, int callCount)
        {
            return action == ToolAction ? callCount > 0 :
                (action == ContinueAction || action == DoneAction || action == BlockedAction || action == NeedsInputAction) && callCount == 0;
        }

        // Use this canonical writer for model envelopes, not serialization of a runtime DTO.
        public string ToJson()
        {
            return new JObject
            {
                ["message"] = Message,
                ["action"] = Action,
                ["tool_calls"] = new JArray(ToolCalls.Select(call => new JObject
                {
                    ["name"] = call.Name,
                    ["arguments"] = call.Arguments.DeepClone()
                }))
            }.ToString(Formatting.None);
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
