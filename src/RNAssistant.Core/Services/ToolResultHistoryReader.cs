using System;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public static class ToolResultHistoryReader
    {

        public static bool TryRead(ChatMessage message, out ToolResultWireReadResult result, out string error)
        {
            result = null;
            if (!TryReadHeader(message, out error)) return false;
            var json = message.Content ?? string.Empty;
            var parsed = ToolResultWire.Read(json);
            if (!parsed.Success)
            {
                error = parsed.Error;
                return false;
            }
            if (parsed.ToolCallId != message.ToolCallId ||
                message.ToolName != parsed.Name)
            {
                error = "Result body disagrees with its runtime call id or canonical tool name.";
                return false;
            }
            result = parsed;
            return true;
        }

        internal static bool TryReadHeader(ChatMessage message, out string error)
        {
            error = null;
            if (message == null || message.Activity != null || !message.ProtocolMessage ||
                message.ToolResultProtocolVersion != ToolResultWire.CurrentVersion ||
                message.ResponseProtocolVersion != 0 || message.AcceptedCallOrigin != null ||
                message.ToolCalls != null && message.ToolCalls.Count > 0 ||
                string.IsNullOrWhiteSpace(message.ToolCallId) || string.IsNullOrWhiteSpace(message.ToolName))
            {
                error = "Result history lacks an identified current tool-result record.";
                return false;
            }
            if (message.Role != ToolResultRoles.User || message.ToolResultRole != message.Role)
            {
                error = "JSON result history requires the user transport role.";
                return false;
            }
            return true;
        }
    }
}
