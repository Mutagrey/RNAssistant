using System.Collections.Generic;
using System.Linq;
using RNAssistant.Application.Contracts;
using RNAssistant.Application.Conversations;
using RNAssistant.Core.Models;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        private IReadOnlyList<ConversationGroupScopeView> ChatGroupCatalog(IEnumerable<ChatSessionSummary> summaries)
        {
            return new ConversationGroupService(_chatStore).Catalog((summaries ?? new ChatSessionSummary[0])
                .Where(item => item != null).Select(item => new ChatSessionHeader { Host = item.Host, DocumentKey = item.DocumentKey,
                    WorkspaceId = item.WorkspaceId, DocumentAuthorityId = GroupAuthority(item.Host, item.DocumentKey, item.DocumentPath, item.DocumentAuthorityId) }));
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
            return new ConversationGroupService(_chatStore).Change(scope, request);
        }
    }
}
