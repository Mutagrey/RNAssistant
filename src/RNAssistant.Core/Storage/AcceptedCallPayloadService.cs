using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    // Runtime-accepted protocol body, separate from the lightweight durable fact.
    // Only active continuation/request boundaries hydrate it; no provider fallback.
    public static class AcceptedCallPayloadService
    {
        public const string ContentType = "application/vnd.rnassistant.accepted-call+json";

        public static bool IsExternalizedCall(ChatMessage message)
        {
            return message != null && message.ProtocolMessage &&
                message.ArgumentPayload != null && message.AcceptedCallPayload != null &&
                message.AcceptedCallPayload.ContentType == ContentType &&
                message.AcceptedCallOrigin != null &&
                !string.IsNullOrWhiteSpace(message.ToolCallId) &&
                !string.IsNullOrWhiteSpace(message.ToolName) &&
                message.ToolResultProtocolVersion == ToolResultWire.CurrentVersion &&
                (message.ToolResultRole == ToolResultRoles.User ||
                 message.ToolResultRole == ToolResultRoles.Developer ||
                 message.ToolResultRole == ToolResultRoles.Tool) &&
                message.ToolCalls != null && message.ToolCalls.Count == 0;
        }

        public static void Externalize(ChatMessage message, ChatBlobStore payloads)
        {
            if (message == null || payloads == null || message.ArgumentPayload == null ||
                string.IsNullOrWhiteSpace(message.ToolCallId) || message.AcceptedCallOrigin == null)
                throw new ArgumentException("An exact accepted call and argument payload are required.");
            var body = new AcceptedCallBody { Content = message.Content, ToolCalls = message.ToolCalls };
            message.AcceptedCallPayload = PayloadRef.FromBlob(payloads.StoreText(JsonConvert.SerializeObject(body),
                ContentType));
            message.Content = "ACCEPTED_TOOL_CALL: " + message.ToolName + " (exact arguments retained in resource payload)";
            message.ToolCalls = new List<LlmToolCall>();
        }

        public static ChatMessage Hydrate(ChatMessage fact, ChatBlobStore payloads)
        {
            if (fact?.AcceptedCallPayload == null) return fact;
            if (!IsExternalizedCall(fact))
                throw new InvalidOperationException("ACCEPTED_CALL_PAYLOAD_INVALID: exact accepted-call metadata is incomplete.");
            if (payloads == null || fact.AcceptedCallPayload.ByteLength > 16L * 1024 * 1024)
                throw new InvalidOperationException("ACCEPTED_CALL_PAYLOAD_UNAVAILABLE: bounded exact payload required.");
            var text = payloads.ReadText(fact.AcceptedCallPayload.ToBlobReference());
            if (text == null) throw new InvalidOperationException("ACCEPTED_CALL_PAYLOAD_UNAVAILABLE: exact historical body is missing.");
            var body = JsonConvert.DeserializeObject<AcceptedCallBody>(text);
            if (body == null || body.Content == null || body.ToolCalls == null)
                throw new InvalidOperationException("ACCEPTED_CALL_PAYLOAD_INVALID: explicit reset or cancellation required.");
            var message = JsonConvert.DeserializeObject<ChatMessage>(JsonConvert.SerializeObject(fact));
            message.Content = body.Content; message.ToolCalls = body.ToolCalls;
            return message;
        }

        private sealed class AcceptedCallBody
        {
            public string Content { get; set; }
            public List<LlmToolCall> ToolCalls { get; set; }
        }
    }
}
