using System;
using System.Collections.Generic;

namespace RNAssistant.Core.Models
{
    public static class ChatModes
    {
        public const string Chat = "chat";
        public const string Auto = "auto";
        public const string Agent = "agent";

        public static string Normalize(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            return value == Auto || value == Agent ? value : Chat;
        }
    }

    public sealed class ChatMessage
    {
        public string Id { get; set; }
        public string Role { get; set; }
        public string Content { get; set; }
        public List<ChatAttachment> Attachments { get; set; }
        public ChatActivity Activity { get; set; }
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
        public int? TotalTokens { get; set; }
        public string UsageJson { get; set; }
        public string ReasoningContent { get; set; }
        public int? ReasoningTokens { get; set; }
        public bool ReasoningTruncated { get; set; }
        public DateTime CreatedUtc { get; set; }

        public ChatMessage()
        {
            Id = Guid.NewGuid().ToString("N");
            CreatedUtc = DateTime.UtcNow;
            Attachments = new List<ChatAttachment>();
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
        public string Kind { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string Status { get; set; }
        public string ExecutionStatus { get; set; }
        public string ErrorCode { get; set; }
        public bool? Retryable { get; set; }
        public string PendingId { get; set; }
        public string ToolId { get; set; }
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
        public string Id { get; set; }
        public string SessionId { get; set; }
        public string Host { get; set; }
        public string DocumentKey { get; set; }
        public string DocumentTitle { get; set; }
        public string DocumentPath { get; set; }
        public string Title { get; set; }
        public string Model { get; set; }
        public string Mode { get; set; }
        public bool HtmlModeEnabled { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public DocumentContext Context { get; set; }
        public PendingAgentTask PendingAgentTask { get; set; }
        public HtmlWorkspace HtmlWorkspace { get; set; }
        public List<ChatMessage> Messages { get; set; }

        public ChatSession()
        {
            Id = Guid.NewGuid().ToString("N");
            SessionId = Id;
            CreatedUtc = DateTime.UtcNow;
            UpdatedUtc = DateTime.UtcNow;
            Context = new DocumentContext();
            HtmlWorkspace = new HtmlWorkspace();
            Messages = new List<ChatMessage>();
            Mode = ChatModes.Chat;
        }
    }

    public sealed class PendingAgentTask
    {
        public string Request { get; set; }
        public string LastQuestion { get; set; }
        public string Kind { get; set; }
        public DateTime UpdatedUtc { get; set; }
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
        public bool HasHtmlWorkspace { get; set; }
        public int HtmlFileCount { get; set; }
        public int HtmlDataSourceCount { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public int MessageCount { get; set; }
        public bool IsCurrentDocument { get; set; }
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
