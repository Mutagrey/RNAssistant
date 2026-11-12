using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Agent;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Runtime;

namespace RNAssistant.Office.Services
{
    // Full-history preflight and source-owned runtime policy projection. Accepted ids
    // are owned by AgentKernel; this adapter only reconstructs a validated continuation.
    internal static class ConversationProtocolContext
    {
        internal static string[] SequentialBatchIds(IEnumerable<ToolCatalogEntry> catalog)
        {
            return (catalog ?? new ToolCatalogEntry[0])
                .Where(tool => tool != null &&
                    !string.IsNullOrWhiteSpace(tool.Id) &&
                    tool.Policy != null &&
                    tool.Policy.AllowsSequentialBatch)
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
                if (message.RuntimeProjection)
                {
                    if (message.ProtocolMessage || message.Role != "assistant" ||
                        !message.ExcludeFromModelContext || message.ResponseProtocolVersion != AgentResponseProtocol.CurrentVersion)
                        throw HistoryFailure("Runtime projection has invalid model-history boundaries.");
                    continue;
                }
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
                        !string.IsNullOrWhiteSpace(message.ToolResultRole);
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
                if (message.AcceptedCallPayload != null)
                {
                    if (!AcceptedCallPayloadService.IsExternalizedCall(message) ||
                        message.AcceptedCallOrigin?.CallCount == null ||
                        calls.ContainsKey(message.ToolCallId))
                        throw HistoryFailure("Неполная reference-first запись принятого вызова.");
                    calls.Add(message.ToolCallId, message);
                    var archivedOrigin = message.AcceptedCallOrigin;
                    if (!origins.Add(Tuple.Create(archivedOrigin.StepId, archivedOrigin.ModelAttemptId, archivedOrigin.CallIndex)))
                        throw HistoryFailure("Повторная запись принятого вызова.");
                    continue;
                }
                var parsed = ConversationResponseHistoryReader.Read(message);
                if (!parsed.Success) throw HistoryFailure("Неполная запись принятого ответа: " + parsed.Error);
                foreach (var call in parsed.Response.ToolCalls)
                {
                    string contractError;
                    if (!ModelToolResultProjection.ValidateAcceptedCall(call, out contractError))
                        throw HistoryFailure("История содержит вызов из заменённого model-facing контракта: " +
                            contractError + " Создайте новый чат или выполните явный reset.");
                }
                if (parsed.Response.ToolCalls.Count > 0)
                {
                    if (!message.ProtocolMessage || message.ToolResultProtocolVersion != ToolResultWire.CurrentVersion ||
                        message.ToolResultRole != ToolResultRoles.User || calls.ContainsKey(message.ToolCallId))
                        throw HistoryFailure("Принятый вызов относится к другой версии результатов или имеет повторный id.");
                    calls.Add(message.ToolCallId, message);
                }
                else if (message.ToolResultProtocolVersion != 0 || !string.IsNullOrWhiteSpace(message.ToolResultRole))
                    throw HistoryFailure("Ответ без вызова содержит метаданные результата инструмента.");
                var origin = message.AcceptedCallOrigin;
                if (origin != null && !origins.Add(Tuple.Create(origin.StepId, origin.ModelAttemptId, origin.CallIndex)))
                    throw HistoryFailure("Один вызов model attempt связан с несколькими accepted records.");
            }
        }

        internal static void EnsureCanContinue(ChatSession session, ToolInvocation command, RNAssistant.Core.Storage.ChatBlobStore payloads = null)
        {
            RestoreContinuation(session, command, payloads);
        }

        internal static AgentRunContinuation RestoreContinuation(ChatSession session, ToolInvocation command, RNAssistant.Core.Storage.ChatBlobStore payloads = null)
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
            return RestorePendingHistory(session, state, payloads, false);
        }

        internal static AgentRunContinuation RestoreWaitContinuation(ChatSession session, RNAssistant.Core.Storage.ChatBlobStore payloads = null)
        {
            EnsureCurrentHistory(session);
            var state = session.LastRun?.KernelState;
            if (state?.Summary.Lifecycle != RunLifecycle.Waiting || state.Summary.RunId != session.LastRun.RunId ||
                state.Summary.TurnId != session.LastRun.TurnId)
                throw HistoryFailure("The saved wait does not belong to the current run.");
            return RestorePendingHistory(session, state, payloads, true);
        }

        private static AgentRunContinuation RestorePendingHistory(ChatSession session, RNAssistant.Core.Persistence.AgentRunState state,
            RNAssistant.Core.Storage.ChatBlobStore payloads, bool waiting)
        {
            var userIndex = session.Messages.FindIndex(message =>
                (ConversationInputMessage.IsUserInput(message) || message.AgentCommunication?.Sender != null) && message.RunId == state.Summary.TurnId);
            if (userIndex < 0) throw HistoryFailure("Continuation has no original input turn.");
            var history = new List<AgentMessage> { AgentMessage.User(ConversationInputMessage.ModelText(session.Messages[userIndex])) };
            ChatMessage previousAccepted = null;
            for (var index = userIndex + 1; index < session.Messages.Count; index++)
            {
                var message = session.Messages[index];
                if (message.Activity != null || message.RuntimeProjection) continue;
                if (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    message = RNAssistant.Core.Storage.AcceptedCallPayloadService.Hydrate(message, payloads);
                    var parsed = ConversationResponseHistoryReader.Read(message);
                    if (!parsed.Success) throw HistoryFailure("Cannot reconstruct accepted calls: " + parsed.Error);
                    ConversationResponseHistoryReader.AppendAcceptedResponse(history, message, parsed.Response, ref previousAccepted);
                }
                else
                {
                    ConversationResponseHistoryReader.RequireCompleteBatch(previousAccepted);
                    previousAccepted = null;
                    if (ConversationInputMessage.IsUserInput(message) || message.AgentCommunication?.Sender != null)
                        history.Add(AgentMessage.User(ConversationInputMessage.ModelText(message)));
                    else if (message.ProtocolMessage && !string.IsNullOrWhiteSpace(message.ToolCallId))
                    {
                        ToolResultWireReadResult wire; string error;
                        if (!ToolResultHistoryReader.TryRead(message, out wire, out error)) throw HistoryFailure(error);
                        if (message.ExecutionProgress == null)
                            throw HistoryFailure("Continuation lacks typed terminal progress; the previous effects cannot be safely replayed.");
                        history.Add(AgentMessage.AcceptedToolResult(message.ToolCallId, wire.Result.Message, message.Content,
                            message.ExecutionProgress, message.ResourceEvidence, message.ResourceEffect));
                    }
                }
            }
            ConversationResponseHistoryReader.RequireCompleteBatch(previousAccepted);
            try { return waiting ? AgentRunContinuation.RestoreWait(state, session.Revision, history)
                : AgentRunContinuation.Restore(state, session.Revision, history); }
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
