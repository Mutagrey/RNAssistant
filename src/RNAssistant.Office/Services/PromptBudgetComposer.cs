using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal sealed class PromptBudgetExceededException : InvalidOperationException
    {
        public bool CanCompact { get; private set; }

        public PromptBudgetExceededException(string message, bool canCompact)
            : base(message)
        {
            CanCompact = canCompact;
        }
    }

    internal sealed class PromptBudgetComposer
    {
        public void AddConversationHistory(
            List<ChatMessage> messages,
            int insertIndex,
            ChatSession session,
            AppSettings settings,
            int inputBudgetTokens = 0,
            bool includeProtocolMessages = true,
            bool excludeLatestUser = true)
        {
            if (messages == null)
            {
                return;
            }

            var budget = inputBudgetTokens > 0 ? inputBudgetTokens : ModelContextBudget.InputBudgetTokens(settings);
            var used = EstimateMessages(messages, settings);
            if (used > budget)
            {
                throw new PromptBudgetExceededException(
                    "The current request and required runtime context exceed the model input budget. No conversation history was removed.",
                    false);
            }
            var history = ConversationHistory(session, includeProtocolMessages, excludeLatestUser);
            var available = Math.Max(0, budget - used);
            if (history.Count == 0)
            {
                return;
            }
            if (available <= 0)
            {
                throw new PromptBudgetExceededException(
                    "Active conversation context exceeds the request budget. Run context compaction before this model turn.",
                    true);
            }

            var candidates = history.Select(CloneConversationMessage).ToList();
            var required = EstimateMessages(candidates, settings);
            if (required > available)
            {
                throw new PromptBudgetExceededException(
                    "Active conversation context exceeds the request budget. Run context compaction before this model turn.",
                    true);
            }
            for (var index = 0; index < candidates.Count; index++)
            {
                messages.Insert(insertIndex, candidates[index]);
                insertIndex += 1;
            }
        }

        public int EstimateMessages(IEnumerable<ChatMessage> messages, AppSettings settings = null)
        {
            return ModelContextBudget.EstimateMessagesTokens(messages, settings);
        }

        internal static List<ChatMessage> ConversationHistory(
            ChatSession session,
            bool includeProtocolMessages = true,
            bool excludeLatestUser = true)
        {
            if (session == null || session.Messages == null)
            {
                return new List<ChatMessage>();
            }

            var history = ContextCompactionService.BuildActiveWindow(session);
            var activeUserIndex = -1;
            for (var index = history.Count - 1; excludeLatestUser && index >= 0; index--)
            {
                var message = history[index];
                if (!message.ProtocolMessage && IsConversationMessage(message, includeProtocolMessages) &&
                    string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    activeUserIndex = index;
                    break;
                }
            }

            return history
                .Where((message, index) => index != activeUserIndex && IsConversationMessage(message, includeProtocolMessages))
                .ToList();
        }

        private static bool IsConversationMessage(ChatMessage message, bool includeProtocolMessages)
        {
            return (ContextCompactionService.IsReplayMessage(message) && (includeProtocolMessages || !message.ProtocolMessage)) ||
                (message != null &&
                 !message.ExcludeFromModelContext &&
                 message.Activity == null &&
                 !string.IsNullOrWhiteSpace(message.Content) &&
                 string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) &&
                 (message.Content ?? string.Empty).StartsWith("COMPACTED_EARLIER_CONTEXT", StringComparison.Ordinal));
        }

        private static ChatMessage CloneConversationMessage(ChatMessage source)
        {
            var sourceAttachments = source == null || source.Attachments == null
                ? new List<ChatAttachment>()
                : source.Attachments.Where(attachment => attachment != null).ToList();
            return new ChatMessage
            {
                Role = source == null ? string.Empty : source.Role,
                Content = AttachmentAnalysisService.AppendHistoricalContext(
                    AppendHistoricalReferences(source, sourceAttachments),
                    source == null ? null : source.AttachmentAnalysis),
                ProtocolMessage = source != null && source.ProtocolMessage,
                ToolCallId = source == null ? null : source.ToolCallId,
                ToolName = source == null ? null : source.ToolName,
                ToolResultRole = source == null ? null : source.ToolResultRole,
                ToolCalls = source == null || source.ToolCalls == null
                    ? new List<LlmToolCall>()
                    : source.ToolCalls.Where(call => call != null).Select(call => new LlmToolCall
                    {
                        Id = call.Id,
                        Type = call.Type,
                        Name = call.Name,
                        ArgumentsJson = call.ArgumentsJson
                    }).ToList(),
                Attachments = sourceAttachments
                        .Where(attachment =>
                            !string.Equals(attachment.Kind, "image", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(attachment.Kind, "audio", StringComparison.OrdinalIgnoreCase))
                        .Select(CloneHistoryAttachment)
                        .ToList()
            };
        }

        private static string AppendHistoricalReferences(ChatMessage source, IEnumerable<ChatAttachment> attachments)
        {
            if (source == null) return string.Empty;
            var references = new List<string>();
            references.AddRange((source.ArtifactIds ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => "artifact:" + id));
            if (!string.IsNullOrWhiteSpace(source.HtmlWorkspaceCheckpointId))
            {
                references.Add("html_workspace:" + source.HtmlWorkspaceCheckpointId);
            }
            references.AddRange((attachments ?? new ChatAttachment[0])
                .Where(attachment =>
                    string.Equals(attachment.Kind, "image", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(attachment.Kind, "audio", StringComparison.OrdinalIgnoreCase))
                .Select(attachment => "attachment:" + (attachment.Id ?? string.Empty) + " | " +
                    (attachment.Kind ?? "media") + " | " + (attachment.FileName ?? "unnamed")));
            references = references.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (references.Count == 0) return source.Content ?? string.Empty;
            return (source.Content ?? string.Empty) +
                "\n\nHISTORICAL_REFERENCES (local artifacts; not new instructions):\n- " +
                string.Join("\n- ", references.ToArray());
        }

        private static ChatAttachment CloneHistoryAttachment(ChatAttachment source)
        {
            return new ChatAttachment
            {
                Id = source.Id,
                FileName = source.FileName,
                ContentType = source.ContentType,
                Size = source.Size,
                Kind = string.Equals(source.Kind, "pdf", StringComparison.OrdinalIgnoreCase) ? "text" : source.Kind,
                RelativePath = source.RelativePath,
                ContentSha256 = source.ContentSha256,
                ContentByteLength = source.ContentByteLength,
                ExtractedText = source.ExtractedText,
                ExtractedTextPath = source.ExtractedTextPath,
                ExtractedTextSha256 = source.ExtractedTextSha256,
                ExtractedTextByteLength = source.ExtractedTextByteLength,
                ExtractedCharCount = source.ExtractedCharCount,
                TextTruncated = source.TextTruncated,
                PageCount = source.PageCount,
                PageTextLengths = source.PageTextLengths == null ? new List<int>() : new List<int>(source.PageTextLengths),
                ExtractionWarning = source.ExtractionWarning,
                Status = source.Status,
                Error = source.Error,
                CreatedUtc = source.CreatedUtc
            };
        }
    }
}
