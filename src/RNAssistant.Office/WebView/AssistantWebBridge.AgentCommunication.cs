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

        private void ConfigureAgentCommunication()
        {
            _controller.ConfigureAgentCommunication(_agentCommunication,
                (chatId, run, phase, text, activity) => ReportInboxProgress(chatId, run, phase, text, activity), ReportChatState);
        }

        private AgentCommunicationStateResponse AgentCommunicationState(AgentCommunicationRequest request, bool resume)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.SessionId) || resume &&
                (string.IsNullOrWhiteSpace(request.ChainId) || string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length > 128))
                throw new ArgumentException("Требуется точная цепочка общения.");
            var settings = new SettingsService(_controller.StoragePaths);
            if (request.Source == "workspace")
            {
                if (Plugins?.Conversations == null || string.IsNullOrWhiteSpace(request.WorkspaceId))
                    throw new ArgumentException("Требуется доступный workspace.");
                var workspace = new WorkspaceStore(_controller.StoragePaths).OpenConnected(request.WorkspaceId);
                Plugins.Conversations.RegisterAgentScope(workspace, settings.Load(), settings.LoadApiKey);
            }
            else if (request.Source != "office" || request.WorkspaceId != null)
                throw new ArgumentException("Требуется точная область чата.");
            var limit = settings.Load().MaxAgentMessages;
            var state = resume ? _agentCommunication.Continue(request.SessionId, request.ChainId, request.OperationId, limit, request.Source, request.WorkspaceId) :
                _agentCommunication.State(request.SessionId, limit, request.Source, request.WorkspaceId);
            return new AgentCommunicationStateResponse { Enabled = state.Enabled, ChainId = state.ChainId, Limit = state.Limit,
                Sent = state.Sent, Remaining = state.Remaining, LimitReached = state.LimitReached };
        }
    }
}
