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
        { return new ConversationHistoryReadResult { Response = response }; }
        internal static ConversationHistoryReadResult Fail(string error)
        { return new ConversationHistoryReadResult { Error = error }; }
    }

    // Accepted history is a runtime fact with exact call IDs, never raw model
    // authority. Old/unknown versions require explicit reset; no v7 inference.
    public static class ConversationResponseHistoryReader
    {
        public static ConversationHistoryReadResult Read(ChatMessage message)
        {
            if (message == null || message.Role != "assistant" || message.Activity != null ||
                !message.ProtocolMessage || message.ResponseProtocolVersion != ConversationResponse.ProtocolVersion)
                return ConversationHistoryReadResult.Fail("History record is not an identified v8 assistant response.");
            ConversationResponseParseResult parsed;
            var nativeCalls = message.ToolCalls;
            if (nativeCalls != null && nativeCalls.Count > 0)
                return ConversationHistoryReadResult.Fail("Native history is not part of the JSON profile. Start a new chat.");
            parsed = ConversationResponseJson.Read(message.Content);
            if (!parsed.Success) return ConversationHistoryReadResult.Fail(parsed.Error);
            var response = parsed.Response;
            if (response.Action == ConversationResponse.ExecuteAction)
            {
                if (response.ToolCalls.Count != 1 || string.IsNullOrWhiteSpace(message.ToolCallId) ||
                    string.IsNullOrWhiteSpace(message.ToolName) || message.AcceptedCallOrigin == null ||
                    string.IsNullOrWhiteSpace(message.AcceptedCallOrigin.ModelAttemptId) ||
                    message.ToolResultProtocolVersion != ToolResultWire.CurrentVersion ||
                    message.ToolResultRole != ToolResultRoles.User ||
                    response.ToolCalls[0].Name != message.ToolName)
                    return ConversationHistoryReadResult.Fail("Accepted v8 call metadata disagrees with its envelope.");
                return ConversationHistoryReadResult.Ok(new AgentResponse(
                    response.Action, response.Reason, response.Message,
                    calls: new[] { new ToolCall(message.ToolCallId, message.ToolName,
                        response.ToolCalls[0].Arguments.ToString(Formatting.None)) },
                    sourceModelAttemptId: message.AcceptedCallOrigin.ModelAttemptId));
            }
            if (nativeCalls != null && nativeCalls.Count > 0 ||
                !string.IsNullOrWhiteSpace(message.ToolCallId) || !string.IsNullOrWhiteSpace(message.ToolName) ||
                message.AcceptedCallOrigin != null || message.ToolResultProtocolVersion != 0 ||
                !string.IsNullOrWhiteSpace(message.ToolResultRole) || !string.IsNullOrWhiteSpace(message.ResponseReason) ||
                string.IsNullOrWhiteSpace(message.ResponseModelAttemptId))
                return ConversationHistoryReadResult.Fail("No-call v8 response has tool metadata or lacks its model attempt origin.");
            return ConversationHistoryReadResult.Ok(new AgentResponse(
                response.Action, response.Reason, response.Message, decision: response.Decision,
                results: response.Results, sourceModelAttemptId: message.ResponseModelAttemptId, outcome: response.Outcome));
        }
    }
}
