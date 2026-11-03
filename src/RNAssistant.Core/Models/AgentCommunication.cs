using Newtonsoft.Json;

namespace RNAssistant.Core.Models
{
    // Runtime-authored provenance. A chat address is public; document authority is not.
    public sealed class AgentChatAddress
    {
        public string Source { get; set; }
        public string WorkspaceId { get; set; }
        public string ChatId { get; set; }
        public string Title { get; set; }
        public string ScopeTitle { get; set; }
    }

    public sealed class AgentCommunicationContext
    {
        public string ChainId { get; set; }
        public int Limit { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public AgentChatAddress Sender { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string SenderRunId { get; set; }
    }
}
