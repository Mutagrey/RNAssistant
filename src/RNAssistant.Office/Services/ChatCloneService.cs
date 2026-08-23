using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Office.Services
{
    public static class ChatCloneService
    {
        public static DocumentContext CloneContext(DocumentContext context)
        {
            if (context == null)
            {
                return null;
            }

            return new DocumentContext
            {
                Host = context.Host,
                DocumentKey = context.DocumentKey,
                Title = context.Title,
                UpdatedUtc = context.UpdatedUtc,
                Notes = context.Notes == null ? null : context.Notes.Select(CloneContextNote).ToList()
            };
        }

        public static List<ChatMessage> CloneMessages(IEnumerable<ChatMessage> messages)
        {
            return messages == null
                ? new List<ChatMessage>()
                : messages.Select(CloneMessage).ToList();
        }

        public static List<ContextCheckpoint> CloneContextCheckpoints(IEnumerable<ContextCheckpoint> checkpoints, IEnumerable<ChatMessage> messages)
        {
            var messageIds = new HashSet<string>((messages ?? new ChatMessage[0]).Where(message => message != null).Select(message => message.Id), System.StringComparer.OrdinalIgnoreCase);
            return (checkpoints ?? new ContextCheckpoint[0])
                .Where(checkpoint => checkpoint != null && messageIds.Contains(checkpoint.ThroughMessageId))
                .Select(checkpoint => new ContextCheckpoint
                {
                    Id = checkpoint.Id,
                    ThroughMessageId = checkpoint.ThroughMessageId,
                    SummaryJson = checkpoint.SummaryJson,
                    SummaryMarkdown = checkpoint.SummaryMarkdown,
                    Model = checkpoint.Model,
                    PromptVersion = checkpoint.PromptVersion,
                    SourceMessageCount = checkpoint.SourceMessageCount,
                    SourceTokens = checkpoint.SourceTokens,
                    SummaryTokens = checkpoint.SummaryTokens,
                    CreatedUtc = checkpoint.CreatedUtc
                }).ToList();
        }

        public static List<ChatArtifact> CloneArtifactsForMessages(IEnumerable<ChatArtifact> artifacts, IEnumerable<ChatMessage> messages)
        {
            return ChatArtifactService.ReachableForMessages(artifacts, messages)
                .Select(CloneArtifact)
                .ToList();
        }

        public static HtmlWorkspace CloneWorkspaceForFork(HtmlWorkspace workspace)
        {
            return HtmlWorkspaceCopyService.CloneCurrent(workspace);
        }

        private static ChatMessage CloneMessage(ChatMessage message)
        {
            if (message == null)
            {
                return null;
            }

            return new ChatMessage
            {
                Id = message.Id,
                Role = message.Role,
                Content = message.Content,
                ExcludeFromModelContext = message.ExcludeFromModelContext,
                ProtocolMessage = message.ProtocolMessage,
                ToolCallId = message.ToolCallId,
                ToolName = message.ToolName,
                ToolResultRole = message.ToolResultRole,
                ToolCalls = message.ToolCalls == null
                    ? new List<LlmToolCall>()
                    : message.ToolCalls.Where(call => call != null).Select(call => new LlmToolCall
                    {
                        Id = call.Id,
                        Type = call.Type,
                        Name = call.Name,
                        ArgumentsJson = call.ArgumentsJson
                    }).ToList(),
                Attachments = message.Attachments == null
                    ? new List<ChatAttachment>()
                    : message.Attachments.Select(CloneAttachment).ToList(),
                ArtifactIds = message.ArtifactIds == null ? new List<string>() : new List<string>(message.ArtifactIds),
                HtmlWorkspaceCheckpointId = message.HtmlWorkspaceCheckpointId,
                Activity = CloneActivity(message.Activity),
                PromptTokens = message.PromptTokens,
                CompletionTokens = message.CompletionTokens,
                TotalTokens = message.TotalTokens,
                UsageJson = message.UsageJson,
                ReasoningContent = message.ReasoningContent,
                ReasoningTokens = message.ReasoningTokens,
                ReasoningTruncated = message.ReasoningTruncated,
                RunId = message.RunId,
                Sequence = message.Sequence,
                CreatedUtc = message.CreatedUtc
            };
        }

        private static ChatArtifact CloneArtifact(ChatArtifact artifact)
        {
            return new ChatArtifact
            {
                Id = artifact.Id,
                Kind = artifact.Kind,
                Title = artifact.Title,
                MimeType = artifact.MimeType,
                SourceMessageId = artifact.SourceMessageId,
                RunId = artifact.RunId,
                Revision = artifact.Revision,
                ParentArtifactId = artifact.ParentArtifactId,
                RelativePath = artifact.RelativePath,
                InlineText = artifact.InlineText,
                ModelContextPolicy = artifact.ModelContextPolicy,
                MetadataJson = artifact.MetadataJson,
                RelatedArtifactIds = artifact.RelatedArtifactIds == null ? new List<string>() : new List<string>(artifact.RelatedArtifactIds),
                CreatedUtc = artifact.CreatedUtc
            };
        }

        private static ChatAttachment CloneAttachment(ChatAttachment attachment)
        {
            if (attachment == null)
            {
                return null;
            }
            return new ChatAttachment
            {
                Id = attachment.Id,
                FileName = attachment.FileName,
                ContentType = attachment.ContentType,
                Size = attachment.Size,
                Kind = attachment.Kind,
                RelativePath = attachment.RelativePath,
                ExtractedText = attachment.ExtractedText,
                ExtractedTextPath = attachment.ExtractedTextPath,
                ExtractedCharCount = attachment.ExtractedCharCount,
                TextTruncated = attachment.TextTruncated,
                PageCount = attachment.PageCount,
                PageTextLengths = attachment.PageTextLengths == null ? new List<int>() : new List<int>(attachment.PageTextLengths),
                ExtractionWarning = attachment.ExtractionWarning,
                Status = attachment.Status,
                Error = attachment.Error,
                CreatedUtc = attachment.CreatedUtc
            };
        }

        private static ChatActivity CloneActivity(ChatActivity activity)
        {
            if (activity == null)
            {
                return null;
            }

            return new ChatActivity
            {
                RunId = activity.RunId,
                Sequence = activity.Sequence,
                StepId = activity.StepId,
                StepMessage = activity.StepMessage,
                Kind = activity.Kind,
                Title = activity.Title,
                Subtitle = activity.Subtitle,
                Status = activity.Status,
                ExecutionStatus = activity.ExecutionStatus,
                ErrorCode = activity.ErrorCode,
                Retryable = activity.Retryable,
                PendingId = activity.PendingId,
                ToolId = activity.ToolId,
                ToolCallId = activity.ToolCallId,
                ArgumentsJson = activity.ArgumentsJson,
                ResultMessage = activity.ResultMessage,
                DataJson = activity.DataJson,
                Children = activity.Children == null ? null : activity.Children.Select(CloneActivity).ToList()
            };
        }

        private static ContextNote CloneContextNote(ContextNote note)
        {
            if (note == null)
            {
                return null;
            }

            return new ContextNote
            {
                Id = note.Id,
                Host = note.Host,
                Kind = note.Kind,
                Title = note.Title,
                Reference = note.Reference,
                Source = note.Source,
                Text = note.Text,
                Preview = note.Preview,
                DetailsJson = note.DetailsJson,
                CreatedUtc = note.CreatedUtc
            };
        }
    }
}
