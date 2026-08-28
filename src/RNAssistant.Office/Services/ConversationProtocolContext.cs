using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Agent;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    // Full-history preflight and legacy local-read classification. Accepted ids
    // are owned by AgentKernel; this adapter only reconstructs a validated continuation.
    internal static class ConversationProtocolContext
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

        internal static string[] BatchSafeReadIds(IEnumerable<ToolDefinition> catalog)
        {
            var tools = (catalog ?? new ToolDefinition[0]).Where(tool => tool != null).ToArray();
            var safety = ToolSafetyPolicy.ResolveAll(tools);
            return tools.Where(tool =>
            {
                ToolSafetyProfile profile;
                return tool.Enabled && tool.BuiltIn && tool.AgentCanRun &&
                    string.Equals(tool.Executor, "builtin", StringComparison.OrdinalIgnoreCase) &&
                    LocalReadIds.Contains(tool.Id ?? string.Empty) && safety.TryGetValue(tool.Id, out profile) &&
                    profile.Valid && profile.AgentCanRun && !profile.MutatesDocument &&
                    !profile.MutatesLocalState && !profile.RequiresConfirmation;
            }).Select(tool => tool.Id).Distinct(StringComparer.Ordinal).ToArray();
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
            RestoreContinuation(session, command);
        }

        internal static AgentRunContinuation RestoreContinuation(ChatSession session, ToolCommand command)
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
                    history.Add(AgentMessage.Assistant(new AgentResponse(parsed.Response.Message,
                        parsed.Response.ToolCalls.Select(ConversationKernelAdapter.ToCoreCall))));
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
