using System;
using System.Collections.Generic;

namespace RNAssistant.Core.Models
{
    public static class ChatArtifactKinds
    {
        public const string Markdown = "markdown";
        public const string HtmlWorkspace = "html_workspace";
        public const string Image = "image";
        public const string File = "file";
        public const string Attachment = "attachment";
        public const string Chart = "chart";
        public const string Compaction = "compaction";
        public const string ToolResult = "tool_result";
    }

    public sealed class ChatArtifact
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public string Title { get; set; }
        public string MimeType { get; set; }
        public string SourceMessageId { get; set; }
        public string RunId { get; set; }
        public int Revision { get; set; }
        public string ParentArtifactId { get; set; }
        public string RelativePath { get; set; }
        public string InlineText { get; set; }
        public string ModelContextPolicy { get; set; }
        public string MetadataJson { get; set; }
        public List<string> RelatedArtifactIds { get; set; }
        public DateTime CreatedUtc { get; set; }

        public ChatArtifact()
        {
            Id = Guid.NewGuid().ToString("N");
            Revision = 1;
            ModelContextPolicy = "reference";
            RelatedArtifactIds = new List<string>();
            CreatedUtc = DateTime.UtcNow;
        }
    }
}
