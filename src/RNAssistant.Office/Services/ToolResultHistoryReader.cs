using System;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal static class ToolResultHistoryReader
    {
        private const string Prefix = "TOOL_RESULT:";

        internal static bool TryRead(ChatMessage message, out ToolResultWireReadResult result, out string error)
        {
            result = null;
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
            var native = message.Role == ToolResultRoles.Tool;
            if ((!native && message.Role != ToolResultRoles.User && message.Role != ToolResultRoles.Developer) ||
                message.ToolResultRole != message.Role)
            {
                error = "Result history role disagrees with its recorded tool-result role.";
                return false;
            }
            var json = message.Content ?? string.Empty;
            if (!native)
            {
                if (!json.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    error = "User/developer tool-result history requires its exact prefix.";
                    return false;
                }
                json = json.Substring(Prefix.Length);
            }
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
    }
}
