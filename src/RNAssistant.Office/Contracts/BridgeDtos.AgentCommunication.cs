using Newtonsoft.Json;

namespace RNAssistant.Office.Contracts
{
    public sealed class AgentChatPresenceRequest
    {
        [JsonProperty("viewId")] public string ViewId { get; set; }
        [JsonProperty("source")] public string Source { get; set; }
        [JsonProperty("workspaceId")] public string WorkspaceId { get; set; }
        [JsonProperty("sessionId")] public string SessionId { get; set; }
        [JsonProperty("open")] public bool Open { get; set; }
    }
    public sealed class AgentChatPresenceResponse { [JsonProperty("registered")] public bool Registered { get; set; } }
    public sealed class AgentCommunicationRequest
    {
        [JsonProperty("sessionId")] public string SessionId { get; set; }
        [JsonProperty("chainId")] public string ChainId { get; set; }
        [JsonProperty("operationId")] public string OperationId { get; set; }
    }
    public sealed class AgentCommunicationStateResponse
    {
        [JsonProperty("enabled")] public bool Enabled { get; set; }
        [JsonProperty("chainId")] public string ChainId { get; set; }
        [JsonProperty("limit")] public int Limit { get; set; }
        [JsonProperty("sent")] public int Sent { get; set; }
        [JsonProperty("remaining")] public int Remaining { get; set; }
        [JsonProperty("limitReached")] public bool LimitReached { get; set; }
    }
}
