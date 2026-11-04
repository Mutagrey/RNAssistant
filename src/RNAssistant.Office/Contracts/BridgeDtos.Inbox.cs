using System.Collections.Generic;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Contracts
{
    public sealed class ActiveChatRunDto
    {
        [JsonProperty("source")] public string Source { get; set; }
        [JsonProperty("workspaceId", NullValueHandling = NullValueHandling.Ignore)] public string WorkspaceId { get; set; }
        [JsonProperty("sessionId")] public string SessionId { get; set; }
        [JsonProperty("runId")] public string RunId { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("scopeTitle")] public string ScopeTitle { get; set; }
        [JsonProperty("phase")] public string Phase { get; set; }
        [JsonProperty("startedUtc")] public System.DateTime StartedUtc { get; set; }
    }
    public sealed class ActiveChatRunsResponse
    {
        [JsonProperty("runs")] public IReadOnlyList<ActiveChatRunDto> Runs { get; set; }
    }
    public sealed class StopActiveChatRunRequest
    {
        [JsonProperty("source", Required = Required.Always)] public string Source { get; set; }
        [JsonProperty("workspaceId")] public string WorkspaceId { get; set; }
        [JsonProperty("sessionId", Required = Required.Always)] public string SessionId { get; set; }
        [JsonProperty("runId", Required = Required.Always)] public string RunId { get; set; }
    }
    public sealed class StopActiveChatRunResponse
    {
        [JsonProperty("requested")] public bool Requested { get; set; }
    }

    public sealed class ChatInboxRequest : ChatPayload
    {
        public List<string> ReceiptOperationIds { get; set; }
    }

    public sealed class ChatInputReceipt
    {
        public string Id { get; set; }
        public string OperationId { get; set; }
        public long Revision { get; set; }
        public ConversationInputStatus Status { get; set; }
    }

    public sealed class SubmitChatInputPayload : ChatPayload
    {
        public string OperationId { get; set; }
        public string Text { get; set; }
        public InputDelivery Delivery { get; set; }
        public string SteerRunId { get; set; }
        public string SteerPendingId { get; set; }
        public List<string> ResourceDraftIds { get; set; }
        public UserQuestionAnswerCommand Answers { get; set; }
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
        [JsonProperty("receipts")] public List<ChatInputReceipt> Receipts { get; set; }
        [JsonProperty("acceptedInput", NullValueHandling = NullValueHandling.Ignore)]
        public ConversationInput AcceptedInput { get; set; }
        [JsonProperty("paused")] public bool Paused { get; set; }
        [JsonProperty("pauseReason")] public string PauseReason { get; set; }
        [JsonProperty("phase")] public string Phase { get; set; }
        [JsonProperty("running")] public bool Running { get; set; }
        [JsonProperty("activeRun")] public bool ActiveRun { get; set; }
        [JsonProperty("hasPendingWait")] public bool HasPendingWait { get; set; }
    }
}
