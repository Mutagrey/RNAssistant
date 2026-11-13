using System;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Application.Conversations
{
    internal sealed class AgentSpawnReceipt
    {
        [JsonProperty("agent_id")] public string AgentId { get; set; }
        [JsonProperty("message_id", NullValueHandling = NullValueHandling.Ignore)] public string MessageId { get; set; }
        [JsonProperty("assignment", DefaultValueHandling = DefaultValueHandling.Ignore)] public bool Assignment { get; set; }
        public string Title { get; set; }
        public bool Created { get; set; }
        public string InitialMessage { get; set; }
        public string Error { get; set; }
    }

    internal sealed partial class AgentCommunicationService
    {
        internal AgentSpawnReceipt Spawn(ConversationInboxService inbox, ChatSession source, int limit,
            string runId, string operationId, string title, string initialMessage, Action dispatch, bool expectReply = false, bool assignment = false,
            ToolExecutionContext execution = null)
        {
            return inbox.AtomicAgentDelivery
                ? SpawnAtomic(inbox, source, limit, operationId, title, initialMessage, dispatch, expectReply, assignment, execution)
                : SpawnEvents(inbox, source, limit, runId, operationId, title, initialMessage, dispatch, expectReply, assignment);
        }

        private AgentSpawnReceipt SpawnAtomic(ConversationInboxService inbox, ChatSession source, int capturedLimit,
            string operation, string title, string initialText, Action dispatch, bool expectReply, bool assignment, ToolExecutionContext execution)
        {
            RequireEnabled(capturedLimit);
            if (execution == null) throw new AgentCommunicationException("agent_call_required", "Нет текущего вызова инструмента.");
            AgentConversationScope scope;
            lock (_sync) scope = Scope(source);
            if (scope.Prepare == null || scope.Endpoint == null)
                throw new AgentCommunicationException("agent_spawn_unavailable", "Создание участников в этой области недоступно.");
            var chain = Context(source);
            if (chain.Root == null)
                throw new AgentCommunicationException("agent_spawn_chain_unavailable", "Для создания участников начните новую цепочку пользовательским сообщением.");
            var runtimeKey = scope.Endpoint(Address(source)).Validate(source);
            var child = scope.Prepare(source, title);
            if (child == null || child.Id == source.Id || child.Revision != 0 || !scope.Contains(child))
                throw new InvalidOperationException("Creation must prepare a fresh conversation in the exact source scope.");
            var limit = Math.Min(capturedLimit, _currentLimit?.Invoke() ?? capturedLimit);
            RequireEnabled(limit);
            var initial = inbox.AcceptAgentSpawn(source, child, new AgentSpawnCommand { OperationId = operation,
                Execution = execution, RootChatId = chain.Root.ChatId, ChainId = chain.ChainId, ParticipantLimit = limit,
                Title = title, InitialText = initialText, RuntimeKey = runtimeKey, ExpectReply = expectReply, Assignment = assignment }, dispatch);
            // The receipt proves creation/intake independently of UI or scheduler
            // availability. Their failure cannot erase that proof or create again.
            try
            {
                var target = scope.Endpoint(Address(child));
                if (initial == null) target.Changed?.Invoke(child);
                else if (target.Inbox.AdoptSpawn(inbox, child, initial)) target.Start?.Invoke(child);
            }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("Agent spawn notification/scheduling: {0}", error.Message); }
            return new AgentSpawnReceipt { AgentId = AgentChatAddress.IdFor(child.Id), Title = child.Title, Created = true,
                InitialMessage = initial == null ? "not_requested" : "queued", MessageId = initial?.Change.Input.Id,
                Assignment = initial?.Change.Input.AgentCommunication.Assignment == true };
        }

        private AgentSpawnReceipt SpawnEvents(ConversationInboxService inbox, ChatSession source, int limit,
            string runId, string operationId, string title, string initialMessage, Action dispatch, bool expectReply, bool assignment)
        {
            ChatSession child;
            lock (_sync)
            {
                RequireEnabled(limit);
                ConversationRunPolicy.RequireCurrentSession(source);
                if ((expectReply || assignment) && initialMessage == null)
                    throw new AgentCommunicationException("agent_spawn_invalid", "expect_reply и assignment требуют initial_message.");
                if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 200 || title.IndexOfAny(new[] { '\r', '\n' }) >= 0 ||
                    initialMessage != null && (string.IsNullOrWhiteSpace(initialMessage) || initialMessage.Length > 128 * 1024))
                    throw new AgentCommunicationException("agent_spawn_invalid", "Требуется название в одну строку до 200 знаков; начальное сообщение — до 128 КиБ.");
                var scope = Scope(source);
                if (scope.Prepare == null || scope.Commit == null || scope.Endpoint == null)
                    throw new AgentCommunicationException("agent_spawn_unavailable", "Создание участников в этой области недоступно.");
                var chain = Context(source);
                if (chain.Root == null)
                    throw new AgentCommunicationException("agent_spawn_chain_unavailable", "Для создания участников начните новую цепочку пользовательским сообщением.");
                var rootEndpoint = FindAgent(source, chain.Root.AgentId);
                if (rootEndpoint == null || rootEndpoint.Address.ChatId != chain.Root.ChatId)
                    throw new AgentCommunicationException("agent_spawn_root_unavailable", "Исходный чат цепочки недоступен; лимит создания нельзя проверить.");
                var root = rootEndpoint.Load();
                rootEndpoint.Validate(root);
                lock (rootEndpoint.Inbox.Get(root).Sync)
                {
                    var parts = new[] { source.Id, title.Trim(), initialMessage, expectReply ? "reply" : "message" };
                    var fingerprint = ConversationInputJournal.Fingerprint(assignment ? parts.Concat(new[] { "assignment" }) : parts);
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
                        ReserveParticipant(source, chain, limit, operationId, fingerprint, Address(child), dispatch);
                        rootEndpoint.Inbox.AppendAgentEvent(root, new AgentCommunicationEvent {
                            State = AgentDeliveryState.SpawnPrepared, ChainId = chain.ChainId,
                            OperationId = operationId, Fingerprint = fingerprint, Target = Address(child) });
                        scope.Commit(child);
                        var saved = scope.Endpoint(Address(child)).Load();
                        if (saved?.AgentCreation?.OperationId != operationId || saved.AgentCreation.Fingerprint != fingerprint)
                            throw new InvalidOperationException("Created conversation read-back failed.");
                        child = saved;
                        rootEndpoint.Inbox.AppendAgentEvent(root, new AgentCommunicationEvent {
                            State = AgentDeliveryState.Spawned, ChainId = chain.ChainId,
                            OperationId = operationId, Fingerprint = fingerprint, Target = Address(child) });
                    }
                }
            }
            var receipt = new AgentSpawnReceipt { AgentId = AgentChatAddress.IdFor(child.Id), Title = child.Title,
                Created = true, InitialMessage = "not_requested" };
            if (initialMessage == null) return receipt;
            try
            {
                var initial = Send(inbox, source, limit, runId, operationId + "_initial", receipt.AgentId, initialMessage, dispatch, expectReply, assignment: assignment);
                receipt.MessageId = initial.MessageId;
                receipt.Assignment = initial.Assignment;
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
