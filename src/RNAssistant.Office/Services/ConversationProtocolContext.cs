using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    // Transitional run bookkeeping for the v3 boundary. AgentKernel takes ownership
    // in Phase 3; nothing here is a durable index, prompt window or tool authority.
    internal sealed class ConversationProtocolContext
    {
        // Legacy ToolDefinition has no external-effect classification. Until typed
        // ToolPolicy (Phase 4), batch only these audited local reads AND safe metadata.
        // Other tools (including pure pipelines) conservatively remain singleton.
        private static readonly HashSet<string> LocalReadIds = new HashSet<string>(new[]
        {
            "common.resources_list", "common.resources_resolve", "common.resources_search", "common.resources_read",
            "common.capabilities_search", "common.capabilities_read",
            "excel.inspect", "excel.read_range", "excel.find_cells"
        }, StringComparer.Ordinal);

        private readonly HashSet<string> _acceptedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly string[] _batchSafeIds;
        private string _error;

        private ConversationProtocolContext(IEnumerable<ToolDefinition> catalog)
        {
            var tools = (catalog ?? new ToolDefinition[0]).Where(tool => tool != null).ToArray();
            var safety = ToolSafetyPolicy.ResolveAll(tools);
            _batchSafeIds = tools.Where(tool =>
            {
                ToolSafetyProfile profile;
                return tool.Enabled && tool.BuiltIn && tool.AgentCanRun &&
                    string.Equals(tool.Executor, "builtin", StringComparison.OrdinalIgnoreCase) &&
                    LocalReadIds.Contains(tool.Id ?? string.Empty) && safety.TryGetValue(tool.Id, out profile) &&
                    profile.Valid && profile.AgentCanRun && !profile.MutatesDocument &&
                    !profile.MutatesLocalState && !profile.RequiresConfirmation;
            }).Select(tool => tool.Id).Distinct(StringComparer.Ordinal).ToArray();
        }

        internal static ConversationProtocolContext Begin(ChatSession session, IEnumerable<ToolDefinition> catalog, ToolCommand continuedCommand)
        {
            var scope = new ConversationProtocolContext(catalog);
            // A fresh user turn never inherits ids from an earlier turn.
            if (continuedCommand == null) return scope;
            scope.SeedContinuation(session, continuedCommand);
            return scope;
        }

        internal ModelProtocolCallContext Snapshot()
        {
            return new ModelProtocolCallContext(_acceptedIds, _batchSafeIds, _error);
        }

        internal static void EnsureCurrentHistory(ChatSession session)
        {
            if (session == null || session.Messages == null || session.Messages.Any(message => message == null))
                throw HistoryFailure("Нет полной истории чата.");
            if (session.LastRun != null && session.LastRun.ResponseProtocolVersion != 0 &&
                session.LastRun.ResponseProtocolVersion != AgentResponseProtocol.CurrentVersion)
                throw HistoryFailure("Версия протокола последнего запуска несовместима.");
            // Check the full projection, never the compacted prompt window. Suppressed
            // accepted responses still belong to this chat; activities/results do not.
            foreach (var message in session.Messages)
            {
                if (message.Activity != null || !string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)) continue;
                if (message.ResponseProtocolVersion != AgentResponseProtocol.CurrentVersion)
                    throw HistoryFailure("История содержит ответ другой или неизвестной версии протокола.");
                var parsed = ConversationResponseHistoryReader.Read(message);
                if (!parsed.Success) throw HistoryFailure("Неполная запись принятого ответа: " + parsed.Error);
            }
        }

        internal static void EnsureCanContinue(ChatSession session, ToolCommand command)
        {
            EnsureCurrentHistory(session);
            if (command == null || session.LastRun == null ||
                session.LastRun.ResponseProtocolVersion != AgentResponseProtocol.CurrentVersion)
                throw HistoryFailure("Ожидающее действие не связано с текущим протоколом запуска.");
            // The controller calls this BEFORE consuming pending state or executing the
            // confirmed tool. Safety authority is rebuilt later from the current catalog.
            Begin(session, new ToolDefinition[0], command).EnsureComplete();
        }

        internal void EnsureComplete()
        {
            if (!string.IsNullOrEmpty(_error)) throw HistoryFailure(_error);
        }

        private static InvalidOperationException HistoryFailure(string reason)
        {
            return new InvalidOperationException(reason +
                " Откройте новый чат или явно сбросьте историю. Для ожидающего действия доступна отмена. " +
                "Автоматическое преобразование или удаление истории не выполняется.");
        }

        internal void ObserveAccepted(IEnumerable<AgentToolCall> calls)
        {
            // Observe the entire accepted response BEFORE any tool can pause/fail.
            // Rejected raw attempts never reach this method; snapshot lists are copies.
            foreach (var call in calls ?? new AgentToolCall[0])
            {
                if (call == null || string.IsNullOrWhiteSpace(call.Id))
                {
                    _error = "Accepted response contains an unidentified tool call.";
                    continue;
                }
                if (!_acceptedIds.Add(call.Id))
                    _error = "Accepted user-turn history contains a repeated tool-call id: " + call.Id + ".";
            }
        }

        private void SeedContinuation(ChatSession session, ToolCommand command)
        {
            var messages = session == null ? null : session.Messages;
            if (messages == null || string.IsNullOrWhiteSpace(command.ToolCallId))
            {
                _error = "Confirmation continuation has no complete accepted history or tool-call id.";
                return;
            }
            var userIndex = messages.FindLastIndex(message => message != null && message.Activity == null &&
                !message.ProtocolMessage && string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase));
            if (userIndex < 0)
            {
                _error = "Confirmation continuation has no user-turn boundary in full history.";
                return;
            }
            var turnId = session.LastRun == null ? null : session.LastRun.TurnId;
            var userRunId = messages[userIndex].RunId;
            if (!string.IsNullOrWhiteSpace(turnId) && !string.IsNullOrWhiteSpace(userRunId) &&
                !string.Equals(turnId, userRunId, StringComparison.OrdinalIgnoreCase))
            {
                _error = "Confirmation continuation does not match the latest user turn.";
                return;
            }
            // Read the full durable projection, including compacted-away records and
            // suppressed pending call messages. Diagnostic activities/results are not responses.
            for (var index = userIndex + 1; index < messages.Count; index++)
            {
                var message = messages[index];
                if (message == null || message.Activity != null ||
                    !string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)) continue;
                var parsed = ConversationResponseHistoryReader.Read(message);
                if (!parsed.Success)
                {
                    _error = "Cannot reconstruct accepted call ids: " + parsed.Error;
                    return;
                }
                ObserveAccepted(parsed.Response.ToolCalls);
            }
            if (!_acceptedIds.Contains(command.ToolCallId))
                _error = "Confirmed call is missing from accepted user-turn history.";
        }

    }
}
