using System.Collections.Generic;
using System.Linq;
using RNAssistant.Application.Contracts;
using RNAssistant.Application.Conversations;
using RNAssistant.Core.Models;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        private ConversationGroupService GroupService(ChatSession scope)
        {
            return new ConversationGroupService(_conversationStore, DocumentArtifactStore.PlanScope(scope),
                _resourceAuthorityStore, _resourceAuthorityStore, _resourceAuthorityStore, _payloads,
                (session, context) => _conversationRouter.MutationOrigin(session, context));
        }

        private IReadOnlyList<ConversationGroupScopeView> ChatGroupCatalog(IEnumerable<ChatSessionSummary> summaries)
        {
            return (summaries ?? new ChatSessionSummary[0]).Where(item => item != null)
                .Select(item => new ChatSessionHeader { Host = item.Host, DocumentKey = item.DocumentKey,
                    WorkspaceId = item.WorkspaceId, DocumentAuthorityId = GroupAuthority(item.Host, item.DocumentKey,
                        item.DocumentPath, item.DocumentAuthorityId) })
                .GroupBy(item => new { item.Host, item.WorkspaceId, item.DocumentAuthorityId })
                .SelectMany(group => {
                    var header = group.First();
                    var scope = new ChatSession { Host = header.Host, DocumentKey = header.DocumentKey,
                        WorkspaceId = header.WorkspaceId, DocumentAuthorityId = header.DocumentAuthorityId };
                    return GroupService(scope).Catalog(group);
                }).ToArray();
        }
        private string GroupAuthority(string host, string key, string path, string authority)
        {
            if (string.IsNullOrEmpty(authority) && host == _adapter.HostName && key == _adapter.DocumentKey)
                return _documentAuthorityRegistry.Resolve(host, _adapter.RuntimeDocumentKey, path).Id;
            return authority;
        }
        public ChangeChatGroupResponse ChangeChatGroup(ChangeChatGroupRequest request)
        {
            var session = LoadAddressedSession(request.SessionId);
            var scope = new ChatSession { Id = session.Id, Host = session.Host, DocumentKey = session.DocumentKey,
                WorkspaceId = session.WorkspaceId, DocumentAuthorityId = GroupAuthority(session.Host, session.DocumentKey,
                    session.DocumentPath, session.DocumentAuthorityId) };
            return GroupService(scope).Change(scope, request);
        }
    }
}
