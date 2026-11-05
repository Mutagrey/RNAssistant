using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Storage;

namespace RNAssistant.Application.Conversations
{
    // Common metadata owner. It neither wakes participants nor changes their contexts.
    public sealed class ConversationGroupService
    {
        private readonly ChatStore _store;
        public ConversationGroupService(ChatStore store) { _store = store ?? throw new ArgumentNullException(nameof(store)); }
        public IReadOnlyList<ConversationGroupView> Read(ChatSession scope)
        { return _store.ReadChatGroups(scope).Select(View).ToArray(); }
        private static ConversationGroupView View(ChatGroup group)
        { return new ConversationGroupView { Id = group.Id, Name = group.Name, Revision = group.Revision, Members = group.Members.ToArray() }; }

        public ConversationGroupScopeView Snapshot(ChatSession scope)
        {
            var snapshot = _store.CaptureChatGroups(scope);
            return new ConversationGroupScopeView { Host = scope.Host, DocumentKey = scope.DocumentKey,
                WorkspaceId = scope.WorkspaceId, Generation = snapshot.Generation, Groups = snapshot.Groups.Select(View).ToArray() };
        }

        public ChangeChatGroupResponse Change(ChatSession scope, ChangeChatGroupRequest request)
        {
            if (request == null || request.SessionId != scope?.Id || request.WorkspaceId != scope.WorkspaceId)
                throw new InvalidOperationException("Область команды группы изменилась.");
            var result = Change(scope, request.ToChange(), "group_ui_" + Guid.NewGuid().ToString("N"));
            return new ChangeChatGroupResponse { Changed = result.Changed, Scope = Snapshot(scope) };
        }

        public IReadOnlyList<ConversationGroupScopeView> Catalog(IEnumerable<ChatSessionHeader> headers)
        {
            return (headers ?? new ChatSessionHeader[0]).GroupBy(header => new { header.Host, header.DocumentKey, header.WorkspaceId })
                .Select(entries => {
                    var header = entries.First();
                    var result = new ConversationGroupScopeView { Host = header.Host, DocumentKey = header.DocumentKey, WorkspaceId = header.WorkspaceId };
                    try { result = Snapshot(new ChatSession { Host = header.Host, DocumentKey = header.DocumentKey,
                        WorkspaceId = header.WorkspaceId, DocumentAuthorityId = header.DocumentAuthorityId }); }
                    catch (Exception error) when (error is InvalidOperationException || error is System.IO.IOException || error is JsonException || error is ArgumentException)
                    { result.Error = error.Message; result.Groups = new ConversationGroupView[0]; }
                    return result;
                }).ToArray();
        }

        public ChatGroupChangeResult Change(ChatSession scope, ChatGroupChange command, string operationId, Action dispatch = null)
        { return _store.ChangeChatGroup(scope, command, operationId, dispatch); }
    }
}
