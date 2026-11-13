using System;
using System.Linq;
using RNAssistant.Application.Conversations;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        internal void ConfigureAgentCommunication(AgentCommunicationService service,
            Action<string, string, string, string, ChatActivity> progress = null, Action<ChatStateResponse> changed = null)
        {
            _toolExecutor.AgentCommunication = service; _toolExecutor.AgentInbox = _inbox;
            service.Register(new AgentConversationScope {
                Key = "office",
                Groups = new ConversationGroupService(_chatStore),
                Catalog = new ConversationCatalogService(_chatStore, _toolExecutor.Payloads),
                Contains = session => session?.WorkspaceId == null && _chatSessions.IsCurrentDocument(session),
                ReadCatalog = () => {
                    var host = _adapter.HostName;
                    var documentKey = _adapter.DocumentKey;
                    return _chatStore.ListHeaders(includeStorageUsage: false)
                        .Where(header => header.WorkspaceId == null &&
                            string.Equals(header.Host, host, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(header.DocumentKey, documentKey, StringComparison.OrdinalIgnoreCase))
                        .Select(header => AgentEndpoint(new AgentChatAddress { Source = "office", ChatId = header.Id,
                            Title = header.Title, ScopeTitle = header.DocumentTitle }, progress, changed)).ToArray();
                },
                Prepare = (source, title) => {
                    EnsureCurrentDocument(source);
                    return _chatSessions.PrepareAgentChat(source, title);
                },
                Commit = session => _conversationStore.Commit(session, new ConversationCommit()),
                Endpoint = address => AgentEndpoint(address, progress, changed)
            });
        }

        private AgentConversationEndpoint AgentEndpoint(AgentChatAddress address,
            Action<string, string, string, string, ChatActivity> progress, Action<ChatStateResponse> changed)
        {
            Action<ChatSession> notify = current => changed?.Invoke(GetChatDetail(current.Id));
            return new AgentConversationEndpoint { Address = address,
                Runs = _activeRuns, Store = _conversationStore, Payloads = _toolExecutor.Payloads,
                Changed = notify,
                Inbox = _inbox, Load = () => LoadInboxSession(address.ChatId),
                Reserve = () => _conversationLeases.Reserve(address.ChatId),
                IsClosing = () => _conversationApplication.IsClosing || _lifetimeCancellation.IsCancellationRequested,
                ValidateResume = current => OfficeDocumentExecutionGuardState.ThrowIfMismatch(_adapter,
                    new OfficeDocumentExecutionExpectation { Host = current.Host, DocumentKey = current.DocumentKey,
                        RuntimeDocumentKey = current.LastRun.DocumentRuntimeKey }),
                Validate = current => { EnsureCurrentDocument(current); return CaptureExpectedRuntimeDocumentKey(current); },
                Start = current => {
                    StartConversationQueue(current, (run, phase, text, activity) => progress?.Invoke(current.Id, run, phase, text, activity), changed);
                    notify(current);
                } };
        }
    }
}
