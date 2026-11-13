using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RNAssistant.Core.Models
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum AgentAssignmentState { Open, DeliveryUnknown, Removed, ResolutionPrepared, ResolutionReceived }

    public sealed class AgentAssignmentView
    {
        [JsonProperty("request_id")] public string RequestId { get; set; }
        [JsonProperty("agent_id")] public string AgentId { get; set; }
        public string Summary { get; set; }
        public static string Excerpt(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length <= 160) return text;
            return text.Substring(0, char.IsHighSurrogate(text[159]) ? 159 : 160) + "…";
        }
        public AgentAssignmentState State { get; set; }
        [JsonProperty("reported_outcome", NullValueHandling = NullValueHandling.Ignore)] public AgentAssignmentOutcome? ReportedOutcome { get; set; }
        [JsonProperty("resolution_message_id", NullValueHandling = NullValueHandling.Ignore)] public string ResolutionMessageId { get; set; }
        [JsonProperty("input_status", NullValueHandling = NullValueHandling.Ignore)] public ConversationInputStatus? InputStatus { get; set; }
    }

    public sealed class AgentAssignmentPage
    {
        public string Direction { get; set; }
        public IReadOnlyList<AgentAssignmentView> Items { get; set; }
        public bool Truncated { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string NextRequest { get; set; }
    }

}
