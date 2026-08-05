using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

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

        public static HtmlWorkspace CloneWorkspaceForFork(HtmlWorkspace workspace)
        {
            if (workspace == null)
            {
                return new HtmlWorkspace();
            }

            return new HtmlWorkspace
            {
                ActiveFileId = workspace.ActiveFileId,
                UpdatedUtc = workspace.UpdatedUtc,
                Files = workspace.Files == null ? new List<HtmlWorkspaceFile>() : workspace.Files.Select(CloneHtmlFile).ToList(),
                DataSources = workspace.DataSources == null ? new List<HtmlWorkspaceDataSource>() : workspace.DataSources.Select(CloneHtmlDataSource).ToList()
            };
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
                ToolCallId = message.ToolCallId,
                ToolName = message.ToolName,
                ToolCalls = message.ToolCalls == null
                    ? new List<LlmToolCall>()
                    : message.ToolCalls.Select(CloneToolCall).ToList(),
                Attachments = message.Attachments == null
                    ? new List<ChatAttachment>()
                    : message.Attachments.Select(CloneAttachment).ToList(),
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

        private static LlmToolCall CloneToolCall(LlmToolCall call)
        {
            return call == null
                ? null
                : new LlmToolCall
                {
                    Id = call.Id,
                    Type = call.Type,
                    Name = call.Name,
                    ArgumentsJson = call.ArgumentsJson
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
                Kind = activity.Kind,
                Title = activity.Title,
                Subtitle = activity.Subtitle,
                Status = activity.Status,
                ExecutionStatus = activity.ExecutionStatus,
                ErrorCode = activity.ErrorCode,
                Retryable = activity.Retryable,
                PendingId = activity.PendingId,
                ToolId = activity.ToolId,
                ArgumentsJson = activity.ArgumentsJson,
                ResultMessage = activity.ResultMessage,
                DataJson = activity.DataJson,
                Children = activity.Children == null ? null : activity.Children.Select(CloneActivity).ToList()
            };
        }

        private static HtmlWorkspaceFile CloneHtmlFile(HtmlWorkspaceFile file)
        {
            if (file == null)
            {
                return null;
            }

            return new HtmlWorkspaceFile
            {
                Id = file.Id,
                Path = file.Path,
                Kind = file.Kind,
                Content = file.Content,
                CreatedUtc = file.CreatedUtc,
                UpdatedUtc = file.UpdatedUtc
            };
        }

        private static HtmlWorkspaceDataSource CloneHtmlDataSource(HtmlWorkspaceDataSource dataSource)
        {
            if (dataSource == null)
            {
                return null;
            }

            return new HtmlWorkspaceDataSource
            {
                Id = dataSource.Id,
                Name = dataSource.Name,
                Json = dataSource.Json,
                CreatedUtc = dataSource.CreatedUtc,
                UpdatedUtc = dataSource.UpdatedUtc
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
