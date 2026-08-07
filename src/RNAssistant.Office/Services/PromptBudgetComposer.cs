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
            int inputBudgetTokens = 0)
        {
            if (messages == null)
            {
                return;
            }

            var budget = inputBudgetTokens > 0 ? inputBudgetTokens : ModelContextBudget.InputBudgetTokens(settings);
            var used = EstimateMessages(messages);
            if (used > budget)
            {
                throw new PromptBudgetExceededException(
                    "The current request and required runtime context exceed the model input budget. No conversation history was removed.",
                    false);
            }
            var history = ConversationHistory(session);
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
            var required = EstimateMessages(candidates);
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

        public void AddProtocolHistory(
            List<ChatMessage> messages,
            IEnumerable<ChatMessage> protocolMessages,
            int inputBudgetTokens)
        {
            if (messages == null) return;
            var groups = ProtocolGroups(protocolMessages);
            if (groups.Count == 0) return;
            var available = Math.Max(0, inputBudgetTokens - EstimateMessages(messages));
            var required = groups.Sum(group => EstimateMessages(group));
            if (required > available)
            {
                throw new PromptBudgetExceededException(
                    "Current agent protocol exceeds the request budget. The accepted tool history was preserved; compact context before continuing.",
                    false);
            }
            foreach (var group in groups)
            {
                messages.AddRange(group);
            }
        }

        public int EstimateMessages(IEnumerable<ChatMessage> messages)
        {
            return ModelContextBudget.EstimateMessagesTokens(messages);
        }

        internal static List<ChatMessage> ConversationHistory(ChatSession session)
        {
            if (session == null || session.Messages == null)
            {
                return new List<ChatMessage>();
            }

            var history = ContextCompactionService.BuildActiveWindow(session);
            var activeUserIndex = -1;
            for (var index = history.Count - 1; index >= 0; index--)
            {
                var message = history[index];
                if (!message.ProtocolMessage && IsConversationMessage(message) &&
                    string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    activeUserIndex = index;
                    break;
                }
            }

            return history
                .Where((message, index) => index != activeUserIndex && IsConversationMessage(message))
                .ToList();
        }

        private static List<IReadOnlyList<ChatMessage>> ProtocolGroups(IEnumerable<ChatMessage> source)
        {
            var messages = (source ?? new ChatMessage[0]).Where(message => message != null).ToList();
            var groups = new List<IReadOnlyList<ChatMessage>>();
            for (var index = 0; index < messages.Count; index++)
            {
                var group = new List<ChatMessage> { messages[index] };
                if (string.Equals(messages[index].Role, "assistant", StringComparison.OrdinalIgnoreCase) && index + 1 < messages.Count)
                {
                    var next = messages[index + 1];
                    if (string.Equals(next.Role, "tool", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(next.Role, "developer", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(next.Role, "user", StringComparison.OrdinalIgnoreCase))
                    {
                        group.Add(next);
                        index += 1;
                    }
                }
                groups.Add(group);
            }
            return groups;
        }

        private static bool IsConversationMessage(ChatMessage message)
        {
            return ContextCompactionService.IsReplayMessage(message) ||
                message != null &&
                !message.ExcludeFromModelContext &&
                message.Activity == null &&
                !string.IsNullOrWhiteSpace(message.Content) &&
                string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) &&
                (message.Content ?? string.Empty).StartsWith("COMPACTED_EARLIER_CONTEXT", StringComparison.Ordinal);
        }

        private static ChatMessage CloneConversationMessage(ChatMessage source)
        {
            var sourceAttachments = source == null || source.Attachments == null
                ? new List<ChatAttachment>()
                : source.Attachments.Where(attachment => attachment != null).ToList();
            return new ChatMessage
            {
                Role = source == null ? string.Empty : source.Role,
                Content = AppendHistoricalReferences(source, sourceAttachments),
                ProtocolMessage = source != null && source.ProtocolMessage,
                ToolCallId = source == null ? null : source.ToolCallId,
                ToolName = source == null ? null : source.ToolName,
                ToolCalls = source == null || source.ToolCalls == null
                    ? new List<LlmToolCall>()
                    : source.ToolCalls.Select(call => call == null ? null : new LlmToolCall
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
                ExtractedText = source.ExtractedText,
                ExtractedTextPath = source.ExtractedTextPath,
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
