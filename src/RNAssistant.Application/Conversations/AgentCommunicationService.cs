using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Application.Conversations
{
    [JsonConverter(typeof(StringEnumConverter))]
    internal enum AgentDeliveryState { Prepared, Accepted, Granted }

    internal sealed class AgentCommunicationEvent
    {
        public AgentDeliveryState State { get; set; }
        public string ChainId { get; set; }
        public int Limit { get; set; }
        public string OperationId { get; set; }
        public string Fingerprint { get; set; }
        public AgentChatAddress Target { get; set; }
    }

    internal sealed class AgentConversationEndpoint
    {
        internal string ViewId;
        internal AgentChatAddress Address;
        internal ConversationInboxService Inbox;
        internal Func<ChatSession> Load;
        internal Func<ChatSession, string> Validate;
        internal Action<ChatSession> Start;
    }

    internal sealed class AgentPeer
    {
        [JsonProperty("agent_id")]
        public string AgentId { get; set; }
        [JsonProperty("is_self")]
        public bool IsSelf { get; set; }
        public string Title { get; set; }
        public string ScopeTitle { get; set; }
        public string Source { get; set; }
        public string Status { get; set; }
    }

    internal sealed class AgentCommunicationState
    {
        public bool Enabled { get; set; } = true;
        public string ChainId { get; set; }
        public int Limit { get; set; }
        public int Sent { get; set; }
        public int Remaining { get; set; }
        public bool LimitReached { get; set; }
    }

    internal sealed class AgentDeliveryReceipt
    {
        public string Status { get; set; }
        [JsonProperty("agent_id")]
        public string AgentId { get; set; }
        public string Title { get; set; }
        public string Text { get; set; }
    }

    [JsonConverter(typeof(StringEnumConverter))]
    internal enum AgentWaitStatus { MessagesReady, TimedOut, InputPending }

    internal sealed class AgentWaitResult
    {
        public AgentWaitStatus Status { get; set; }
        public IReadOnlyList<AgentPeer> Peers { get; set; }
    }

    internal sealed class AgentCommunicationException : InvalidOperationException
    {
        internal string Code { get; }
        internal AgentCommunicationException(string code, string message) : base(message) { Code = code; }
    }

    // Only live endpoint registrations are retained here. Inputs, receipts and budgets
    // are canonical conversation events. Waiting uses the existing Inbox input boundary;
    // no service lock or Office document gate is held while awaiting a message.
    internal sealed class AgentCommunicationService
    {
        private readonly object _sync = new object();
        private readonly Func<int> _currentLimit;
        private readonly Dictionary<string, AgentConversationEndpoint> _open =
            new Dictionary<string, AgentConversationEndpoint>(StringComparer.Ordinal);

        internal AgentCommunicationService(Func<int> currentLimit = null) { _currentLimit = currentLimit; }
        internal bool IsEnabled(int limit) { return limit > 0 && (_currentLimit == null || _currentLimit() > 0); }
        private void RequireEnabled(int limit)
        {
            if (!IsEnabled(limit)) throw new AgentCommunicationException("agent_communication_disabled",
                "Межагентное общение отключено: лимит сообщений в настройках равен 0.");
        }

        internal void Open(AgentConversationEndpoint endpoint)
        {
            if (endpoint?.Address == null || string.IsNullOrWhiteSpace(endpoint.Address.ChatId) ||
                string.IsNullOrWhiteSpace(endpoint.ViewId)) throw new ArgumentException("An exact open chat is required.");
            lock (_sync) _open[endpoint.ViewId] = endpoint;
        }

        internal void Close(string viewId)
        {
            lock (_sync) _open.Remove(viewId);
        }

        internal void Clear() { lock (_sync) _open.Clear(); }
        private AgentConversationEndpoint Find(string chatId)
        { return _open.Values.LastOrDefault(item => item.Address.ChatId == chatId); }

        private AgentConversationEndpoint FindAgent(string agentId)
        {
            var matches = _open.Values.Where(item => item.Address.AgentId == agentId)
                .GroupBy(item => item.Address.ChatId, StringComparer.Ordinal).Select(group => group.Last()).ToArray();
            if (matches.Length > 1) throw new AgentCommunicationException("agent_address_ambiguous",
                "Короткий адрес неоднозначен. Сообщение не отправлено.");
            return matches.SingleOrDefault();
        }

        internal IReadOnlyList<AgentPeer> List(string senderId, int limit = AppSettings.DefaultMaxAgentMessages)
        {
            lock (_sync)
            {
                RequireEnabled(limit);
                return _open.Values.GroupBy(item => item.Address.ChatId, StringComparer.Ordinal).Select(group => group.Last())
                    .Select(item => {
                        var peer = item.Inbox.AgentPeer(item.Address);
                        peer.IsSelf = item.Address.ChatId == senderId;
                        return peer;
                    }).OrderByDescending(peer => peer.IsSelf).ToArray();
            }
        }

        internal static AgentChatAddress Address(ChatSession session)
        {
            return new AgentChatAddress { ChatId = session.Id, Title = session.Title,
                Source = session.WorkspaceId == null ? "office" : "workspace", WorkspaceId = session.WorkspaceId,
                ScopeTitle = session.DocumentTitle };
        }

        internal async Task<AgentWaitResult> WaitAsync(ConversationInboxService inbox, ChatSession session,
            int limit, string runId, int timeoutSeconds, CancellationToken token)
        {
            RequireEnabled(limit);
            if (timeoutSeconds < 1 || timeoutSeconds > 10)
                throw new AgentCommunicationException("agent_wait_invalid", "Ожидание: от 1 до 10 секунд.");
            var status = await inbox.WaitForAgentsAsync(session, runId, timeoutSeconds, token).ConfigureAwait(false);
            return new AgentWaitResult { Status = status, Peers = List(session.Id, limit) };
        }

        internal static AgentCommunicationContext Context(ChatSession session, int limit)
        {
            var input = ConversationInputMessage.Latest(session);
            if (input == null) throw new AgentCommunicationException("agent_input_required", "Нет входящего запроса для общения.");
            return input.AgentCommunication ?? new AgentCommunicationContext {
                ChainId = session.Id + ":" + input.Id, Limit = Math.Max(1, limit) };
        }

        internal AgentCommunicationState State(ConversationInboxService inbox, ChatSession session, int limit)
        {
            lock (_sync)
            {
                if (!IsEnabled(limit)) return new AgentCommunicationState { Enabled = false };
                if (ConversationInputMessage.Latest(session) == null) return new AgentCommunicationState();
                var chain = Context(session, limit);
                return State(inbox.AgentEvents(session), chain);
            }
        }

        internal AgentCommunicationState State(string chatId, int limit)
        {
            lock (_sync)
            {
                if (!IsEnabled(limit)) return new AgentCommunicationState { Enabled = false };
                AgentConversationEndpoint target;
                if ((target = Find(chatId)) == null) throw new InvalidOperationException("Чат закрыт.");
                return State(target.Inbox, target.Load(), limit);
            }
        }

        private static AgentCommunicationState State(IReadOnlyList<AgentCommunicationEvent> events, AgentCommunicationContext chain)
        {
            var facts = events.Where(item => item.ChainId == chain.ChainId).ToArray();
            var pinned = facts.FirstOrDefault();
            var limit = pinned?.Limit ?? chain.Limit;
            var sent = facts.Where(item => item.State == AgentDeliveryState.Prepared)
                .Select(item => item.OperationId).Distinct(StringComparer.Ordinal).Count();
            var grants = facts.Where(item => item.State == AgentDeliveryState.Granted)
                .Select(item => item.OperationId).Distinct(StringComparer.Ordinal).Count();
            var remaining = Math.Max(0L, (long)limit * (grants + 1L) - sent);
            return new AgentCommunicationState { ChainId = chain.ChainId, Limit = limit, Sent = sent,
                Remaining = (int)Math.Min(int.MaxValue, remaining), LimitReached = remaining == 0 };
        }

        internal AgentDeliveryReceipt Send(ConversationInboxService sourceInbox, ChatSession source, int limit,
            string runId, string operationId, string agentId, string text, Action dispatch)
        {
            AgentConversationEndpoint target = null;
            ChatSession recipient = null;
            var schedule = false;
            AgentDeliveryReceipt receipt;
            try
            {
            lock (_sync)
            {
                RequireEnabled(limit);
                if (string.IsNullOrWhiteSpace(text) || text.Length > 128 * 1024)
                    throw new AgentCommunicationException("agent_message_invalid", "Требуется текст до 128 КиБ.");
                if (agentId == AgentChatAddress.IdFor(source.Id))
                    throw new AgentCommunicationException("agent_self_send", "Нельзя отправить сообщение своему чату.");
                var chain = Context(source, limit);
                var facts = sourceInbox.AgentEvents(source);
                var fingerprint = ConversationInputJournal.Fingerprint(new AgentDeliveryReceipt { AgentId = agentId, Text = text });
                var existing = facts.FirstOrDefault(item => item.OperationId == operationId);
                if (existing != null && existing.Fingerprint != fingerprint)
                    throw new AgentCommunicationException("agent_operation_conflict", "Операция уже принадлежит другому сообщению.");
                if (existing != null && facts.Any(item => item.OperationId == operationId && item.State == AgentDeliveryState.Accepted))
                    return new AgentDeliveryReceipt { Status = "queued", AgentId = existing.Target.AgentId,
                        Title = existing.Target.Title, Text = text };
                if ((target = existing == null ? FindAgent(agentId) : Find(existing.Target.ChatId)) == null)
                    throw new AgentCommunicationException(existing == null ? "agent_chat_not_found" : "agent_delivery_unknown",
                        existing == null ? "Открытый агент с указанным agent_id не найден. Обновите agents.list и скопируйте короткий ID адресата; этот отказ не доказывает, что чат закрыт." : "Получатель недоступен для проверки прежней доставки. Повтор запрещён.");
                var budget = State(facts, chain);
                if (existing == null && budget.LimitReached)
                    throw new AgentCommunicationException("agent_message_limit", "Лимит общения исчерпан. Пользователь может продолжить общение в чате.");
                string runtimeKey;
                try
                {
                    recipient = target.Load();
                    runtimeKey = target.Validate(recipient);
                    ConversationRunPolicy.RequireCurrentSession(recipient);
                }
                catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is System.IO.IOException)
                { throw new AgentCommunicationException("agent_chat_unavailable", error.Message); }
                var accepted = target.Inbox.FindOperation(recipient, operationId, fingerprint);
                if (existing != null)
                {
                    if (accepted == null)
                        throw new AgentCommunicationException("agent_delivery_unknown", "Предыдущая доставка не подтверждена. Автоматический повтор запрещён.");
                    return Receipt(target, text);
                }
                var input = new ConversationInput { Id = Guid.NewGuid().ToString("N"), OperationId = operationId,
                    RequestFingerprint = fingerprint, Text = text, CreatedUtc = DateTime.UtcNow,
                    Delivery = InputDelivery.Queue, DocumentRuntimeKey = runtimeKey,
                    AgentCommunication = new AgentCommunicationContext { ChainId = chain.ChainId, Limit = budget.Limit,
                        Sender = Address(source), SenderRunId = runId } };
                // Reserve before destination append. An unresolved reservation conservatively
                // consumes one slot, and is never replayed on recovery.
                dispatch();
                sourceInbox.AppendAgentEvent(source, new AgentCommunicationEvent { State = AgentDeliveryState.Prepared,
                    ChainId = chain.ChainId, Limit = budget.Limit, OperationId = operationId,
                    Fingerprint = fingerprint, Target = target.Address });
                target.Inbox.Submit(recipient, input);
                if (target.Inbox.FindOperation(recipient, operationId, fingerprint) == null)
                    throw new InvalidOperationException("Нет подтверждения записи сообщения в очередь.");
                schedule = true;
                sourceInbox.AppendAgentEvent(source, new AgentCommunicationEvent { State = AgentDeliveryState.Accepted,
                    ChainId = chain.ChainId, Limit = budget.Limit, OperationId = operationId,
                    Fingerprint = fingerprint, Target = target.Address });
                receipt = Receipt(target, text);
            }
            return receipt;
            }
            finally
            {
                // Even if the sender receipt fails, accepted destination work is scheduled.
                // Scheduling never resumes a stopped queue or repeats uncertain delivery.
                if (schedule)
                    try { target.Start(recipient); }
                    catch (Exception error) { System.Diagnostics.Trace.TraceError("Agent queue scheduling: {0}", error.Message); }
            }
        }

        private static AgentDeliveryReceipt Receipt(AgentConversationEndpoint target, string text)
        { return new AgentDeliveryReceipt { Status = "queued", AgentId = target.Address.AgentId, Title = target.Address.Title, Text = text }; }

        internal AgentCommunicationState Continue(string chatId, string chainId, string operationId, int limit)
        {
            AgentConversationEndpoint target;
            ChatSession session;
            AgentCommunicationState result;
            lock (_sync)
            {
                RequireEnabled(limit);
                if ((target = Find(chatId)) == null) throw new InvalidOperationException("Чат закрыт.");
                session = target.Load();
                if (target.Inbox.HasActiveWork(chatId)) throw new InvalidOperationException("Дождитесь завершения текущего запроса.");
                var chain = Context(session, limit);
                if (chain.ChainId != chainId) throw new InvalidOperationException("Цепочка общения уже изменилась.");
                var facts = target.Inbox.AgentEvents(session);
                var grant = facts.FirstOrDefault(item => item.OperationId == operationId && item.State == AgentDeliveryState.Granted);
                var state = State(facts, chain);
                if (grant == null && !state.LimitReached) throw new InvalidOperationException("Лимит общения ещё не исчерпан.");
                var runtimeKey = target.Validate(session);
                target.Inbox.ContinueAgentCommunication(session, chainId,
                    grant == null ? new AgentCommunicationEvent { State = AgentDeliveryState.Granted,
                        ChainId = chainId, Limit = state.Limit, OperationId = operationId } : null,
                    new ConversationInput { Id = operationId, OperationId = operationId,
                    RequestFingerprint = ConversationInputJournal.Fingerprint(chainId), Delivery = InputDelivery.Queue,
                    Text = "Продолжи межагентное общение. Пользователь предоставил следующий бюджет сообщений. Не повторяй доставки с неизвестным результатом.",
                    CreatedUtc = DateTime.UtcNow, DocumentRuntimeKey = runtimeKey,
                    AgentCommunication = new AgentCommunicationContext { ChainId = chainId, Limit = state.Limit } });
                result = State(target.Inbox.AgentEvents(session), chain);
            }
            target.Start(session);
            return result;
        }
    }
}
