using System;
using System.Collections.Generic;

namespace RNAssistant.Core.Models
{
    public sealed class ChatMessage
    {
        public string Role { get; set; }
        public string Content { get; set; }
        public DateTime CreatedUtc { get; set; }

        public ChatMessage()
        {
            CreatedUtc = DateTime.UtcNow;
        }
    }

    public sealed class ChatSession
    {
        public string Id { get; set; }
        public string Host { get; set; }
        public string DocumentKey { get; set; }
        public string Title { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public List<ChatMessage> Messages { get; set; }

        public ChatSession()
        {
            Id = Guid.NewGuid().ToString("N");
            UpdatedUtc = DateTime.UtcNow;
            Messages = new List<ChatMessage>();
        }
    }
}

