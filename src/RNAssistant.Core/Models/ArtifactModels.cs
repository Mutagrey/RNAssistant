using System;
using System.Collections.Generic;

namespace RNAssistant.Core.Models
{
    public static class ChatArtifactLimits
    {
        public const int MaximumTextCharacters = 2000000;
    }

    public static class ChatArtifactKinds
    {
        public const string Plan = "plan";
        public const string Markdown = "markdown";
        public const string HtmlWorkspace = "html_workspace";
        public const string Image = "image";
        public const string File = "file";
        public const string Attachment = "attachment";
        public const string Chart = "chart";
        public const string Compaction = "compaction";
        public const string ToolResult = "tool_result";
    }

    public sealed class ChatPlan
    {
        public const int CurrentProtocolVersion = 1;

        [Newtonsoft.Json.JsonProperty("protocolVersion")]
        public int ProtocolVersion { get; set; }

        [Newtonsoft.Json.JsonProperty("id")]
        public string Id { get; set; }

        [Newtonsoft.Json.JsonProperty("goal")]
        public string Goal { get; set; }

        [Newtonsoft.Json.JsonProperty("steps")]
        public List<ChatPlanStep> Steps { get; set; }

        public ChatPlan()
        {
            ProtocolVersion = CurrentProtocolVersion;
            Steps = new List<ChatPlanStep>();
        }
    }

    public sealed class ChatPlanStep
    {
        [Newtonsoft.Json.JsonProperty("id")]
        public string Id { get; set; }

        [Newtonsoft.Json.JsonProperty("text")]
        public string Text { get; set; }

        [Newtonsoft.Json.JsonProperty("status")]
        public string Status { get; set; }
    }

    public sealed class ChatArtifact
    {
        private string _inlineText;

        public string Id { get; set; }
        public string Kind { get; set; }
        public string Title { get; set; }
        public string MimeType { get; set; }
        public string SourceMessageId { get; set; }
        public string RunId { get; set; }
        public int Revision { get; set; }
        public string ParentArtifactId { get; set; }
        public string RelativePath { get; set; }
        public string InlineText
        {
            get { return _inlineText; }
            set
            {
                if (!string.Equals(_inlineText, value, StringComparison.Ordinal)) StorageInlineTextTrusted = false;
                _inlineText = value;
            }
        }
        public string ContentSha256 { get; set; }
        public long? ContentByteLength { get; set; }
        [Newtonsoft.Json.JsonIgnore]
        internal bool StorageInlineTextTrusted { get; set; }
        [Newtonsoft.Json.JsonIgnore]
        internal string StorageContentSha256 { get; set; }
        [Newtonsoft.Json.JsonIgnore]
        internal long? StorageContentByteLength { get; set; }
        public string MetadataJson { get; set; }
        public List<string> RelatedArtifactIds { get; set; }
        public DateTime CreatedUtc { get; set; }

        public ChatArtifact()
        {
            Id = Guid.NewGuid().ToString("N");
            Revision = 1;
            RelatedArtifactIds = new List<string>();
            CreatedUtc = DateTime.UtcNow;
        }
    }
}
