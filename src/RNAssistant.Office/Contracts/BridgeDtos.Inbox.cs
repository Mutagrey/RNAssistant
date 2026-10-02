using System.Collections.Generic;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Contracts
{
    public sealed class SubmitChatInputPayload : ChatPayload
    {
        public string OperationId { get; set; }
        public string Text { get; set; }
        public InputDelivery Delivery { get; set; }
        public List<string> ResourceDraftIds { get; set; }
    }
    public sealed class UpdateChatInputPayload : ChatPayload
    {
        public string InputId { get; set; }
        public long ExpectedRevision { get; set; }
        public string Text { get; set; }
    }
    public sealed class ChatInboxResponse
    {
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("epoch")] public string Epoch { get; set; }
        [JsonProperty("revision")] public long Revision { get; set; }
        [JsonProperty("items")] public List<ConversationInput> Items { get; set; }
        [JsonProperty("paused")] public bool Paused { get; set; }
        [JsonProperty("pauseReason")] public string PauseReason { get; set; }
        [JsonProperty("phase")] public string Phase { get; set; }
        [JsonProperty("running")] public bool Running { get; set; }
    }
}
