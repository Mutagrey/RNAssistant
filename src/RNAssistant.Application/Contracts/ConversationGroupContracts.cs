using System.Collections.Generic;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Application.Contracts
{
    public sealed class ConversationGroupView
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("revision")] public string Revision { get; set; }
        [JsonProperty("members")] public IReadOnlyList<string> Members { get; set; }
    }
    public sealed class ConversationGroupScopeView
    {
        [JsonProperty("generation")] public long Generation { get; set; }
        [JsonProperty("host")] public string Host { get; set; }
        [JsonProperty("documentKey")] public string DocumentKey { get; set; }
        [JsonProperty("workspaceId")] public string WorkspaceId { get; set; }
        [JsonProperty("groups")] public IReadOnlyList<ConversationGroupView> Groups { get; set; }
        [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)] public string Error { get; set; }
    }

    public sealed class ChangeChatGroupRequest
    {
        [JsonProperty("sessionId")] public string SessionId { get; set; }
        [JsonProperty("workspaceId")] public string WorkspaceId { get; set; }
        [JsonProperty("action", Required = Required.Always)] public ChatGroupAction Action { get; set; }
        [JsonProperty("groupId")] public string GroupId { get; set; }
        [JsonProperty("revision")] public string Revision { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("members")] public string[] Members { get; set; }
        public ChatGroupChange ToChange() { return new ChatGroupChange { Action = Action, GroupId = GroupId,
            ExpectedRevision = Revision, Name = Name, Members = Members }; }
    }
    public sealed class ChangeChatGroupResponse
    {
        [JsonProperty("changed")] public bool Changed { get; set; }
        [JsonProperty("scope")] public ConversationGroupScopeView Scope { get; set; }
    }
}
