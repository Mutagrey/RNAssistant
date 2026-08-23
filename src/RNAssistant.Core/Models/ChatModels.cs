using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace RNAssistant.Core.Models
{
    public static class ChatModes
    {
        public const string Chat = "chat";
        public const string Agent = "agent";

        public static string Normalize(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            return value == Chat ? Chat : Agent;
        }
    }

    public sealed class ChatMessage
    {
        public string Id { get; set; }
        public string Role { get; set; }
        public string Content { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool ExcludeFromModelContext { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool ProtocolMessage { get; set; }
        public string ToolCallId { get; set; }
        public string ToolName { get; set; }
        public string ToolResultRole { get; set; }
        public List<RNAssistant.Core.Llm.LlmToolCall> ToolCalls { get; set; }
        public List<ChatAttachment> Attachments { get; set; }
        public List<string> ArtifactIds { get; set; }
        public string HtmlWorkspaceCheckpointId { get; set; }
        public ChatActivity Activity { get; set; }
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
        public int? TotalTokens { get; set; }
        public string UsageJson { get; set; }
        public string ReasoningContent { get; set; }
        public int? ReasoningTokens { get; set; }
        public bool ReasoningTruncated { get; set; }
        public string RunId { get; set; }
        public int? Sequence { get; set; }
        public DateTime CreatedUtc { get; set; }

        public ChatMessage()
        {
            Id = Guid.NewGuid().ToString("N");
            CreatedUtc = DateTime.UtcNow;
            Attachments = new List<ChatAttachment>();
            ToolCalls = new List<RNAssistant.Core.Llm.LlmToolCall>();
            ArtifactIds = new List<string>();
        }
    }

    public sealed class ChatAttachment
    {
        public string Id { get; set; }
        public string FileName { get; set; }
        public string ContentType { get; set; }
        public long Size { get; set; }
        public string Kind { get; set; }
        public string RelativePath { get; set; }
        public string ExtractedText { get; set; }
        public string ExtractedTextPath { get; set; }
        public int ExtractedCharCount { get; set; }
        public bool TextTruncated { get; set; }
        public int PageCount { get; set; }
        public List<int> PageTextLengths { get; set; }
        public string ExtractionWarning { get; set; }
        public string Status { get; set; }
        public string Error { get; set; }
        public DateTime CreatedUtc { get; set; }

        public ChatAttachment()
        {
            Id = Guid.NewGuid().ToString("N");
            CreatedUtc = DateTime.UtcNow;
            Status = "ready";
            PageTextLengths = new List<int>();
        }
    }

    public sealed class ChatActivity
    {
        public string RunId { get; set; }
        public int? Sequence { get; set; }
        public string StepId { get; set; }
        public string StepMessage { get; set; }
        public string Kind { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string Status { get; set; }
        public string ExecutionStatus { get; set; }
        public string ErrorCode { get; set; }
        public bool? Retryable { get; set; }
        public string PendingId { get; set; }
        public string ToolId { get; set; }
        public string ToolCallId { get; set; }
        public string ArgumentsJson { get; set; }
        public string ResultMessage { get; set; }
        public string DataJson { get; set; }
        public List<ChatActivity> Children { get; set; }

        public ChatActivity()
        {
            Children = new List<ChatActivity>();
        }
    }

    public sealed class ChatSession
    {
        public const int CurrentFormatVersion = 2;

        [JsonProperty(Required = Required.Always)]
        public int FormatVersion { get; set; }
        public string Id { get; set; }
        public string Host { get; set; }
        public string DocumentKey { get; set; }
        public string DocumentTitle { get; set; }
        public string DocumentPath { get; set; }
        public string Title { get; set; }
        public string Model { get; set; }
        public string Mode { get; set; }
        public bool HtmlModeEnabled { get; set; }
        public bool ReasoningEnabled { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public DocumentContext Context { get; set; }
        public ChatRunRecord LastRun { get; set; }
        public HtmlWorkspace HtmlWorkspace { get; set; }
        public List<ChatMessage> Messages { get; set; }
        public List<ContextCheckpoint> ContextCheckpoints { get; set; }
        public string ActiveContextCheckpointId { get; set; }
        public List<ChatArtifact> Artifacts { get; set; }
        public string ActiveHtmlArtifactId { get; set; }
        public string ActivePlanArtifactId { get; set; }

        public ChatSession()
        {
            FormatVersion = CurrentFormatVersion;
            Id = Guid.NewGuid().ToString("N");
            CreatedUtc = DateTime.UtcNow;
            UpdatedUtc = DateTime.UtcNow;
            Context = new DocumentContext();
            HtmlWorkspace = new HtmlWorkspace();
            Messages = new List<ChatMessage>();
            ContextCheckpoints = new List<ContextCheckpoint>();
            Artifacts = new List<ChatArtifact>();
            Mode = ChatModes.Agent;
        }
    }

    public sealed class ContextCheckpoint
    {
        public const string CurrentPromptVersion = "context-compaction-v1";

        public string Id { get; set; }
        public string ThroughMessageId { get; set; }
        public string SummaryJson { get; set; }
        public string SummaryMarkdown { get; set; }
        public string Model { get; set; }
        public string PromptVersion { get; set; }
        public int SourceMessageCount { get; set; }
        public int SourceTokens { get; set; }
        public int SummaryTokens { get; set; }
        public DateTime CreatedUtc { get; set; }

        public ContextCheckpoint()
        {
            Id = Guid.NewGuid().ToString("N");
            PromptVersion = CurrentPromptVersion;
            CreatedUtc = DateTime.UtcNow;
        }
    }

    public sealed class ChatRunRecord
    {
        public string RunId { get; set; }
        public string RuntimeId { get; set; }
        public string Status { get; set; }
        public string Phase { get; set; }
        public string CurrentAction { get; set; }
        public DateTime StartedUtc { get; set; }
    }

    public sealed class ChatSessionHeader
    {
        public string Id { get; set; }
        public string Host { get; set; }
        public string DocumentKey { get; set; }
        public string DocumentTitle { get; set; }
        public string DocumentPath { get; set; }
        public string Title { get; set; }
        public string Model { get; set; }
        public string Mode { get; set; }
        public bool HtmlModeEnabled { get; set; }
        public bool ReasoningEnabled { get; set; }
        public bool HasHtmlWorkspace { get; set; }
        public int HtmlFileCount { get; set; }
        public int HtmlDataSourceCount { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public int MessageCount { get; set; }
        public string RunId { get; set; }
        public string RunRuntimeId { get; set; }
        public string RunStatus { get; set; }
        public string RunPhase { get; set; }
        public DateTime? RunStartedUtc { get; set; }
    }

    public sealed class ChatSessionSummary
    {
        public string Id { get; set; }
        public string Host { get; set; }
        public string DocumentKey { get; set; }
        public string DocumentTitle { get; set; }
        public string DocumentPath { get; set; }
        public string Title { get; set; }
        public string Model { get; set; }
        public string Mode { get; set; }
        public bool HtmlModeEnabled { get; set; }
        public bool ReasoningEnabled { get; set; }
        public bool HasHtmlWorkspace { get; set; }
        public int HtmlFileCount { get; set; }
        public int HtmlDataSourceCount { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public int MessageCount { get; set; }
        public bool IsCurrentDocument { get; set; }
        public string RunId { get; set; }
        public string RunStatus { get; set; }
        public string RunPhase { get; set; }
        public DateTime? RunStartedUtc { get; set; }
    }

    public sealed class HtmlWorkspace
    {
        public string ActiveFileId { get; set; }
        public List<HtmlWorkspaceFile> Files { get; set; }
        public List<HtmlWorkspaceDataSource> DataSources { get; set; }
        public List<HtmlWorkspaceSnapshot> History { get; set; }
        public List<HtmlWorkspaceSnapshot> RedoHistory { get; set; }
        public DateTime UpdatedUtc { get; set; }

        public HtmlWorkspace()
        {
            Files = new List<HtmlWorkspaceFile>();
            DataSources = new List<HtmlWorkspaceDataSource>();
            History = new List<HtmlWorkspaceSnapshot>();
            RedoHistory = new List<HtmlWorkspaceSnapshot>();
            UpdatedUtc = DateTime.UtcNow;
        }
    }

    public sealed class HtmlWorkspaceSnapshot
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public string ActiveFileId { get; set; }
        public List<HtmlWorkspaceFile> Files { get; set; }
        public List<HtmlWorkspaceDataSource> DataSources { get; set; }
        public DateTime CreatedUtc { get; set; }

        public HtmlWorkspaceSnapshot()
        {
            Id = Guid.NewGuid().ToString("N");
            Files = new List<HtmlWorkspaceFile>();
            DataSources = new List<HtmlWorkspaceDataSource>();
            CreatedUtc = DateTime.UtcNow;
        }
    }

    public sealed class HtmlWorkspaceFile
    {
        public string Id { get; set; }
        public string Path { get; set; }
        public string Kind { get; set; }
        public string Content { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }

        public HtmlWorkspaceFile()
        {
            CreatedUtc = DateTime.UtcNow;
            UpdatedUtc = CreatedUtc;
        }
    }

    public sealed class HtmlWorkspaceDataSource
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Json { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }

        public HtmlWorkspaceDataSource()
        {
            CreatedUtc = DateTime.UtcNow;
            UpdatedUtc = CreatedUtc;
        }
    }
}
