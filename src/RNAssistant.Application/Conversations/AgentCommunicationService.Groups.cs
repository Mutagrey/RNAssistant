using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Application.Contracts;

namespace RNAssistant.Application.Conversations
{
    internal sealed class AgentGroupRequest
    {
        [JsonProperty("action")] public string Action { get; set; }
        [JsonProperty("group")] public string Group { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("agent_ids")] public string[] AgentIds { get; set; }
        [JsonProperty("after")] public string After { get; set; }
    }
    internal sealed class AgentGroupView
    {
        public string Name { get; set; }
        public int MemberCount { get; set; }
        public string[] Members { get; set; }
        public bool Truncated { get; set; }
    }
    internal sealed class AgentGroupResult
    {
        public bool Changed { get; set; }
        public IReadOnlyList<AgentGroupView> Groups { get; set; }
        public string NextGroup { get; set; }
    }
    internal sealed partial class AgentCommunicationService
    {
        internal AgentGroupResult Group(ChatSession source, int limit, AgentGroupRequest request, string operationId, Action dispatch)
        {
            lock (_sync)
            {
                RequireEnabled(limit);
                var domain = Scope(source).Groups;
                if (domain == null) throw new AgentCommunicationException("agent_groups_unavailable", "Группы этой области недоступны.");
                var groups = domain.Read(source);
                var named = string.IsNullOrEmpty(request.Group) ? null : groups.SingleOrDefault(item =>
                    string.Equals(item.Name, request.Group, StringComparison.OrdinalIgnoreCase));
                if (request.Group != null && named == null) throw new AgentCommunicationException("agent_group_missing", "Группа не найдена; перечитайте список.");
                if (request.Action == "list")
                {
                    if (request.Name != null || request.AgentIds != null) throw new ArgumentException("list не изменяет группы.");
                    var page = (named == null ? groups : new[] { named }).Where(item => request.After == null ||
                        string.Compare(item.Name, request.After, StringComparison.OrdinalIgnoreCase) > 0)
                        .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).Take(51).ToArray();
                    return new AgentGroupResult { Groups = page.Take(50).Select(ModelGroup).ToArray(),
                        NextGroup = page.Length > 50 ? page[49].Name : null };
                }
                ChatGroupAction action;
                if (request.After != null || !Enum.TryParse(request.Action, true, out action) || !Enum.IsDefined(typeof(ChatGroupAction), action))
                    throw new ArgumentException("Неизвестное действие группы.");
                if (action == ChatGroupAction.Create && request.Group != null || action != ChatGroupAction.Create && named == null)
                    throw new ArgumentException("create требует name; остальные действия — существующую group.");
                var membership = action == ChatGroupAction.Add || action == ChatGroupAction.Remove;
                if (!membership && request.AgentIds != null || action != ChatGroupAction.Create && action != ChatGroupAction.Rename && request.Name != null)
                    throw new ArgumentException("Лишние параметры действия группы.");
                var ids = (request.AgentIds ?? new string[0]).Select(id => {
                    var endpoint = FindAgent(source, id);
                    if (endpoint == null) throw new AgentCommunicationException("agent_target_unavailable", "Участник отсутствует в каталоге этой области.");
                    return endpoint.Address.ChatId;
                }).ToArray();
                var result = domain.Change(source, new ChatGroupChange { Action = action, GroupId = named?.Id,
                    ExpectedRevision = named?.Revision, Name = request.Name, Members = ids }, operationId, dispatch);
                var current = result.Group == null ? null : domain.Read(source).SingleOrDefault(item => item.Id == result.Group.Id);
                return new AgentGroupResult { Changed = result.Changed, Groups = current == null ? new AgentGroupView[0] : new[] { ModelGroup(current) } };
            }
        }

        private static AgentGroupView ModelGroup(ConversationGroupView group)
        { return new AgentGroupView { Name = group.Name, MemberCount = group.Members.Count,
            Members = group.Members.Take(50).Select(AgentChatAddress.IdFor).ToArray(), Truncated = group.Members.Count > 50 }; }

        private HashSet<string> GroupMembers(ChatSession source, string name)
        {
            lock (_sync)
            {
                var group = Scope(source).Groups?.Read(source).SingleOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
                if (group == null) throw new AgentCommunicationException("agent_group_missing", "Группа не найдена.");
                return new HashSet<string>(group.Members.Select(AgentChatAddress.IdFor), StringComparer.Ordinal);
            }
        }
    }
}
