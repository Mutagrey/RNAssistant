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

        private AgentRecoveryPage RecoverableAgentWaits(AgentRecoveryQuery request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            PrepareAgentScope(request.Source, request.WorkspaceId);
            return _agentCommunication.ListRecoverableWaits(request);
        }

        private AgentRecoveryResponse ResumeAgentWaits(AgentRecoveryCommand request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            PrepareAgentScope(request.Source, request.WorkspaceId);
            return _agentCommunication.ResumeSelectedWaits(request);
        }

        private void PrepareAgentScope(string source, string workspaceId)
        {
            if (source == "workspace")
            {
                if (Plugins?.Conversations == null || string.IsNullOrWhiteSpace(workspaceId))
                    throw new ArgumentException("Требуется доступный workspace.");
                var settings = new SettingsService(_controller.StoragePaths);
                var workspace = new WorkspaceStore(_controller.StoragePaths).OpenConnected(workspaceId);
                Plugins.Conversations.RegisterAgentScope(workspace, settings.Load(), settings.LoadApiKey);
            }
            else if (source != "office" || workspaceId != null)
                throw new ArgumentException("Требуется точная область чата.");
        }

        private AgentCommunicationStateResponse AgentCommunicationState(AgentCommunicationRequest request, bool resume)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.SessionId) || resume &&
                (string.IsNullOrWhiteSpace(request.ChainId) || string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length > 128))
                throw new ArgumentException("Требуется точная цепочка общения.");
            var settings = new SettingsService(_controller.StoragePaths);
            PrepareAgentScope(request.Source, request.WorkspaceId);
            var limit = settings.Load().MaxAgentMessages;
            var state = resume ? _agentCommunication.Continue(request.SessionId, request.ChainId, request.OperationId, limit, request.Source, request.WorkspaceId) :
                _agentCommunication.State(request.SessionId, limit, request.Source, request.WorkspaceId);
            return new AgentCommunicationStateResponse { Enabled = state.Enabled, ChainId = state.ChainId, Limit = state.Limit,
                Sent = state.Sent, Remaining = state.Remaining, LimitReached = state.LimitReached };
        }
    }
}
