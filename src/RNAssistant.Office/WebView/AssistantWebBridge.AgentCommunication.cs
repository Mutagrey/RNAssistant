using System;
using RNAssistant.Application.Conversations;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.WebView
{
    public sealed partial class AssistantWebBridge
    {
        private readonly AgentCommunicationService _agentCommunication;

        private AgentChatPresenceResponse SetAgentChatPresence(AgentChatPresenceRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ViewId) || request.ViewId.Length > 128)
                throw new ArgumentException("Требуется экземпляр вкладки чата.");
            if (!request.Open) { _agentCommunication.Close(request.ViewId); return new AgentChatPresenceResponse(); }
            if (string.IsNullOrWhiteSpace(request.SessionId) || request.Source != "office" && request.Source != "workspace" ||
                request.Source == "office" && request.WorkspaceId != null ||
                request.Source == "workspace" && string.IsNullOrWhiteSpace(request.WorkspaceId))
                throw new ArgumentException("Требуется точный адрес чата.");
            AgentConversationEndpoint endpoint;
            if (request.Source == "office")
                endpoint = _controller.AgentEndpoint(request.SessionId, request.ViewId,
                    (run, phase, text, activity) => ReportInboxProgress(request.SessionId, run, phase, text, activity),
                    ReportChatState);
            else
            {
                if (Plugins?.Conversations == null) throw new InvalidOperationException("Workspace runtime недоступен.");
                Plugins.Conversations.AgentCommunication = _agentCommunication;
                var settings = new SettingsService(_controller.StoragePaths);
                var target = new ProjectChatRequest { WorkspaceId = request.WorkspaceId, SessionId = request.SessionId };
                endpoint = Plugins.Conversations.AgentEndpoint(request.WorkspaceId, request.SessionId, request.ViewId,
                    settings.Load, settings.LoadApiKey, progress => ReportWorkspaceProgress("agents:" + request.SessionId, target, progress));
            }
            _agentCommunication.Open(endpoint);
            return new AgentChatPresenceResponse { Registered = true };
        }

        private AgentCommunicationStateResponse AgentCommunicationState(AgentCommunicationRequest request, bool resume)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.SessionId) || resume &&
                (string.IsNullOrWhiteSpace(request.ChainId) || string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length > 128))
                throw new ArgumentException("Требуется точная цепочка общения.");
            var limit = new SettingsService(_controller.StoragePaths).Load().MaxAgentMessages;
            var state = resume ? _agentCommunication.Continue(request.SessionId, request.ChainId, request.OperationId, limit) :
                _agentCommunication.State(request.SessionId, limit);
            return new AgentCommunicationStateResponse { Enabled = state.Enabled, ChainId = state.ChainId, Limit = state.Limit,
                Sent = state.Sent, Remaining = state.Remaining, LimitReached = state.LimitReached };
        }
    }
}
