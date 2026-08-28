using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.ModelProtocol
{
    // Current v3 history only, not historical-format compatibility. Never mutates
    // a durable message or grants tool authority. Incompatible chats require skip/reset.
    public static class ConversationResponseHistoryReader
    {
        public static ConversationResponseParseResult Read(ChatMessage message)
        {
            if (message == null || !string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) ||
                message.Activity != null || message.ResponseProtocolVersion != ConversationResponse.ProtocolVersion)
                return ConversationResponseParseResult.Fail("History record is not an identified v3 assistant response.");

            var nativeCalls = message.ToolCalls;
            if (nativeCalls != null && nativeCalls.Count > 0)
            {
                // Current accepted native history stores one call per message and keeps
                // its canonical id separately. Never reverse-map a provider-safe name.
                if (nativeCalls.Count != 1 || nativeCalls[0] == null || string.IsNullOrWhiteSpace(message.ToolName) ||
                    string.IsNullOrWhiteSpace(nativeCalls[0].ArgumentsJson))
                    return ConversationResponseParseResult.Fail("Native history needs one call with a canonical ToolName and object arguments.");
                var call = nativeCalls[0];
                var envelope = new JObject
                {
                    ["message"] = message.Content ?? string.Empty,
                    ["tool_calls"] = new JArray(new JObject
                    {
                        ["id"] = call.Id,
                        ["name"] = message.ToolName,
                        ["arguments"] = new JRaw(call.ArgumentsJson)
                    })
                }.ToString(Formatting.None);
                var parsed = ConversationResponseJson.Read(envelope);
                if (!parsed.Success) return parsed;
                // Raw arguments must not inject another call or change envelope identity.
                if (parsed.Response.ToolCalls.Count != 1 || parsed.Response.ToolCalls[0].Id != call.Id ||
                    parsed.Response.ToolCalls[0].Name != message.ToolName || parsed.Response.Message != (message.Content ?? string.Empty))
                    return ConversationResponseParseResult.Fail("Native argument JSON changed the response envelope.");
                return CheckMetadata(message, parsed);
            }
            if (message.ProtocolMessage)
            {
                var parsed = ConversationResponseJson.Read(message.Content);
                return parsed.Success ? CheckMetadata(message, parsed) : parsed;
            }
            if (!string.IsNullOrWhiteSpace(message.ToolCallId) || !string.IsNullOrWhiteSpace(message.ToolName))
                return ConversationResponseParseResult.Fail("Plain assistant history has unexpected tool-call metadata.");
            // Plain final text is a typed history form, even if it happens to look like JSON.
            // Model status metadata is deliberately not interpreted as runtime truth.
            return ConversationResponseParseResult.Ok(new ConversationResponse(message.Content ?? string.Empty, new AgentToolCall[0]));
        }

        private static ConversationResponseParseResult CheckMetadata(ChatMessage message, ConversationResponseParseResult parsed)
        {
            if (string.IsNullOrWhiteSpace(message.ToolCallId))
                return string.IsNullOrWhiteSpace(message.ToolName) ? parsed
                    : ConversationResponseParseResult.Fail("History has ToolName without ToolCallId.");
            var call = parsed.Response.ToolCalls.FirstOrDefault(item => string.Equals(item.Id, message.ToolCallId, StringComparison.Ordinal));
            if (call == null || (!string.IsNullOrWhiteSpace(message.ToolName) && call.Name != message.ToolName))
                return ConversationResponseParseResult.Fail("History tool-call metadata disagrees with the accepted envelope.");
            return parsed;
        }
    }
}
