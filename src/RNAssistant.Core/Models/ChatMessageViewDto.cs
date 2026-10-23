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
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string ReflectionDecision { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public IReadOnlyList<ModelResultViewState> Results { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public int? PromptTokens { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public int? CompletionTokens { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public int? TotalTokens { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string ReasoningContent { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public int? ReasoningTokens { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public bool ReasoningTruncated { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string RunId { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string StepId { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public DateTime? CreatedUtc { get; set; }

        public static ChatMessageViewDto From(ChatMessage message)
        {
            if (message == null) return null;
            var view = new ChatMessageViewDto
            {
                Id = message.Id,
                Role = message.Role,
                ProtocolMessage = message.ProtocolMessage,
                RunId = message.RunId,
                StepId = message.Activity?.StepId
            };
            if (message.ProtocolMessage) return view;
            view.Content = message.Content;
            view.ExcludeFromModelContext = message.ExcludeFromModelContext;
            view.RunViewState = message.RunViewState;
            view.Attachments = message.Attachments == null || message.Attachments.Count == 0
                ? null : message.Attachments.Select(ChatAttachmentViewDto.From).ToList();
            view.Activity = ChatActivityViewProjector.From(message.Activity);
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

    // One browser projection for Office and workspace activities. Runtime guards
    // and payload references never enter the WebView state.
    public static class ChatActivityViewProjector
    {
        public static ChatActivity From(ChatActivity activity)
        {
            if (activity == null) return null;
            return new ChatActivity
            {
                RunId = activity.RunId,
                Sequence = activity.Sequence,
                StepId = activity.StepId,
                StepMessage = activity.StepMessage,
                Kind = activity.Kind,
                Title = activity.Title,
                Display = activity.Display,
                Subtitle = activity.Subtitle,
                Status = activity.Status,
                ExecutionStatus = activity.ExecutionStatus,
                ExecutionEvidence = activity.ExecutionEvidence,
                ErrorCode = activity.ErrorCode,
                Retryable = activity.Retryable,
                PendingId = activity.PendingId,
                ToolId = activity.ToolId,
                ToolCallId = activity.ToolCallId,
                ArgumentsJson = activity.ArgumentsJson,
                ResultMessage = activity.ResultMessage,
                DataJson = activity.DataJson,
                ResultSummary = activity.ResultSummary == null ? null : new ToolResultSummary
                {
                    Kind = activity.ResultSummary.Kind,
                    Representation = activity.ResultSummary.Representation,
                    ReturnedItems = activity.ResultSummary.ReturnedItems,
                    TotalItems = activity.ResultSummary.TotalItems,
                    ReturnedCharacters = activity.ResultSummary.ReturnedCharacters,
                    ReturnedRows = activity.ResultSummary.ReturnedRows,
                    Complete = activity.ResultSummary.Complete,
                    Partial = activity.ResultSummary.Partial,
                    HydratedForNextModelStep = activity.ResultSummary.HydratedForNextModelStep
                },
                ReadSummary = activity.ReadSummary == null ? null : new ResourceReadSummary
                {
                    Representation = activity.ReadSummary.Representation,
                    ReturnedCharacters = activity.ReadSummary.ReturnedCharacters,
                    ReturnedRows = activity.ReadSummary.ReturnedRows,
                    Complete = activity.ReadSummary.Complete,
                    HydratedForNextModelStep = activity.ReadSummary.HydratedForNextModelStep
                },
                Children = activity.Children == null ? null : activity.Children.Select(From).ToList()
            };
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

    // Shared history window shape; host-specific responses may add artifact data.
    public class ChatMessageWindowViewDto
    {
        public const int PageSize = 80;
        [JsonProperty("chatId")] public string ChatId { get; set; }
        [JsonProperty("sessionRevision")] public long SessionRevision { get; set; }
        [JsonProperty("startIndex")] public int StartIndex { get; set; }
        [JsonProperty("totalCount")] public int TotalCount { get; set; }
        [JsonProperty("messages")] public IReadOnlyList<ChatMessageViewDto> Messages { get; set; }
    }

    public static class ChatMessageWindowProjector
    {
        public static T Project<T>(ChatSession session, int beforeIndex,
            Func<ChatSession, IEnumerable<ChatMessage>, IReadOnlyList<ChatMessageViewDto>> project)
            where T : ChatMessageWindowViewDto, new()
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            var messages = session?.Messages ?? new List<ChatMessage>();
            if (beforeIndex < 0 || beforeIndex > messages.Count)
                throw new ArgumentOutOfRangeException(nameof(beforeIndex));
            var start = Math.Max(0, beforeIndex - ChatMessageWindowViewDto.PageSize);
            return new T
            {
                ChatId = session?.Id,
                SessionRevision = session?.Revision ?? 0,
                StartIndex = start,
                TotalCount = messages.Count,
                Messages = project(session, messages.Skip(start).Take(beforeIndex - start))
            };
        }
    }
}
