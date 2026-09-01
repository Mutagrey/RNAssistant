using RNAssistant.Core.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Agent;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Office.Runtime;

namespace RNAssistant.Office.Services
{
    // Full-history preflight and source-owned runtime policy projection. Accepted ids
    // are owned by AgentKernel; this adapter only reconstructs a validated continuation.
    internal static class ConversationProtocolContext
    {
        internal static string[] BatchSafeReadIds(IEnumerable<ToolCatalogEntry> catalog)
        {
            return (catalog ?? new ToolCatalogEntry[0])
                .Where(tool => tool != null &&
                    !string.IsNullOrWhiteSpace(tool.Id) &&
                    tool.Policy != null &&
                    tool.Policy.IndependentLocalRead)
                .Select(tool => tool.Id).Distinct(StringComparer.Ordinal).ToArray();
        }

        internal static void EnsureCurrentHistory(ChatSession session)
        {
            if (session == null || session.Messages == null || session.Messages.Any(message => message == null))
                throw HistoryFailure("Нет полной истории чата.");
            if (session.LastRun != null && session.LastRun.ResponseProtocolVersion != 0 &&
                session.LastRun.ResponseProtocolVersion != AgentResponseProtocol.CurrentVersion)
                throw HistoryFailure("Версия протокола последнего запуска несовместима.");
            // Check the full projection, never the compacted prompt window. Suppressed
            // accepted responses/results still belong to this chat. Missing results
            // may be typed pauses or in-flight calls; the kernel owns that lifecycle.
            var origins = new HashSet<Tuple<string, string, int>>();
            var calls = new Dictionary<string, ChatMessage>(StringComparer.Ordinal);
            var results = new HashSet<string>(StringComparer.Ordinal);
            foreach (var message in session.Messages)
            {
                if (message.Activity != null) continue;
                if (!string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    if (!message.ProtocolMessage && string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
                    {
                        // Confirmation resumes this scope; a new user turn starts a new id namespace.
                        calls.Clear();
                        results.Clear();
                    }
                    var isResult = message.Role == ToolResultRoles.Tool || message.ToolResultProtocolVersion != 0 ||
                        !string.IsNullOrWhiteSpace(message.ToolCallId) || !string.IsNullOrWhiteSpace(message.ToolName) ||
                        !string.IsNullOrWhiteSpace(message.ToolResultRole) ||
                        message.ProtocolMessage && (message.Content ?? string.Empty).StartsWith("TOOL_RESULT:", StringComparison.Ordinal);
                    if (!isResult) continue;
                    ToolResultWireReadResult result;
                    string error;
                    if (!ToolResultHistoryReader.TryRead(message, out result, out error))
                        throw HistoryFailure("Неполная запись результата инструмента: " + error);
                    ChatMessage accepted;
                    if (!calls.TryGetValue(result.ToolCallId, out accepted) || accepted.ToolName != result.Name ||
                        accepted.ToolResultRole != message.Role || !results.Add(result.ToolCallId))
                        throw HistoryFailure("Результат не связан с единственным принятым вызовом и его ролью.");
                    continue;
                }
                if (message.ResponseProtocolVersion != AgentResponseProtocol.CurrentVersion)
                    throw HistoryFailure("История содержит ответ другой или неизвестной версии протокола.");
                var parsed = ConversationResponseHistoryReader.Read(message);
                if (!parsed.Success) throw HistoryFailure("Неполная запись принятого ответа: " + parsed.Error);
                if (parsed.Response.ToolCalls.Count > 0)
                {
                    if (!message.ProtocolMessage || message.ToolResultProtocolVersion != ToolResultWire.CurrentVersion ||
                        message.ToolResultRole != ToolResultRoles.User && message.ToolResultRole != ToolResultRoles.Developer &&
                        message.ToolResultRole != ToolResultRoles.Tool || calls.ContainsKey(message.ToolCallId))
                        throw HistoryFailure("Принятый вызов относится к другой версии результатов или имеет повторный id.");
                    var native = message.ToolResultRole == ToolResultRoles.Tool;
                    if (native != (message.ToolCalls != null && message.ToolCalls.Count == 1) ||
                        native && (message.ToolCalls[0].Name != AgentJsonProtocol.ApiToolName(message.ToolName) ||
                            message.ToolCalls[0].Type != "function"))
                        throw HistoryFailure("Форма принятого вызова не соответствует сохранённой роли результатов.");
                    calls.Add(message.ToolCallId, message);
                }
                else if (message.ToolResultProtocolVersion != 0 || !string.IsNullOrWhiteSpace(message.ToolResultRole))
                    throw HistoryFailure("Ответ без вызова содержит метаданные результата инструмента.");
                var origin = message.AcceptedCallOrigin;
                if (origin != null && !origins.Add(Tuple.Create(origin.StepId, origin.ModelAttemptId, origin.CallIndex)))
                    throw HistoryFailure("Один вызов model attempt связан с несколькими accepted records.");
            }
        }

        internal static void EnsureCanContinue(ChatSession session, ToolInvocation command)
        {
            RestoreContinuation(session, command);
        }

        internal static AgentRunContinuation RestoreContinuation(ChatSession session, ToolInvocation command)
        {
            EnsureCurrentHistory(session);
            var state = session.LastRun == null ? null : session.LastRun.KernelState;
            var pending = state == null ? null : state.Summary.PendingConfirmation;
            if (command == null || pending == null || session.LastRun.TurnId != state.Summary.TurnId || session.LastRun.ResponseProtocolVersion != AgentResponseProtocol.CurrentVersion ||
                !string.Equals(command.ToolCallId, pending.Call.Id, StringComparison.Ordinal) || command.ToolId != pending.Call.Name ||
                JsonConvert.SerializeObject(command.Arguments, Formatting.None) != pending.Call.ArgumentsJson)
                throw HistoryFailure("Ожидающее действие не связано с полной kernel evidence текущего запуска.");
            var activity = session.Messages.LastOrDefault(message => message.Activity != null &&
                message.Activity.ToolCallId == pending.Call.Id);
            if (activity == null || !string.Equals(activity.Activity.ConfirmationCatalogSha256, pending.Policy.Revision, StringComparison.Ordinal))
                throw HistoryFailure("Ожидающее действие не связано с сохранённым fingerprint каталога.");
            var userIndex = session.Messages.FindLastIndex(message => message.Activity == null && !message.ProtocolMessage &&
                string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase));
            if (userIndex < 0 || !string.Equals(session.Messages[userIndex].RunId, state.Summary.TurnId, StringComparison.Ordinal))
                throw HistoryFailure("Confirmation continuation does not match the latest user turn.");
            var history = new List<AgentMessage> { AgentMessage.User(session.Messages[userIndex].Content) };
            for (var index = userIndex + 1; index < session.Messages.Count; index++)
            {
                var message = session.Messages[index];
                if (message.Activity != null) continue;
                if (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    var parsed = ConversationResponseHistoryReader.Read(message);
                    if (!parsed.Success) throw HistoryFailure("Cannot reconstruct accepted calls: " + parsed.Error);
                    history.Add(AgentMessage.Assistant(parsed.Response));
                }
                else if (message.ProtocolMessage && !string.IsNullOrWhiteSpace(message.ToolCallId))
                    history.Add(AgentMessage.AcceptedToolResult(message.ToolCallId, string.Empty, message.Content));
            }
            try { return AgentRunContinuation.Restore(state.Summary, state.Limits, session.Revision, history); }
            catch (InvalidOperationException ex) { throw HistoryFailure(ex.Message); }
        }

        private static InvalidOperationException HistoryFailure(string reason)
        {
            return new InvalidOperationException(reason +
                " Откройте новый чат или явно сбросьте историю. Для ожидающего действия доступна отмена. " +
                "Автоматическое преобразование или удаление истории не выполняется.");
        }
    }
}
