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
    internal enum AgentDeliveryState { Prepared, Accepted, Granted, SpawnPrepared, Spawned, CancelPrepared, CancelRecorded }

    internal sealed class AgentCommunicationEvent
    {
        public AgentDeliveryState State { get; set; }
        public string ChainId { get; set; }
        public int Limit { get; set; }
        public string OperationId { get; set; }
        public string Fingerprint { get; set; }
        public AgentChatAddress Target { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string MessageId { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string ReplyTo { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public bool ExpectReply { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string TargetRunId { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public AgentCancellationStatus? Cancellation { get; set; }
    }

    internal sealed class AgentConversationEndpoint
    {
        internal AgentChatAddress Address;
        internal ConversationInboxService Inbox;
        internal Func<ChatSession> Load;
        internal Func<ChatSession, string> Validate;
        internal Action<ChatSession> Start;
        internal Action<ChatSession> Changed;
        internal Func<IDisposable> Reserve;
        internal Action<ChatSession> ValidateResume;
        internal Func<bool> IsClosing;
        internal ConversationRunOwner Runs;
        internal RNAssistant.Core.Persistence.IConversationStore Store;
        internal RNAssistant.Core.Storage.ChatBlobStore Payloads;
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

    internal sealed class AgentPeerPage
    {
        public IReadOnlyList<AgentPeer> Peers { get; set; }
        public bool Truncated { get; set; }
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
        [JsonProperty("message_id")] public string MessageId { get; set; }
        public string Title { get; set; }
        public string Text { get; set; }
    }

    [JsonConverter(typeof(StringEnumConverter))]
    internal enum AgentWaitStatus { MessagesReady, RepliesReady, TimedOut, InputPending }

    internal sealed class AgentReplyMatch
    {
        [JsonProperty("request_id")] public string RequestId { get; set; }
        [JsonProperty("message_id")] public string MessageId { get; set; }
        [JsonProperty("agent_id")] public string AgentId { get; set; }
    }

    internal sealed class AgentWaitResult
    {
        public AgentWaitStatus Status { get; set; }
        public IReadOnlyList<AgentPeer> Peers { get; set; }
        public bool PeersTruncated { get; set; }
        public IReadOnlyList<AgentReplyMatch> Matches { get; set; } = new AgentReplyMatch[0];
    }

    internal sealed class AgentCommunicationException : InvalidOperationException
    {
        internal string Code { get; }
        internal AgentCommunicationException(string code, string message) : base(message) { Code = code; }
    }

    // Bound runtime scopes read participants from the canonical chat catalog.
    // Inputs, receipts and budgets remain conversation events, not view state.
    internal sealed partial class AgentCommunicationService
    {
        private readonly object _sync = new object();
        private readonly Func<int> _currentLimit;
        private readonly Dictionary<string, AgentConversationScope> _scopes =
            new Dictionary<string, AgentConversationScope>(StringComparer.Ordinal);

        internal AgentCommunicationService(Func<int> currentLimit = null) { _currentLimit = currentLimit; }
        internal bool IsEnabled(int limit) { return limit > 0 && (_currentLimit == null || _currentLimit() > 0); }
        private void RequireEnabled(int limit)
        {
            if (!IsEnabled(limit)) throw new AgentCommunicationException("agent_communication_disabled",
                "Межагентное общение отключено: лимит сообщений в настройках равен 0.");
        }

        internal void Register(AgentConversationScope scope)
        {
            if (scope == null || string.IsNullOrEmpty(scope.Key) || scope.Contains == null || scope.ReadCatalog == null)
                throw new ArgumentException("A bound conversation catalog is required.");
            lock (_sync) _scopes[scope.Key] = scope;
        }
        internal void Clear() { lock (_sync) _scopes.Clear(); }
        internal void Unregister(string key) { lock (_sync) _scopes.Remove(key); }
        private AgentConversationScope Scope(ChatSession source)
        {
            var scope = _scopes.Values.SingleOrDefault(item => item.Contains(source));
            if (scope == null)
                throw new AgentCommunicationException("agent_scope_unavailable", "Runtime области чата недоступен.");
            return scope;
        }
        private AgentConversationEndpoint Find(string chatId, string source = null, string workspaceId = null)
        {
            return _scopes.Values.Where(scope => source == null || scope.Key ==
                (source == "workspace" ? "workspace:" + workspaceId : "office"))
                .SelectMany(scope => scope.ReadCatalog()).SingleOrDefault(item => item.Address.ChatId == chatId);
        }

        private AgentConversationEndpoint FindAgent(ChatSession source, string agentId)
        {
            var matches = Scope(source).ReadCatalog().Where(item => item.Address.AgentId == agentId)
                .GroupBy(item => item.Address.ChatId, StringComparer.Ordinal).Select(group => group.Last()).ToArray();
            if (matches.Length > 1) throw new AgentCommunicationException("agent_address_ambiguous",
                "Короткий адрес неоднозначен. Сообщение не отправлено.");
            return matches.SingleOrDefault();
        }

        internal IReadOnlyList<AgentPeer> List(ChatSession source, int limit = AppSettings.DefaultMaxAgentMessages)
        {
            lock (_sync)
            {
                RequireEnabled(limit);
                return Scope(source).ReadCatalog().GroupBy(item => item.Address.ChatId, StringComparer.Ordinal).Select(group => group.Last())
                    .Select(item => {
                        var peer = item.Inbox.AgentPeer(item.Address);
                        peer.IsSelf = item.Address.ChatId == source.Id;
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

        internal AgentPeerPage Discover(ChatSession source, int limit, string query = null, string agentId = null)
        {
            var peers = List(source, limit).Where(peer =>
                (string.IsNullOrEmpty(agentId) || peer.AgentId == agentId) &&
                (string.IsNullOrWhiteSpace(query) || (peer.Title ?? "").IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase) >= 0))
                .Take(51).ToArray();
            return new AgentPeerPage { Peers = peers.Take(50).ToArray(), Truncated = peers.Length > 50 };
        }

        internal AgentWaitCondition PrepareWait(ConversationInboxService inbox, ChatSession session, int limit,
            int timeoutSeconds, string[] requests, string mode)
        {
            RequireEnabled(limit);
            if (timeoutSeconds < 1 || timeoutSeconds > 10)
                throw new AgentCommunicationException("agent_wait_invalid", "Ожидание: от 1 до 10 секунд.");
            if (mode != "any" && mode != "all" || requests == null && mode != "any" ||
                requests != null && (requests.Length == 0 || requests.Length > 32 ||
                    requests.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 64) || requests.Distinct(StringComparer.Ordinal).Count() != requests.Length))
                throw new AgentCommunicationException("agent_wait_invalid", "Укажите 1–32 разных message_id запросов и mode any или all.");
            ResolveWaitRequests(inbox, session, requests);
            var now = DateTime.UtcNow;
            return new AgentWaitCondition(now, now.AddSeconds(timeoutSeconds), requests, mode == "all");
        }

        private static Dictionary<string, AgentChatAddress> ResolveWaitRequests(ConversationInboxService inbox,
            ChatSession session, string[] requests)
        {
            Dictionary<string, AgentChatAddress> expected = null;
            if (requests != null)
            {
                var facts = inbox.AgentEvents(session);
                expected = new Dictionary<string, AgentChatAddress>(StringComparer.Ordinal);
                foreach (var id in requests)
                {
                    var request = facts.SingleOrDefault(item => item.State == AgentDeliveryState.Prepared && item.MessageId == id);
                    if (request?.Target == null || !request.ExpectReply)
                        throw new AgentCommunicationException("agent_request_not_found", "Нет собственного запроса с expect_reply=true и указанным message_id.");
                    expected.Add(id, request.Target);
                }
            }
            return expected;
        }

        internal async Task<AgentWaitResult> CompleteWaitAsync(ConversationInboxService inbox, ChatSession session,
            int limit, string runId, AgentWaitCondition condition, CancellationToken token)
        {
            RequireEnabled(limit);
            var expected = ResolveWaitRequests(inbox, session, condition.RequestIds.Count == 0 ? null : condition.RequestIds.ToArray());
            var result = await inbox.WaitForAgentsAsync(session, runId, condition.DeadlineUtc, token, expected, condition.All).ConfigureAwait(false);
            var page = Discover(session, limit);
            result.Peers = page.Peers; result.PeersTruncated = page.Truncated;
            return result;
        }

        internal static AgentCommunicationContext Context(ChatSession session, int limit)
        {
            var input = ConversationInputMessage.Latest(session);
            if (input == null) throw new AgentCommunicationException("agent_input_required", "Нет входящего запроса для общения.");
            return input.AgentCommunication ?? new AgentCommunicationContext {
                ChainId = session.Id + ":" + input.Id, Limit = Math.Max(1, limit), Root = Address(session) };
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

        internal AgentCommunicationState State(string chatId, int limit, string source = null, string workspaceId = null)
        {
            lock (_sync)
            {
                if (!IsEnabled(limit)) return new AgentCommunicationState { Enabled = false };
                AgentConversationEndpoint target;
                if ((target = Find(chatId, source, workspaceId)) == null) throw new InvalidOperationException("Область чата недоступна.");
                RequireAddress(target, source, workspaceId);
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
            string runId, string operationId, string agentId, string text, Action dispatch,
            bool expectReply = false, string replyTo = null)
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
                if (replyTo != null)
                {
                    var original = sourceInbox.FindAgentMessage(source, replyTo);
                    if (original?.AgentCommunication?.Sender == null || original.Status != ConversationInputStatus.Applied ||
                        original.AgentCommunication.Sender.AgentId != agentId)
                        throw new AgentCommunicationException("agent_reply_invalid", "reply_to должен указывать на полученное сообщение адресата в этом чате.");
                    chain = original.AgentCommunication;
                }
                var senderPolicy = source.LastRun?.ConversationPolicy ?? ConversationRunPolicy.ForSession(source);
                var facts = sourceInbox.AgentEvents(source);
                var fingerprint = ConversationInputJournal.Fingerprint(new[] { agentId, text, expectReply ? "reply" : "message", replyTo });
                var existing = facts.FirstOrDefault(item => item.OperationId == operationId);
                if (existing != null && existing.Fingerprint != fingerprint)
                    throw new AgentCommunicationException("agent_operation_conflict", "Операция уже принадлежит другому сообщению.");
                if (existing != null && facts.Any(item => item.OperationId == operationId && item.State == AgentDeliveryState.Accepted))
                    return new AgentDeliveryReceipt { Status = "queued", AgentId = existing.Target.AgentId,
                        MessageId = existing.MessageId, Title = existing.Target.Title, Text = text };
                if ((target = FindAgent(source, existing == null ? agentId : existing.Target.AgentId)) == null)
                    throw new AgentCommunicationException(existing == null ? "agent_chat_not_found" : "agent_delivery_unknown",
                        existing == null ? "Агент не найден в области документа/проекта. Обновите agents.list и скопируйте точный адрес; закрытие вкладки не удаляет участника." : "Получатель недоступен для проверки прежней доставки. Повтор запрещён.");
                var budget = State(facts, chain);
                if (existing == null && budget.LimitReached)
                    throw new AgentCommunicationException("agent_message_limit", "Лимит общения исчерпан. Пользователь может продолжить общение в чате.");
                string runtimeKey;
                try
                {
                    recipient = target.Load();
                    if (Scope(recipient) != Scope(source)) throw new InvalidOperationException("Получатель принадлежит другой области.");
                    runtimeKey = target.Validate(recipient);
                    ConversationRunPolicy.RequireCurrentSession(recipient);
                    ConversationRunPolicy.ForSession(recipient).ForPeerInput(new AgentCommunicationContext {
                        Sender = Address(source), Policy = senderPolicy });
                }
                catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is System.IO.IOException)
                { throw new AgentCommunicationException("agent_chat_unavailable", error.Message); }
                var accepted = target.Inbox.FindOperation(recipient, operationId, fingerprint);
                if (existing != null)
                {
                    if (accepted == null)
                        throw new AgentCommunicationException("agent_delivery_unknown", "Предыдущая доставка не подтверждена. Автоматический повтор запрещён.");
                    if (accepted.AgentCommunication?.MessageId != existing.MessageId)
                        throw new AgentCommunicationException("agent_delivery_unknown", "Идентификатор прежней доставки не подтверждён. Повтор запрещён.");
                    return Receipt(target, text, existing.MessageId);
                }
                var messageId = "m-" + Guid.NewGuid().ToString("N");
                var input = new ConversationInput { Id = Guid.NewGuid().ToString("N"), OperationId = operationId,
                    RequestFingerprint = fingerprint, Text = text, CreatedUtc = DateTime.UtcNow,
                    Delivery = InputDelivery.Queue, DocumentRuntimeKey = runtimeKey,
                    AgentCommunication = new AgentCommunicationContext { ChainId = chain.ChainId, Limit = budget.Limit,
                        Sender = Address(source), SenderRunId = runId, Root = chain.Root, Policy = senderPolicy,
                        MessageId = messageId, ReplyTo = replyTo, ExpectReply = expectReply } };
                // Reserve before destination append. An unresolved reservation conservatively
                // consumes one slot, and is never replayed on recovery.
                dispatch();
                sourceInbox.AppendAgentEvent(source, new AgentCommunicationEvent { State = AgentDeliveryState.Prepared,
                    ChainId = chain.ChainId, Limit = budget.Limit, OperationId = operationId,
                    Fingerprint = fingerprint, Target = target.Address, MessageId = messageId, ReplyTo = replyTo, ExpectReply = expectReply });
                target.Inbox.Submit(recipient, input);
                if (target.Inbox.FindOperation(recipient, operationId, fingerprint)?.AgentCommunication?.MessageId != messageId)
                    throw new InvalidOperationException("Нет подтверждения записи сообщения в очередь.");
                schedule = true;
                sourceInbox.AppendAgentEvent(source, new AgentCommunicationEvent { State = AgentDeliveryState.Accepted,
                    ChainId = chain.ChainId, Limit = budget.Limit, OperationId = operationId,
                    Fingerprint = fingerprint, Target = target.Address, MessageId = messageId, ReplyTo = replyTo, ExpectReply = expectReply });
                receipt = Receipt(target, text, messageId);
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

        private static AgentDeliveryReceipt Receipt(AgentConversationEndpoint target, string text, string messageId)
        { return new AgentDeliveryReceipt { Status = "queued", AgentId = target.Address.AgentId, MessageId = messageId, Title = target.Address.Title, Text = text }; }

        private static void RequireAddress(AgentConversationEndpoint target, string source, string workspaceId)
        {
            if (source != null && (target.Address.Source != source || target.Address.WorkspaceId != workspaceId))
                throw new ArgumentException("Чат не принадлежит указанной области.");
        }

        internal AgentCommunicationState Continue(string chatId, string chainId, string operationId, int limit,
            string source = null, string workspaceId = null)
        {
            AgentConversationEndpoint target;
            ChatSession session;
            AgentCommunicationState result;
            lock (_sync)
            {
                RequireEnabled(limit);
                if ((target = Find(chatId, source, workspaceId)) == null) throw new InvalidOperationException("Область чата недоступна.");
                RequireAddress(target, source, workspaceId);
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
                    AgentCommunication = new AgentCommunicationContext { ChainId = chainId, Limit = state.Limit, Root = chain.Root } });
                result = State(target.Inbox.AgentEvents(session), chain);
            }
            target.Start(session);
            return result;
        }
    }
}
