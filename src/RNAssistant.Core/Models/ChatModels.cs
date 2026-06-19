using System;
using System.Collections.Generic;

namespace RNAssistant.Core.Models
{
    public sealed class ChatMessage
    {
        public string Id { get; set; }
        public string Role { get; set; }
        public string Content { get; set; }
        public ChatActivity Activity { get; set; }
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
        public int? TotalTokens { get; set; }
        public string UsageJson { get; set; }
        public DateTime CreatedUtc { get; set; }

        public ChatMessage()
        {
            Id = Guid.NewGuid().ToString("N");
            CreatedUtc = DateTime.UtcNow;
        }
    }

    public sealed class ChatActivity
    {
        public string Kind { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string Status { get; set; }
        public string ExecutionStatus { get; set; }
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
        public string Title { get; set; }
        public string Model { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public DocumentContext Context { get; set; }
        public List<ChatMessage> Messages { get; set; }

        public ChatSession()
        {
            Id = Guid.NewGuid().ToString("N");
            SessionId = Id;
            CreatedUtc = DateTime.UtcNow;
            UpdatedUtc = DateTime.UtcNow;
            Context = new DocumentContext();
            Messages = new List<ChatMessage>();
        }
    }

    public sealed class ChatSessionSummary
    {
        public string Id { get; set; }
        public string Host { get; set; }
        public string DocumentKey { get; set; }
        public string DocumentTitle { get; set; }
        public string Title { get; set; }
        public string Model { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public int MessageCount { get; set; }
    }
}
