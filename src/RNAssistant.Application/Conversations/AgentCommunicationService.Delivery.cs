using System;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Tools;

namespace RNAssistant.Application.Conversations
{
    internal sealed partial class AgentCommunicationService
    {
        private AgentDeliveryReceipt SendAtomic(ConversationInboxService inbox, ChatSession source, int capturedLimit,
            string operation, string agentId, string text, Action dispatch, bool expectReply, string replyTo,
            bool assignment, AgentAssignmentOutcome? resolution, ToolExecutionContext execution)
        {
            RequireEnabled(capturedLimit);
            if (execution == null) throw new AgentCommunicationException("agent_call_required", "Нет текущего вызова инструмента.");
            AgentConversationScope scope;
            lock (_sync) scope = Scope(source);
            if (scope.Endpoint == null) throw new AgentCommunicationException("agent_scope_unavailable", "Runtime адресной доставки недоступен.");
            // Exact indexed lookup and endpoint loading happen outside the service
            // registry lock; list/discovery is not a prerequisite for a known address.
            var address = inbox.FindAgent(source, agentId);
            if (address == null || address.ChatId == source.Id)
                throw new AgentCommunicationException("agent_chat_not_found", "Укажите точный адрес другого чата своей области.");
            var target = scope.Endpoint(address);
            var recipient = target.Load();
            if (!scope.Contains(recipient) || recipient.Id != address.ChatId)
                throw new AgentCommunicationException("agent_chat_unavailable", "Получатель принадлежит другой области.");
            var runtimeKey = target.Validate(recipient);
            var chain = Context(source);
            if (replyTo != null)
            {
                var original = inbox.FindAgentMessage(source, replyTo);
                if (original?.AgentCommunication?.Sender?.AgentId != agentId || original.Status != ConversationInputStatus.Applied)
                    throw new AgentCommunicationException("agent_reply_invalid", "Ответ требует уже полученного сообщения этого адресата.");
                chain = original.AgentCommunication;
            }
            var limit = Math.Min(capturedLimit, _currentLimit?.Invoke() ?? capturedLimit);
            RequireEnabled(limit);
            var accepted = inbox.AcceptAgentDelivery(source, target.Inbox, recipient, new AgentDeliveryCommand {
                OperationId = operation, Execution = execution, AgentId = agentId, Text = text,
                RootChatId = chain.Root?.ChatId, ChainId = chain.ChainId, ParticipantLimit = limit,
                RecipientRuntimeKey = runtimeKey, ExpectReply = expectReply, ReplyTo = replyTo,
                Assignment = assignment, Resolution = resolution }, dispatch);
            // Wake/start only after durable read-back and both live cursors have
            // advanced. Scheduling is outside every Inbox/storage/service lock.
            try { target.Start?.Invoke(target.Inbox.Get(recipient).Session); }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("Agent queue scheduling: {0}", error.Message); }
            return Receipt(target, text, accepted.Change.Input.AgentCommunication.MessageId, assignment);
        }
    }
}
