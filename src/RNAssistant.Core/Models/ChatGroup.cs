using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RNAssistant.Core.Models
{
    public sealed class ChatGroupSnapshot
    {
        public long Generation { get; set; }
        public IReadOnlyList<ChatGroup> Groups { get; set; }
    }
    // A document/workspace resource. Membership is not execution authority.
    public sealed class ChatGroup
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Revision { get; set; }
        public string CreationKey { get; set; }
        public List<string> Members { get; set; } = new List<string>();
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum ChatGroupAction { Create, Rename, Add, Remove, Delete }

    public sealed class ChatGroupChange
    {
        public ChatGroupAction Action { get; set; }
        public string GroupId { get; set; }
        public string ExpectedRevision { get; set; }
        public string Name { get; set; }
        public string[] Members { get; set; }
    }

    public sealed class ChatGroupChangeResult
    {
        public bool Changed { get; set; }
        public ChatGroup Group { get; set; }
        [JsonIgnore] public ResourceAuthorityCommit AuthorityCommit { get; set; }
    }
}
