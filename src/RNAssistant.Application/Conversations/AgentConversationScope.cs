using System;
using System.Collections.Generic;
using RNAssistant.Core.Models;

namespace RNAssistant.Application.Conversations
{
    // A bound application port over the existing chat catalog. It owns no chat
    // list or persistence; closing a view never unregisters an execution scope.
    internal sealed class AgentConversationScope
    {
        internal string Key;
        internal Func<ChatSession, bool> Contains;
        internal Func<IReadOnlyList<AgentConversationEndpoint>> ReadCatalog;
        internal Func<ChatSession, string, ChatSession> Prepare;
        internal Action<ChatSession> Commit;
        internal Func<AgentChatAddress, AgentConversationEndpoint> Endpoint;
        internal ConversationGroupService Groups;
        internal Func<ChatSession, ConversationGroupService> ResolveGroups;
        internal ConversationCatalogService Catalog;

        internal ConversationGroupService GroupsFor(ChatSession session)
        { return ResolveGroups == null ? Groups : ResolveGroups(session); }
    }
}
