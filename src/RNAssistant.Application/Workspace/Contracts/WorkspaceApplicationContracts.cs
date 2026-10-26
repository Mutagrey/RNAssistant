using System.Collections.Generic;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Agent;

namespace RNAssistant.Runtime.Contracts
{
    public sealed class WorkspaceSubmitInputRequest
    {
        public string SessionId { get; set; }
        public string RequestId { get; set; }
        public string Message { get; set; }
        public string EditMessageId { get; set; }
        public long? ExpectedRevision { get; set; }
        [JsonProperty("resourceDraftIds", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> ResourceDraftIds { get; set; }
        public string Mode { get; set; }
        public MutationPolicy? MutationPolicy { get; set; }
        public bool? AutoConfirmToolActions { get; set; }
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
        public bool TitleGenerationPending { get; set; }
        [JsonProperty("readOnly")] public bool ReadOnly { get; set; }
        public string Model { get; set; }
        public string Mode { get; set; }
        public MutationPolicy? MutationPolicy { get; set; }
        public ConversationRunPolicy ConversationPolicy { get; set; }
        public long Revision { get; set; }
        public string Cursor { get; set; }
        public string Status { get; set; }
        [JsonProperty("runViewState", NullValueHandling = NullValueHandling.Ignore)]
        public RunViewState RunViewState { get; set; }
        public string RunId { get; set; }
        public int? IterationsUsed { get; set; }
        public int? ToolStepsUsed { get; set; }
        public AgentRunLimits Limits { get; set; }
        public string ContextCheckpointId { get; set; }
        [JsonProperty("modelConfiguration")] public ModelRunMetadata ModelConfiguration { get; set; }
        [JsonProperty("office")] public WorkspaceOfficeContext Office { get; set; }
        [JsonProperty("contextReceipt")] public ContextReceipt ContextReceipt { get; set; }
        [JsonProperty("contextUsage")] public WorkspaceContextUsage ContextUsage { get; set; }
        [JsonProperty("acceptance")] public WorkspaceRunAcceptance Acceptance { get; set; }
        [JsonProperty("userInput")] public WorkspaceUserInput UserInput { get; set; }
        [JsonProperty("taskList")] public TaskListStateProjection TaskList { get; set; }
        [JsonProperty("plan")] public WorkspacePlanState Plan { get; set; }
        [JsonProperty("interruptedToolId")] public string InterruptedToolId { get; set; }
        [JsonProperty("interruptedPath")] public string InterruptedPath { get; set; }
        [JsonProperty("interruptedEffectPossible")] public bool? InterruptedEffectPossible { get; set; }
        [JsonProperty("messageCount")] public int MessageCount { get; set; }
        public WorkspaceRunPresentation Presentation { get; set; }
        [JsonProperty("modelProgress", NullValueHandling = NullValueHandling.Ignore)] public WorkspaceModelProgress ModelProgress { get; set; }
        [JsonProperty("modelProgressUnavailable", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool ModelProgressUnavailable { get; set; }
    }

    public sealed class WorkspaceContextUsage
    {
        public string MessageId { get; set; }
        public string RunId { get; set; }
        public string StepId { get; set; }
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
        public int? TotalTokens { get; set; }
        public int? ContextWindowTokens { get; set; }
    }

    // Transient preview, scoped to one live attempt. Never durable history or authority.
    public sealed class WorkspaceModelProgress
    {
        public long Sequence { get; set; }
        public string RunId { get; set; }
        public string StepId { get; set; }
        public string AttemptId { get; set; }
        public string Message { get; set; }
        public string Reasoning { get; set; }
        public bool ReasoningTruncated { get; set; }
        public bool Retrying { get; set; }
    }

    public sealed class WorkspaceOfficeContext
    {
        public string Host { get; set; }
        public string DocumentTitle { get; set; }
    }

    public sealed class WorkspaceRunControlRequest
    {
        public string SessionId { get; set; }
        public string RunId { get; set; }
        public string Cursor { get; set; }
    }

    [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public enum WorkspaceCancellationStatus { Requested, AlreadyStopped, StaleState, Unconfirmed }

    public sealed class WorkspaceCancellationReceipt
    {
        public string SessionId { get; set; }
        public string RunId { get; set; }
        public WorkspaceCancellationStatus Status { get; set; }
    }
}
