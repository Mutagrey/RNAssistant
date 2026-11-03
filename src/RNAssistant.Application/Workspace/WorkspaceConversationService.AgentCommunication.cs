using System;
using RNAssistant.Application.Conversations;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

using RNAssistant.Core.Storage;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Runtime
{
    public sealed partial class WorkspaceConversationService
    {
        internal AgentCommunicationService AgentCommunication { get; set; }

        internal AgentConversationEndpoint AgentEndpoint(string workspaceId, string chatId, string viewId,
            Func<AppSettings> settings, Func<string> key, Action<WorkspaceRunEvent> progress)
        {
            var workspace = _workspaces.OpenConnected(workspaceId);
            var session = GetSession(workspace, chatId);
            if (session == null) throw new ArgumentException("Чат проекта не найден.");
            _conversationInbox.Get(session);
            return new AgentConversationEndpoint { ViewId = viewId, Address = AgentCommunicationService.Address(session),
                Inbox = _conversationInbox, Load = () => GetSession(_workspaces.OpenConnected(workspaceId), chatId),
                Validate = current => {
                    if (current == null || current.WorkspaceId != workspaceId) throw new InvalidOperationException("Исходный проект недоступен.");
                    ConversationRunPolicy.RequireCurrentSession(current);
                    return workspaceId;
                },
                Start = current => {
                    StartQueuedInputs(workspace, current.Id, settings(), key, progress, null);
                    progress?.Invoke(new WorkspaceRunEvent("QueueChanged", current.LastRun?.KernelState?.Summary,
                        null, ConversationEventFeed.Cursor(current)));
                } };
        }
    }
}
