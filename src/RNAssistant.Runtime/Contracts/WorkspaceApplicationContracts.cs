using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Runtime.Contracts
{
    public sealed class WorkspaceSubmitInputRequest
    {
        public string SessionId { get; set; }
        public string RequestId { get; set; }
        public string Message { get; set; }
        public string Mode { get; set; }
        public bool? ReasoningEnabled { get; set; }
        public WorkspaceRunAcceptance Acceptance { get; set; }
        public UserQuestionAnswerCommand Answers { get; set; }
    }

    public sealed class WorkspaceInputReceipt
    {
        public string SessionId { get; set; }
        public string RequestId { get; set; }
        public string InputId { get; set; }
        public string RunId { get; set; }
        public long AcceptedSequence { get; set; }
        public string AcceptedCursor { get; set; }
        // Delivery is user-message persistence, never run completion or effect.
        public bool DeliveryRecorded { get; set; }
    }

    public sealed class WorkspaceSessionSnapshot
    {
        public string Id { get; set; }
        public string WorkspaceId { get; set; }
        public string Title { get; set; }
        public string Model { get; set; }
        public string Mode { get; set; }
        public long Revision { get; set; }
        public string Cursor { get; set; }
        public string Status { get; set; }
        [JsonProperty("modelConfiguration")] public ModelRunMetadata ModelConfiguration { get; set; }
        [JsonProperty("contextReceipt")] public ContextReceipt ContextReceipt { get; set; }
        [JsonProperty("acceptance")] public WorkspaceRunAcceptance Acceptance { get; set; }
        [JsonProperty("userInput")] public WorkspaceUserInput UserInput { get; set; }
        [JsonProperty("taskList")] public TaskListStateProjection TaskList { get; set; }
        [JsonProperty("plan")] public WorkspacePlanState Plan { get; set; }
        [JsonProperty("interruptedToolId")] public string InterruptedToolId { get; set; }
        [JsonProperty("interruptedPath")] public string InterruptedPath { get; set; }
        [JsonProperty("interruptedEffectPossible")] public bool? InterruptedEffectPossible { get; set; }
        [JsonProperty("messageCount")] public int MessageCount { get; set; }
    }
}
