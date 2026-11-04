using System;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Application.Conversations
{
    internal sealed class AgentSpawnReceipt
    {
        [JsonProperty("agent_id")] public string AgentId { get; set; }
        [JsonProperty("message_id", NullValueHandling = NullValueHandling.Ignore)] public string MessageId { get; set; }
        public string Title { get; set; }
        public bool Created { get; set; }
        public string InitialMessage { get; set; }
        public string Error { get; set; }
    }

    internal sealed partial class AgentCommunicationService
    {
        internal const int MaximumSpawnedThreads = 8;

        internal AgentSpawnReceipt Spawn(ConversationInboxService inbox, ChatSession source, int limit,
            string runId, string operationId, string title, string initialMessage, Action dispatch, bool expectReply = false)
        {
            ChatSession child;
            lock (_sync)
            {
                RequireEnabled(limit);
                ConversationRunPolicy.RequireCurrentSession(source);
                if (expectReply && initialMessage == null)
                    throw new AgentCommunicationException("agent_spawn_invalid", "expect_reply требует initial_message.");
                if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 200 || title.IndexOfAny(new[] { '\r', '\n' }) >= 0 ||
                    initialMessage != null && (string.IsNullOrWhiteSpace(initialMessage) || initialMessage.Length > 128 * 1024))
                    throw new AgentCommunicationException("agent_spawn_invalid", "Требуется название в одну строку до 200 знаков; начальное сообщение — до 128 КиБ.");
                var scope = Scope(source);
                if (scope.Prepare == null || scope.Commit == null || scope.Endpoint == null)
                    throw new AgentCommunicationException("agent_spawn_unavailable", "Создание участников в этой области недоступно.");
                var chain = Context(source, limit);
                if (chain.Root == null)
                    throw new AgentCommunicationException("agent_spawn_chain_unavailable", "Для создания участников начните новую цепочку пользовательским сообщением.");
                var rootEndpoint = FindAgent(source, chain.Root.AgentId);
                if (rootEndpoint == null || rootEndpoint.Address.ChatId != chain.Root.ChatId)
                    throw new AgentCommunicationException("agent_spawn_root_unavailable", "Исходный чат цепочки недоступен; лимит создания нельзя проверить.");
                var root = rootEndpoint.Load();
                rootEndpoint.Validate(root);
                lock (rootEndpoint.Inbox.Get(root).Sync)
                {
                    var fingerprint = ConversationInputJournal.Fingerprint(new[] { source.Id, title.Trim(), initialMessage, expectReply ? "reply" : "message" });
                    var facts = rootEndpoint.Inbox.AgentEvents(root);
                    var prior = facts.FirstOrDefault(item => item.OperationId == operationId && item.State == AgentDeliveryState.SpawnPrepared);
                    if (prior != null)
                    {
                        if (prior.Fingerprint != fingerprint)
                            throw new AgentCommunicationException("agent_operation_conflict", "Операция создания уже принадлежит другому запросу.");
                        var endpoint = FindAgent(source, prior.Target.AgentId);
                        child = endpoint?.Load();
                        if (child?.AgentCreation?.OperationId != operationId || child.Id != prior.Target.ChatId || child.AgentCreation.Fingerprint != fingerprint)
                            throw new AgentCommunicationException("agent_spawn_unknown", "Прежнее создание не подтверждено. Автоматический повтор запрещён.");
                    }
                    else
                    {
                        if (facts.Count(item => item.ChainId == chain.ChainId && item.State == AgentDeliveryState.SpawnPrepared) >= MaximumSpawnedThreads)
                            throw new AgentCommunicationException("agent_spawn_limit", "В этой цепочке уже зарезервированы 8 новых участников, включая вложенные создания.");
                        child = scope.Prepare(source, title.Trim());
                        if (child == null || child.Revision != 0 || child.Id == source.Id || !scope.Contains(child))
                            throw new InvalidOperationException("Creation must prepare a fresh conversation in the exact source scope.");
                        child.Mode = source.Mode;
                        child.MutationPolicy = source.MutationPolicy;
                        child.Model = string.IsNullOrWhiteSpace(source.LastRun?.ModelConfiguration?.Model)
                            ? source.Model : source.LastRun.ModelConfiguration.Model;
                        child.ReasoningEnabled = source.ReasoningEnabled;
                        child.TitleAssignedByAgent = true;
                        child.AgentCreation = new AgentThreadCreation { OperationId = operationId, Fingerprint = fingerprint,
                            ChainId = chain.ChainId, Creator = Address(source), Root = chain.Root };
                        dispatch();
                        rootEndpoint.Inbox.AppendAgentEvent(root, new AgentCommunicationEvent {
                            State = AgentDeliveryState.SpawnPrepared, ChainId = chain.ChainId, Limit = chain.Limit,
                            OperationId = operationId, Fingerprint = fingerprint, Target = Address(child) });
                        scope.Commit(child);
                        var saved = scope.Endpoint(child).Load();
                        if (saved?.AgentCreation?.OperationId != operationId || saved.AgentCreation.Fingerprint != fingerprint)
                            throw new InvalidOperationException("Created conversation read-back failed.");
                        child = saved;
                        rootEndpoint.Inbox.AppendAgentEvent(root, new AgentCommunicationEvent {
                            State = AgentDeliveryState.Spawned, ChainId = chain.ChainId, Limit = chain.Limit,
                            OperationId = operationId, Fingerprint = fingerprint, Target = Address(child) });
                    }
                }
            }
            var receipt = new AgentSpawnReceipt { AgentId = AgentChatAddress.IdFor(child.Id), Title = child.Title,
                Created = true, InitialMessage = "not_requested" };
            if (initialMessage == null) return receipt;
            try
            {
                var initial = Send(inbox, source, limit, runId, operationId + "_initial", receipt.AgentId, initialMessage, dispatch, expectReply);
                receipt.MessageId = initial.MessageId;
                receipt.InitialMessage = "queued";
            }
            catch (AgentCommunicationException error)
            {
                // The participant already exists. A rejected/uncertain first input
                // must never turn this into permission to spawn a replacement.
                receipt.InitialMessage = error.Code == "agent_delivery_unknown" ? "unknown" : "rejected";
                receipt.Error = error.Code;
            }
            catch (Exception error) when (error is System.IO.IOException || error is InvalidOperationException)
            { receipt.InitialMessage = "unknown"; receipt.Error = "agent_initial_message_unknown"; }
            return receipt;
        }
    }
}
