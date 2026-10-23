using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace RNAssistant.Core.Models
{
    // Browser-safe projection shared by document and workspace chats.
    // Protocol bodies and execution evidence stay in the durable/model path.
    public sealed class ChatMessageViewDto
    {
        public string Id { get; set; }
        public string Role { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string Content { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public bool ProtocolMessage { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public bool ExcludeFromModelContext { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public RunViewState RunViewState { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public IReadOnlyList<ChatAttachmentViewDto> Attachments { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public IReadOnlyList<string> ArtifactIds { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public ChatActivity Activity { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public int? PromptTokens { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public int? CompletionTokens { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public int? TotalTokens { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string ReasoningContent { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public int? ReasoningTokens { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public bool ReasoningTruncated { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string RunId { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public DateTime? CreatedUtc { get; set; }

        public static ChatMessageViewDto From(ChatMessage message)
        {
            if (message == null) return null;
            var view = new ChatMessageViewDto
            {
                Id = message.Id,
                Role = message.Role,
                ProtocolMessage = message.ProtocolMessage,
                RunId = message.RunId
            };
            if (message.ProtocolMessage) return view;
            view.Content = message.Content;
            view.ExcludeFromModelContext = message.ExcludeFromModelContext;
            view.RunViewState = message.RunViewState;
            view.Attachments = message.Attachments == null || message.Attachments.Count == 0
                ? null : message.Attachments.Select(ChatAttachmentViewDto.From).ToList();
            view.PromptTokens = message.PromptTokens;
            view.CompletionTokens = message.CompletionTokens;
            view.TotalTokens = message.TotalTokens;
            view.ReasoningContent = message.ReasoningContent;
            view.ReasoningTokens = message.ReasoningTokens;
            view.ReasoningTruncated = message.ReasoningTruncated;
            view.CreatedUtc = message.CreatedUtc;
            return view;
        }
    }

    public sealed class ChatAttachmentViewDto
    {
        public string Id { get; set; }
        public string FileName { get; set; }
        public string Kind { get; set; }
        public long Size { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string ExtractionWarning { get; set; }

        public static ChatAttachmentViewDto From(ChatAttachment attachment)
        {
            if (attachment == null) return null;
            return new ChatAttachmentViewDto
            {
                Id = attachment.Id,
                FileName = attachment.FileName,
                Kind = attachment.Kind,
                Size = attachment.Size,
                ExtractionWarning = attachment.ExtractionWarning
            };
        }
    }
}
