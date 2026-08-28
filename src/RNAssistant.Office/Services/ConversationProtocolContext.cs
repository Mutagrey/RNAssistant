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
                _acceptedIds.Add(call.Id);
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
                    !string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) ||
                    (!message.ProtocolMessage && (message.ToolCalls == null || message.ToolCalls.Count == 0))) continue;
                // Temporary consumer of the CURRENT v2 transcript's typed call metadata,
                // not a v2 JSON reader or an old-chat migration. Remove at the v3 switch.
                if (message.ResponseProtocolVersion == 2 && AgentResponseProtocol.CurrentVersion == 2)
                {
                    if (!ReadCurrentV2CallIds(message)) return;
                    continue;
                }
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

        private bool ReadCurrentV2CallIds(ChatMessage message)
        {
            if (string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                _error = "Current v2 transcript is missing its accepted ToolCallId.";
                return false;
            }
            var native = message.ToolCalls;
            if (native != null && native.Count > 0)
            {
                if (native.Any(call => call == null || string.IsNullOrWhiteSpace(call.Id)) ||
                    !native.Any(call => call.Id == message.ToolCallId))
                {
                    _error = "Current native transcript has inconsistent accepted call ids.";
                    return false;
                }
                foreach (var call in native) _acceptedIds.Add(call.Id);
            }
            _acceptedIds.Add(message.ToolCallId);
            return true;
        }
    }
}
