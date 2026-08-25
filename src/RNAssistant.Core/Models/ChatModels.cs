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

    public static class ChatStorageWarningLevels
    {
        public const string None = "none";
        public const string Warning = "warning";
        public const string Critical = "critical";
    }

    public static class ChatStorageUsagePolicy
    {
        public const long WarningJsonlByteLength = 64L * 1024 * 1024;
        public const long CriticalJsonlByteLength = 256L * 1024 * 1024;
        public const long WarningStoredFootprintByteLength = 256L * 1024 * 1024;
        public const long CriticalStoredFootprintByteLength = 1024L * 1024 * 1024;
        public const long WarningCasLogicalByteLength = 512L * 1024 * 1024;
        public const long CriticalCasLogicalByteLength = 2L * 1024 * 1024 * 1024;

        public static string GetWarningLevel(
            long jsonlByteLength,
            long casLogicalByteLength,
            long casStoredByteLength,
            int missingCasBlobCount,
            int casReferenceIssueCount)
        {
            var storedFootprint = SaturatingAdd(jsonlByteLength, casStoredByteLength);
            if (missingCasBlobCount > 0 || casReferenceIssueCount > 0 ||
                jsonlByteLength >= CriticalJsonlByteLength ||
                storedFootprint >= CriticalStoredFootprintByteLength ||
                casLogicalByteLength >= CriticalCasLogicalByteLength)
            {
                return ChatStorageWarningLevels.Critical;
            }
            if (jsonlByteLength >= WarningJsonlByteLength ||
                storedFootprint >= WarningStoredFootprintByteLength ||
                casLogicalByteLength >= WarningCasLogicalByteLength)
            {
                return ChatStorageWarningLevels.Warning;
            }
            return ChatStorageWarningLevels.None;
        }

        private static long SaturatingAdd(long first, long second)
        {
            first = first < 0 ? 0 : first;
            second = second < 0 ? 0 : second;
            return first > long.MaxValue - second ? long.MaxValue : first + second;
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
        public AttachmentAnalysisContext AttachmentAnalysis { get; set; }
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

    public sealed class AttachmentAnalysisContext
    {
        public const int CurrentPromptVersion = 1;

        public int PromptVersion { get; set; }
        public string SourceFingerprint { get; set; }
        public string Content { get; set; }
        public List<string> Models { get; set; }
        public List<string> AttachmentIds { get; set; }
        public DateTime CreatedUtc { get; set; }

        public AttachmentAnalysisContext()
        {
            PromptVersion = CurrentPromptVersion;
            Models = new List<string>();
            AttachmentIds = new List<string>();
            CreatedUtc = DateTime.UtcNow;
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
        public string ContentSha256 { get; set; }
        public long? ContentByteLength { get; set; }
        public string ExtractedText { get; set; }
        public string ExtractedTextPath { get; set; }
        public string ExtractedTextSha256 { get; set; }
        public long? ExtractedTextByteLength { get; set; }
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
        public string ConfirmationCatalogSha256 { get; set; }
        public string ToolId { get; set; }
        public string ToolCallId { get; set; }
        public string ArgumentsJson { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string RuntimeGuardJson { get; set; }
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
        public const int CurrentFormatVersion = 5;

        [JsonProperty(Required = Required.Always)]
        public int FormatVersion { get; set; }
        public long Revision { get; set; }
        [JsonIgnore]
        internal string StorageHeadHash { get; set; }
        [JsonIgnore]
        internal long StorageByteLength { get; set; }
        [JsonIgnore]
        internal long StorageLastWriteUtcTicks { get; set; }
        [JsonIgnore]
        internal long StorageTailByteOffset { get; set; }
        public string Id { get; set; }
        public string ParentSessionId { get; set; }
        public long? ParentSessionRevision { get; set; }
        public string ForkedThroughMessageId { get; set; }
        public string Host { get; set; }
        public string DocumentKey { get; set; }
        public string DocumentTitle { get; set; }
        public string DocumentPath { get; set; }
        public string Title { get; set; }
        public string Model { get; set; }
        public string Mode { get; set; }
        public bool ReasoningEnabled { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public DocumentContext Context { get; set; }
        public ChatRunRecord LastRun { get; set; }
        public HtmlWorkspace HtmlWorkspace { get; set; }
        [JsonIgnore]
        public HtmlWorkspaceRecoveryState HtmlWorkspaceRecovery { get; set; }
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
            HtmlWorkspaceRecovery = new HtmlWorkspaceRecoveryState();
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
        public string TurnId { get; set; }
        public string RuntimeId { get; set; }
        public string Status { get; set; }
        public string Phase { get; set; }
        public string CurrentAction { get; set; }
        public string DocumentRuntimeKey { get; set; }
        public int IterationsUsed { get; set; }
        public int ToolStepsUsed { get; set; }
        public DateTime StartedUtc { get; set; }
    }

    public sealed class ChatSessionHeader
    {
        public string Id { get; set; }
        public long Revision { get; set; }
        public string Host { get; set; }
        public string DocumentKey { get; set; }
        public string DocumentTitle { get; set; }
        public string DocumentPath { get; set; }
        public string Title { get; set; }
        public string Model { get; set; }
        public string Mode { get; set; }
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
        public long JsonlByteLength { get; set; }
        public int CasBlobCount { get; set; }
        public long CasLogicalByteLength { get; set; }
        public long CasStoredByteLength { get; set; }
        public int CasMissingBlobCount { get; set; }
        public int CasReferenceIssueCount { get; set; }
        public string StorageWarningLevel { get; set; }
    }

    public sealed class ChatSessionSummary
    {
        public string Id { get; set; }
        public long Revision { get; set; }
        public string Host { get; set; }
        public string DocumentKey { get; set; }
        public string DocumentTitle { get; set; }
        public string DocumentPath { get; set; }
        public string Title { get; set; }
        public string Model { get; set; }
        public string Mode { get; set; }
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
        public long JsonlByteLength { get; set; }
        public int CasBlobCount { get; set; }
        public long CasLogicalByteLength { get; set; }
        public long CasStoredByteLength { get; set; }
        public int CasMissingBlobCount { get; set; }
        public int CasReferenceIssueCount { get; set; }
        public string StorageWarningLevel { get; set; }
    }

    public sealed class HtmlWorkspace
    {
        public string ActiveFileId { get; set; }
        public List<HtmlWorkspaceFile> Files { get; set; }
        public List<HtmlWorkspaceDataSource> DataSources { get; set; }
        public List<HtmlWorkspaceSnapshot> History { get; set; }
        public List<HtmlWorkspaceRedoBranch> RedoBranches { get; set; }
        public DateTime UpdatedUtc { get; set; }

        public HtmlWorkspace()
        {
            Files = new List<HtmlWorkspaceFile>();
            DataSources = new List<HtmlWorkspaceDataSource>();
            History = new List<HtmlWorkspaceSnapshot>();
            RedoBranches = new List<HtmlWorkspaceRedoBranch>();
            UpdatedUtc = DateTime.UtcNow;
        }
    }

    public sealed class HtmlWorkspaceRedoBranch
    {
        public string Id { get; set; }
        public string ParentArtifactId { get; set; }
        public string Label { get; set; }
        public int Revision { get; set; }
        public int? FileCount { get; set; }
        public int? DataSourceCount { get; set; }
        public DateTime CreatedUtc { get; set; }
    }

    public static class HtmlWorkspaceRecoveryStatuses
    {
        public const string Empty = "empty";
        public const string Healthy = "healthy";
        public const string Degraded = "degraded";
    }

    public static class HtmlWorkspaceRecoveryIssues
    {
        public const string ActiveArtifactMissing = "active_artifact_missing";
        public const string ActiveBodyUnavailable = "active_body_unavailable";
        public const string ActiveBodyInvalid = "active_body_invalid";
        public const string ParentArtifactMissing = "parent_artifact_missing";
        public const string ParentBodyUnavailable = "parent_body_unavailable";
        public const string ParentBodyInvalid = "parent_body_invalid";
        public const string LineageCycle = "lineage_cycle";
    }

    public sealed class HtmlWorkspaceRecoveryState
    {
        public string Status { get; set; }
        public string Issue { get; set; }
        public string Message { get; set; }
        public string ActiveArtifactId { get; set; }
        public string ProblemArtifactId { get; set; }
        public bool CanMutate { get; set; }
        public List<HtmlWorkspaceRecoveryCandidate> Candidates { get; set; }

        public HtmlWorkspaceRecoveryState()
        {
            Status = HtmlWorkspaceRecoveryStatuses.Empty;
            CanMutate = true;
            Candidates = new List<HtmlWorkspaceRecoveryCandidate>();
        }
    }

    public sealed class HtmlWorkspaceRecoveryCandidate
    {
        public string Id { get; set; }
        public string ParentArtifactId { get; set; }
        public string Label { get; set; }
        public int Revision { get; set; }
        public int? FileCount { get; set; }
        public int? DataSourceCount { get; set; }
        public DateTime CreatedUtc { get; set; }
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
        public HtmlWorkspaceDataBinding Binding { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }

        public HtmlWorkspaceDataSource()
        {
            CreatedUtc = DateTime.UtcNow;
            UpdatedUtc = CreatedUtc;
        }
    }

    public sealed class HtmlWorkspaceDataBinding
    {
        public string ToolId { get; set; }
        public string ArgumentsJson { get; set; }
        public string Transform { get; set; }
        public string Headers { get; set; }
        public string RefreshPolicy { get; set; }
        public string Host { get; set; }
        public string DocumentKey { get; set; }
        public string DocumentTitle { get; set; }
        public string Status { get; set; }
        public string LastError { get; set; }
        public string ContentSha256 { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public DateTime? LastRefreshUtc { get; set; }

        public HtmlWorkspaceDataBinding()
        {
            ArgumentsJson = "{}";
            Transform = "raw";
            Headers = "firstRow";
            RefreshPolicy = "on_preview";
            Status = "ready";
            CreatedUtc = DateTime.UtcNow;
            UpdatedUtc = CreatedUtc;
        }
    }
}
