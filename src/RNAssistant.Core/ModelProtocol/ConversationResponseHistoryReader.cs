using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.ModelProtocol
{
    public sealed class ConversationHistoryReadResult
    {
        public AgentResponse Response { get; private set; }
        public string Error { get; private set; }
        public bool Success { get { return Response != null; } }

        private ConversationHistoryReadResult() { }
        internal static ConversationHistoryReadResult Ok(AgentResponse response)
        {
            return new ConversationHistoryReadResult { Response = response };
        }
        internal static ConversationHistoryReadResult Fail(string error)
        {
            return new ConversationHistoryReadResult { Error = error };
        }
    }

    // Current accepted history is not raw model wire: IDs come exclusively from
    // durable runtime metadata. Neither this reader nor replay allocates them.
    public static class ConversationResponseHistoryReader
    {
        public static ConversationHistoryReadResult Read(ChatMessage message)
        {
            if (message == null || !string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) ||
                message.Activity != null || message.ResponseProtocolVersion != ConversationResponse.ProtocolVersion)
                return ConversationHistoryReadResult.Fail("History record is not an identified v4 assistant response.");

            var nativeCalls = message.ToolCalls;
            if (nativeCalls != null && nativeCalls.Count > 0)
            {
                if (nativeCalls.Count != 1 || nativeCalls[0] == null || string.IsNullOrWhiteSpace(message.ToolName) ||
                    string.IsNullOrWhiteSpace(nativeCalls[0].ArgumentsJson) ||
                    !string.Equals(nativeCalls[0].Id, message.ToolCallId, StringComparison.Ordinal))
                    return ConversationHistoryReadResult.Fail("Native history needs one matching runtime ID, canonical ToolName and object arguments.");
                var envelope = new JObject
                {
                    ["message"] = message.Content ?? string.Empty,
                    ["tool_calls"] = new JArray(new JObject
                    {
                        ["name"] = message.ToolName,
                        ["arguments"] = new JRaw(nativeCalls[0].ArgumentsJson)
                    })
                }.ToString(Formatting.None);
                var parsed = ConversationResponseJson.Read(envelope);
                if (!parsed.Success) return ConversationHistoryReadResult.Fail(parsed.Error);
                // A raw arguments string must not inject another call or change
                // the accepted envelope. Native history keeps the exact public id.
                if (parsed.Response.ToolCalls.Count != 1 || parsed.Response.ToolCalls[0].Name != message.ToolName ||
                    parsed.Response.Message != (message.Content ?? string.Empty))
                    return ConversationHistoryReadResult.Fail("Native argument JSON changed the response envelope.");
                return FromMetadata(message, parsed.Response);
            }
            if (message.ProtocolMessage)
            {
                var parsed = ConversationResponseJson.Read(message.Content);
                return parsed.Success ? FromMetadata(message, parsed.Response) : ConversationHistoryReadResult.Fail(parsed.Error);
            }
            if (!string.IsNullOrWhiteSpace(message.ToolCallId) || !string.IsNullOrWhiteSpace(message.ToolName) ||
                message.AcceptedCallOrigin != null)
                return ConversationHistoryReadResult.Fail("Plain assistant history has unexpected tool-call metadata.");
            return ConversationHistoryReadResult.Ok(new AgentResponse(message.Content ?? string.Empty, new ToolCall[0]));
        }

        private static ConversationHistoryReadResult FromMetadata(ChatMessage message, ConversationResponse response)
        {
            if (response.ToolCalls.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(message.ToolCallId) || !string.IsNullOrWhiteSpace(message.ToolName) || message.AcceptedCallOrigin != null)
                    return ConversationHistoryReadResult.Fail("Final history has unexpected tool-call metadata.");
                return ConversationHistoryReadResult.Ok(new AgentResponse(response.Message, new ToolCall[0]));
            }
            if (response.ToolCalls.Count != 1 || string.IsNullOrWhiteSpace(message.ToolCallId) ||
                string.IsNullOrWhiteSpace(message.ToolName) || message.AcceptedCallOrigin == null)
                return ConversationHistoryReadResult.Fail("Accepted history needs one runtime call ID, name and immutable model-attempt origin.");
            var call = response.ToolCalls[0];
            if (!string.Equals(call.Name, message.ToolName, StringComparison.Ordinal))
                return ConversationHistoryReadResult.Fail("History tool-call metadata disagrees with the accepted envelope.");
            return ConversationHistoryReadResult.Ok(new AgentResponse(response.Message, new[]
            {
                new ToolCall(message.ToolCallId, call.Name, call.Arguments.ToString(Formatting.None))
            }));
        }
    }
}
