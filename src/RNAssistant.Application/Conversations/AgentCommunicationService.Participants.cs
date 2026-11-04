using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Application.Conversations
{
    internal sealed partial class AgentCommunicationService
    {
        // Called under the communication owner lock. The root Inbox serializes
        // recursive and concurrent membership changes in the existing journal.
        private void ReserveParticipant(ChatSession source, AgentCommunicationContext chain, int capturedLimit,
            string operationId, string fingerprint, AgentChatAddress target, Action dispatch)
        {
            if (chain.Root == null)
                throw new AgentCommunicationException("agent_participants_untracked", "Начните совместную работу новым сообщением пользователя: участники прежней цепочки не учитывались.");
            var rootEndpoint = FindAgent(source, chain.Root.AgentId);
            if (rootEndpoint == null || rootEndpoint.Address.ChatId != chain.Root.ChatId)
                throw new AgentCommunicationException("agent_root_unavailable", "Исходный чат совместной работы недоступен; число участников нельзя проверить.");
            var root = rootEndpoint.Load();
            rootEndpoint.Validate(root);
            lock (rootEndpoint.Inbox.Get(root).Sync)
            {
                var facts = rootEndpoint.Inbox.AgentEvents(root).Where(item => item.ChainId == chain.ChainId).ToArray();
                var reservations = facts.Where(item => item.State == AgentDeliveryState.ParticipantReserved).ToArray();
                if (reservations.Length == 0 && facts.Any(item => item.State == AgentDeliveryState.Prepared || item.State == AgentDeliveryState.SpawnPrepared))
                    throw new AgentCommunicationException("agent_participants_untracked", "Начните совместную работу новым сообщением пользователя: участники прежней цепочки не учитывались.");
                var participants = new HashSet<string>(reservations.Select(item => item.Target.ChatId), StringComparer.Ordinal) { root.Id };
                if (!participants.Contains(source.Id))
                    throw new AgentCommunicationException("agent_participant_unknown", "Отправитель не зарегистрирован в этой совместной работе.");
                if (reservations.Any(item => item.OperationId == operationId))
                    throw new AgentCommunicationException("agent_participant_unknown_effect", "Участник зарезервирован, но операция не подтверждена. Автоматический повтор запрещён.");
                var limit = Math.Min(capturedLimit, _currentLimit?.Invoke() ?? capturedLimit);
                if (limit <= 0)
                    throw new AgentCommunicationException("agent_communication_disabled", "Межагентное общение отключено в настройках.");
                if (!participants.Contains(target.ChatId) && participants.Count >= limit)
                    throw new AgentCommunicationException("agent_participant_limit", "Достигнут максимум участников совместной работы (" + limit + "), включая исходный чат. Продолжайте с текущими участниками.");
                dispatch();
                if (!participants.Contains(target.ChatId))
                    rootEndpoint.Inbox.AppendAgentEvent(root, new AgentCommunicationEvent {
                        State = AgentDeliveryState.ParticipantReserved, ChainId = chain.ChainId,
                        OperationId = operationId, Fingerprint = fingerprint, Target = target });
            }
        }
    }
}
