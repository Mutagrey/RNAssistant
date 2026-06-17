using System;
using System.Collections.Generic;

namespace RNAssistant.Core.Models
{
    public sealed class DocumentContext
    {
        public string Host { get; set; }
        public string DocumentKey { get; set; }
        public string Title { get; set; }
        public string Summary { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public List<ContextNote> Notes { get; set; }

        public DocumentContext()
        {
            UpdatedUtc = DateTime.UtcNow;
            Notes = new List<ContextNote>();
        }
    }

    public sealed class ContextNote
    {
        public string Source { get; set; }
        public string Text { get; set; }
        public DateTime CreatedUtc { get; set; }

        public ContextNote()
        {
            CreatedUtc = DateTime.UtcNow;
        }
    }
}

