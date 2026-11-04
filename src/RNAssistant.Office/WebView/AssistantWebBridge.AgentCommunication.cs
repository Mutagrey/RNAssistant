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

        private AgentWaitCancelReceipt CancelAgentWait(AgentWaitCancelCommand request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            PrepareAgentScope(request.Source, request.WorkspaceId);
            return _agentCommunication.CancelSavedWait(request);
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

    }
}
