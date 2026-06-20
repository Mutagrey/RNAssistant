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
        public string Id { get; set; }
        public string Host { get; set; }
        public string Kind { get; set; }
        public string Title { get; set; }
        public string Reference { get; set; }
        public string Source { get; set; }
        public string Text { get; set; }
        public string Preview { get; set; }
        public string DetailsJson { get; set; }
        public DateTime CreatedUtc { get; set; }

        public ContextNote()
        {
            Id = Guid.NewGuid().ToString("N");
            CreatedUtc = DateTime.UtcNow;
        }
    }

    public sealed class OfficeContext
    {
        public string Host { get; set; }
        public IntPtr AppHwnd { get; set; }
        public int ProcessId { get; set; }
        public string DocumentPath { get; set; }
        public string DocumentTitle { get; set; }
        public string ContainerName { get; set; }
        public string SelectionAddress { get; set; }
        public string SelectionText { get; set; }
        public DateTime CapturedAt { get; set; }

        public OfficeContext()
        {
            CapturedAt = DateTime.UtcNow;
        }
    }
}
