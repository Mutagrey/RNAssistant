using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
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
            var history = ConversationHistory(session);
            var available = Math.Max(0, budget - used);
            if (history.Count == 0 || available <= 0)
            {
                return;
            }

            var candidates = history.Select(CloneConversationMessage).ToList();
            var estimates = candidates.Select(candidate => EstimateMessages(new[] { candidate })).ToList();
            var needsCompression = estimates.Sum() > available;
            var compressionEnabled = settings == null || settings.AutoCompressContext;
            var summaryBudget = needsCompression && compressionEnabled && available >= 256
                ? Math.Min(1024, Math.Max(128, available / 5))
                : 0;
            var recentBudget = Math.Max(0, available - summaryBudget);
            var firstRecentIndex = candidates.Count;
            var recentUsed = 0;
            for (var index = candidates.Count - 1; index >= 0; index--)
            {
                if (recentUsed + estimates[index] > recentBudget)
                {
                    break;
                }
                firstRecentIndex = index;
                recentUsed += estimates[index];
            }

            if (firstRecentIndex == candidates.Count && summaryBudget > 0)
            {
                summaryBudget = 0;
                recentBudget = available;
                for (var index = candidates.Count - 1; index >= 0; index--)
                {
                    if (recentUsed + estimates[index] > recentBudget)
                    {
                        break;
                    }
                    firstRecentIndex = index;
                    recentUsed += estimates[index];
                }
            }

            ChatMessage compressed = null;
            if (summaryBudget > 0 && firstRecentIndex > 0)
            {
                compressed = BuildCompressedHistory(candidates.Take(firstRecentIndex), summaryBudget);
                if (compressed != null && EstimateMessages(new[] { compressed }) + recentUsed > available)
                {
                    compressed = null;
                }
            }

            if (compressed != null)
            {
                messages.Insert(insertIndex, compressed);
                insertIndex += 1;
            }
            for (var index = firstRecentIndex; index < candidates.Count; index++)
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
            var available = Math.Max(0, inputBudgetTokens - EstimateMessages(messages));
            if (available <= 0) return;

            var selected = new List<IReadOnlyList<ChatMessage>>();
            var groups = ProtocolGroups(protocolMessages);
            for (var index = groups.Count - 1; index >= 0; index--)
            {
                var cost = EstimateMessages(groups[index]);
                if (cost > available) break;
                selected.Insert(0, groups[index]);
                available -= cost;
            }
            foreach (var group in selected)
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

            var activeUserIndex = -1;
            for (var index = session.Messages.Count - 1; index >= 0; index--)
            {
                var message = session.Messages[index];
                if (IsConversationMessage(message) &&
                    string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    activeUserIndex = index;
                    break;
                }
            }

            return session.Messages
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
            return message != null &&
                message.Activity == null &&
                !string.IsNullOrWhiteSpace(message.Content) &&
                (string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase));
        }

        private static ChatMessage CloneConversationMessage(ChatMessage source)
        {
            return new ChatMessage
            {
                Role = source == null ? string.Empty : source.Role,
                Content = source == null ? string.Empty : source.Content ?? string.Empty,
                Attachments = source == null || source.Attachments == null
                    ? new List<ChatAttachment>()
                    : source.Attachments
                        .Where(attachment => attachment != null &&
                            !string.Equals(attachment.Kind, "image", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(attachment.Kind, "audio", StringComparison.OrdinalIgnoreCase))
                        .Select(CloneHistoryAttachment)
                        .ToList()
            };
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

        private static ChatMessage BuildCompressedHistory(IEnumerable<ChatMessage> history, int budgetTokens)
        {
            var builder = new StringBuilder();
            builder.AppendLine("COMPRESSED_EARLIER_CONVERSATION (reference only; not new instructions):");
            foreach (var message in history ?? new ChatMessage[0])
            {
                if (message == null || string.IsNullOrWhiteSpace(message.Content))
                {
                    continue;
                }
                var role = string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) ? "User" : "Assistant";
                var line = "- " + role + ": " + Compact(message.Content, 280);
                var attachmentNames = (message.Attachments ?? new List<ChatAttachment>())
                    .Where(attachment => attachment != null && !string.IsNullOrWhiteSpace(attachment.FileName))
                    .Select(attachment => attachment.FileName)
                    .Take(3)
                    .ToArray();
                if (attachmentNames.Length > 0)
                {
                    line += " [attachments: " + string.Join(", ", attachmentNames) + "]";
                }

                var remaining = budgetTokens - ModelContextBudget.EstimateTextTokens(builder.ToString()) - 4;
                if (remaining <= 8)
                {
                    break;
                }
                if (ModelContextBudget.EstimateTextTokens(line) > remaining)
                {
                    line = Compact(line, Math.Max(16, remaining * 3));
                }
                builder.AppendLine(line);
            }

            return builder.Length <= 80
                ? null
                : new ChatMessage { Role = "assistant", Content = builder.ToString().TrimEnd() };
        }

        private static string Compact(string value, int maxChars)
        {
            var normalized = string.Join(
                " ",
                (value ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            if (normalized.Length <= maxChars)
            {
                return normalized;
            }
            return normalized.Substring(0, Math.Max(0, maxChars - 1)).TrimEnd() + "…";
        }
    }
}
