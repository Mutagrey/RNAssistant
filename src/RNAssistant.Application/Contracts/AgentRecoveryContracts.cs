using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RNAssistant.Application.Contracts
{
    // User commands. These are not model tools and confer no peer authority.
    public class AgentRecoveryScope
    {
        [JsonProperty("source")] public string Source { get; set; }
        [JsonProperty("workspaceId")] public string WorkspaceId { get; set; }
        [JsonProperty("sessionId")] public string SessionId { get; set; }
    }
    public sealed class AgentRecoveryQuery : AgentRecoveryScope
    {
        [JsonProperty("afterSessionId")] public string AfterSessionId { get; set; }
    }
    public sealed class AgentWaitResumeTarget
    {
        [JsonProperty("sessionId")] public string SessionId { get; set; }
        [JsonProperty("runId")] public string RunId { get; set; }
        [JsonProperty("revision")] public long Revision { get; set; }
    }
    public sealed class AgentRecoveryCommand : AgentRecoveryScope
    {
        [JsonProperty("targets")] public List<AgentWaitResumeTarget> Targets { get; set; }
    }
    public sealed class AgentRecoveryParticipant
    {
        [JsonProperty("sessionId")] public string SessionId { get; set; }
        [JsonProperty("agentId")] public string AgentId { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("target")] public AgentWaitResumeTarget Target { get; set; }
        [JsonProperty("error")] public string Error { get; set; }
    }
    public sealed class AgentRecoveryPage
    {
        [JsonProperty("participants")] public IReadOnlyList<AgentRecoveryParticipant> Participants { get; set; }
        [JsonProperty("nextAfterSessionId")] public string NextAfterSessionId { get; set; }
    }
    [JsonConverter(typeof(StringEnumConverter))]
    public enum AgentWaitResumeStatus { Requested, Changed, Unavailable, Unconfirmed }
    public sealed class AgentWaitResumeReceipt
    {
        [JsonProperty("sessionId")] public string SessionId { get; set; }
        [JsonProperty("runId")] public string RunId { get; set; }
        [JsonProperty("status")] public AgentWaitResumeStatus Status { get; set; }
        [JsonProperty("detail")] public string Detail { get; set; }
    }
    public sealed class AgentRecoveryResponse
    {
        [JsonProperty("receipts")] public IReadOnlyList<AgentWaitResumeReceipt> Receipts { get; set; }
    }
}
