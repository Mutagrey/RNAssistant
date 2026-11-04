using System;
using System.Linq;
using RNAssistant.Application.Conversations;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Persistence;

using RNAssistant.Core.Storage;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Runtime
{
    public sealed partial class WorkspaceConversationService
    {
        internal AgentCommunicationService AgentCommunication { get; set; } = new AgentCommunicationService();
        internal Action<string, string, WorkspaceRunEvent> AgentProgress { get; set; }

        internal void RegisterAgentScope(WorkspaceDescriptor workspace, AppSettings settings, Func<string> key)
        {
            var captured = settings.Clone();
            AgentCommunication.Register(new AgentConversationScope {
                Key = "workspace:" + workspace.WorkspaceId,
                Contains = session => session?.WorkspaceId == workspace.WorkspaceId,
                ReadCatalog = () => _chats.ListHeaders(includeStorageUsage: false)
                    .Where(header => header.WorkspaceId == workspace.WorkspaceId)
                    .Select(header => AgentEndpoint(workspace, new AgentChatAddress { Source = "workspace", ChatId = header.Id,
                        WorkspaceId = workspace.WorkspaceId, Title = header.Title, ScopeTitle = header.DocumentTitle }, captured, key)).ToArray(),
                Prepare = (source, title) => {
                    _workspaces.OpenConnected(workspace.WorkspaceId);
                    var child = _chats.CreateTransient("Workspace", workspace.WorkspaceId, workspace.Name, title);
                    child.WorkspaceId = workspace.WorkspaceId;
                    return child;
                },
                Commit = child => _chats.Commit(child, new ConversationCommit()),
                Endpoint = child => AgentEndpoint(workspace, AgentCommunicationService.Address(child), captured, key)
            });
        }

        private AgentConversationEndpoint AgentEndpoint(WorkspaceDescriptor workspace, AgentChatAddress address,
            AppSettings settings, Func<string> key)
        {
            var workspaceId = workspace.WorkspaceId;
            return new AgentConversationEndpoint { Address = address,
                Inbox = _conversationInbox, Load = () => GetSession(_workspaces.OpenConnected(workspaceId), address.ChatId),
                ReserveForResume = () => ReserveRun(address.ChatId, false),
                IsClosing = () => _conversationApplication.IsClosing || _titleLifetime.IsCancellationRequested,
                Validate = current => {
                    if (current == null || current.WorkspaceId != workspaceId) throw new InvalidOperationException("Исходный проект недоступен.");
                    ConversationRunPolicy.RequireCurrentSession(current);
                    return workspaceId;
                },
                Start = current => {
                    Action<WorkspaceRunEvent> progress = value => AgentProgress?.Invoke(workspaceId, current.Id, value);
                    StartQueuedInputs(workspace, current.Id, settings, key, progress, null);
                    progress(new WorkspaceRunEvent("QueueChanged", current.LastRun?.KernelState?.Summary,
                        null, ConversationEventFeed.Cursor(current)));
                } };
        }
    }
}
