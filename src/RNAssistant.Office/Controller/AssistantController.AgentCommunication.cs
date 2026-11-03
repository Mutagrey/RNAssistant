using System;
using RNAssistant.Application.Conversations;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        internal void ConfigureAgentCommunication(AgentCommunicationService service)
        { _toolExecutor.AgentCommunication = service; _toolExecutor.AgentInbox = _inbox; }

        internal AgentConversationEndpoint AgentEndpoint(string chatId, string viewId,
            Action<string, string, string, ChatActivity> progress, Action<ChatStateResponse> changed)
        {
            var session = LoadInboxSession(chatId);
            _inbox.Get(session);
            return new AgentConversationEndpoint { ViewId = viewId, Address = AgentCommunicationService.Address(session),
                Inbox = _inbox, Load = () => LoadInboxSession(chatId),
                Validate = current => { EnsureCurrentDocument(current); return CaptureExpectedRuntimeDocumentKey(current); },
                Start = current => {
                    StartConversationQueue(current, progress, changed);
                    changed?.Invoke(GetChatDetail(current.Id));
                } };
        }
    }
}
