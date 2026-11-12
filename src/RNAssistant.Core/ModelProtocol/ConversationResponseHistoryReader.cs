using System;
using System.Collections.Generic;
using System.Linq;
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
        // Durable occurrences store one call each. Both hosts reconstruct the
        // original decision before handing its pending suffix to the kernel.
        public static void AppendAcceptedResponse(List<AgentMessage> history, ChatMessage occurrence,
            AgentResponse response, ref ChatMessage previousOccurrence)
        {
            var origin = occurrence.AcceptedCallOrigin;
            var previous = history.LastOrDefault();
            if (origin != null && string.IsNullOrWhiteSpace(occurrence.RunId))
                throw InvalidBatch("Accepted call has no run ID.");
            if (origin != null && previous?.Kind == AgentMessageKind.Assistant && previous.Action == ConversationResponse.ExecuteAction)
            {
                var prior = previousOccurrence?.AcceptedCallOrigin;
                if (prior == null || origin.CallIndex != prior.CallIndex + 1 || origin.CallCount != prior.CallCount ||
                    origin.StepId != prior.StepId || origin.ModelAttemptId != prior.ModelAttemptId ||
                    occurrence.RunId != previousOccurrence.RunId || previous.Reason != response.Reason || response.Message.Length != 0)
                    throw InvalidBatch("Stored batch origin or response metadata disagree.");
                history[history.Count - 1] = AgentMessage.Assistant(new AgentResponse(ConversationResponse.ExecuteAction,
                    previous.Reason, previous.Text, calls: previous.ToolCalls.Concat(response.ToolCalls).ToArray(),
                    sourceModelAttemptId: origin.ModelAttemptId));
            }
            else
            {
                RequireCompleteBatch(previousOccurrence);
                if (origin != null && origin.CallIndex != 0)
                    throw InvalidBatch("Stored batch has no first accepted call.");
                history.Add(AgentMessage.Assistant(response));
            }
            previousOccurrence = origin == null ? null : occurrence;
        }

        public static void RequireCompleteBatch(ChatMessage lastOccurrence)
        {
            var origin = lastOccurrence?.AcceptedCallOrigin;
            if (origin != null && origin.CallIndex + 1 != origin.CallCount)
                throw InvalidBatch("Stored batch is missing accepted calls.");
        }

        private static InvalidOperationException InvalidBatch(string reason)
        { return new InvalidOperationException(reason + " Создайте новый чат; сохранённая история не изменяется."); }

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
                    !message.AcceptedCallOrigin.CallCount.HasValue ||
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
