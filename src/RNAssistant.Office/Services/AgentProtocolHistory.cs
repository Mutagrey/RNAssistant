using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Services
{
    internal static class AgentProtocolHistory
    {
        private const int MaxDataChars = 6000;
        private const int MaxSummaryChars = 1200;

        public static void AppendToolExchange(
            ICollection<ChatMessage> protocolMessages,
            AgentPlannerAttempt attempt,
            ToolCommand command,
            ToolResult result,
            AppSettings settings)
        {
            AppendToolExchanges(
                protocolMessages,
                attempt,
                new[] { new AgentToolExchange(command, result) },
                settings);
        }

        public static void AppendToolExchanges(
            ICollection<ChatMessage> protocolMessages,
            AgentPlannerAttempt attempt,
            IEnumerable<AgentToolExchange> exchanges,
            AppSettings settings)
        {
            if (protocolMessages == null) return;
            var batch = (exchanges ?? new AgentToolExchange[0])
                .Where(exchange => exchange != null && exchange.Command != null)
                .ToList();
            if (batch.Count == 0) return;

            var calls = new List<LlmToolCall>();
            var results = new List<ChatMessage>();
            var nativeCalls = attempt == null || attempt.Completion == null
                ? new List<LlmToolCall>()
                : attempt.Completion.ToolCalls ?? new List<LlmToolCall>();
            var usedCallIds = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < batch.Count; index++)
            {
                var exchange = batch[index];
                var command = exchange.Command;
                var result = exchange.Result;
                var nativeCall = index < nativeCalls.Count
                    ? nativeCalls[index]
                    : nativeCalls.FirstOrDefault(call => call != null &&
                        string.Equals(call.Id, command.ToolCallId, StringComparison.Ordinal));
                var callId = command.ToolCallId;
                while (string.IsNullOrWhiteSpace(callId) || !usedCallIds.Add(callId))
                {
                    callId = "call_" + Guid.NewGuid().ToString("N");
                }
                command.ToolCallId = callId;
                var apiTool = attempt == null || attempt.RequestOptions == null
                    ? null
                    : (attempt.RequestOptions.Tools ?? new LlmToolDefinition[0]).FirstOrDefault(tool =>
                        tool != null && string.Equals(tool.ToolId, command.ToolId, StringComparison.OrdinalIgnoreCase));
                var apiName = !string.IsNullOrWhiteSpace(command.ToolApiName)
                    ? command.ToolApiName
                    : apiTool == null ? command.ToolId.Replace('.', '_') : apiTool.ApiName;
                command.ToolApiName = apiName;
                var argumentsJson = JsonConvert.SerializeObject(command.Arguments ?? new Dictionary<string, object>());
                calls.Add(nativeCall == null
                    ? new LlmToolCall { Id = callId, Name = apiName, ArgumentsJson = argumentsJson }
                    : new LlmToolCall
                    {
                        Id = callId,
                        Type = string.IsNullOrWhiteSpace(nativeCall.Type) ? "function" : nativeCall.Type,
                        Name = string.IsNullOrWhiteSpace(nativeCall.Name) ? apiName : nativeCall.Name,
                        ArgumentsJson = string.IsNullOrWhiteSpace(nativeCall.ArgumentsJson) ? argumentsJson : nativeCall.ArgumentsJson
                    });
                results.Add(new ChatMessage
                {
                    Role = "tool",
                    ToolCallId = callId,
                    ToolName = apiName,
                    Content = BuildResultJson(command, result, callId)
                });
            }

            var role = NormalizeToolResultRole(settings == null ? null : settings.ToolResultRole);
            if (string.Equals(role, "tool", StringComparison.Ordinal))
            {
                protocolMessages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = nativeCalls.Count == 0 || attempt == null ? string.Empty : attempt.Text ?? string.Empty,
                    ToolCalls = calls
                });
                foreach (var result in results) protocolMessages.Add(result);
                return;
            }

            if (attempt != null && !string.IsNullOrWhiteSpace(attempt.Text))
            {
                protocolMessages.Add(new ChatMessage { Role = "assistant", Content = attempt.Text });
            }
            foreach (var result in results)
            {
                protocolMessages.Add(new ChatMessage { Role = role, Content = "TOOL_RESULT:\n" + result.Content });
            }
        }

        public static void AppendToolExchange(
            ICollection<ChatMessage> protocolMessages,
            ChatSession session,
            AgentPlannerAttempt attempt,
            ToolCommand command,
            ToolResult result,
            AppSettings settings)
        {
            var list = protocolMessages as IList<ChatMessage>;
            var before = list == null ? -1 : list.Count;
            AppendToolExchange(protocolMessages, attempt, command, result, settings);
            if (session == null || list == null || before < 0)
            {
                return;
            }
            session.Messages = session.Messages ?? new List<ChatMessage>();
            for (var index = before; index < list.Count; index++)
            {
                var message = list[index];
                if (message == null) continue;
                message.ProtocolMessage = true;
                message.HtmlWorkspaceCheckpointId = session.ActiveHtmlArtifactId;
                session.Messages.Add(message);
            }
        }

        public static void AppendToolExchanges(
            ICollection<ChatMessage> protocolMessages,
            ChatSession session,
            AgentPlannerAttempt attempt,
            IEnumerable<AgentToolExchange> exchanges,
            AppSettings settings)
        {
            var list = protocolMessages as IList<ChatMessage>;
            var before = list == null ? -1 : list.Count;
            AppendToolExchanges(protocolMessages, attempt, exchanges, settings);
            PersistAddedMessages(list, session, before);
        }

        private static void PersistAddedMessages(IList<ChatMessage> list, ChatSession session, int before)
        {
            if (session == null || list == null || before < 0) return;
            session.Messages = session.Messages ?? new List<ChatMessage>();
            for (var index = before; index < list.Count; index++)
            {
                var message = list[index];
                if (message == null) continue;
                message.ProtocolMessage = true;
                message.HtmlWorkspaceCheckpointId = session.ActiveHtmlArtifactId;
                session.Messages.Add(message);
            }
        }

        private static string BuildResultJson(ToolCommand command, ToolResult result, string callId)
        {
            return JsonConvert.SerializeObject(new
            {
                protocolVersion = AgentDecisionProtocol.Version,
                callId = callId,
                toolId = command == null ? string.Empty : command.ToolId,
                ok = result != null && result.Success,
                status = result == null ? "failed" : result.Status,
                summary = BoundText(result == null ? "Tool returned no result." : result.Message, MaxSummaryChars),
                data = ParseProtocolData(result == null ? null : result.DataJson),
                error = result != null && result.Success ? null : new
                {
                    code = result == null ? "missing_result" : result.ErrorCode,
                    message = BoundText(result == null ? "Tool returned no result." : result.Message, MaxSummaryChars),
                    retryable = result == null ? (bool?)false : result.Retryable
                }
            });
        }

        private static object ParseProtocolData(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (value.Length > MaxDataChars)
            {
                return new
                {
                    truncated = true,
                    originalChars = value.Length,
                    preview = value.Substring(0, MaxDataChars)
                };
            }
            try { return JToken.Parse(value); }
            catch (JsonException) { return value; }
        }

        private static string BoundText(string value, int maxChars)
        {
            value = value ?? string.Empty;
            return value.Length <= maxChars ? value : value.Substring(0, maxChars) + "…";
        }

        private static string NormalizeToolResultRole(string role)
        {
            if (string.Equals(role, "developer", StringComparison.OrdinalIgnoreCase)) return "developer";
            if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase)) return "user";
            return "tool";
        }

    }

    internal sealed class AgentToolExchange
    {
        public ToolCommand Command { get; private set; }
        public ToolResult Result { get; private set; }

        public AgentToolExchange(ToolCommand command, ToolResult result)
        {
            Command = command;
            Result = result;
        }
    }
}
