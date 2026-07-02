using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal sealed class ChatContextWindowBuilder
    {
        public List<ChatMessage> BuildPlainMessages(
            string userText,
            ChatSession session,
            DocumentContext context,
            AppSettings settings,
            IReadOnlyList<ChatAttachment> attachments)
        {
            settings = settings ?? new AppSettings();
            var messages = new List<ChatMessage>();
            var instruction = (settings.SystemPrompt ?? string.Empty).Trim();
            instruction += "\nAnswer the user normally. Do not emit planner JSON and do not claim to call local tools.";
            if (string.Equals(settings.SystemPromptRole, "system", StringComparison.OrdinalIgnoreCase))
            {
                messages.Add(new ChatMessage { Role = "system", Content = instruction });
            }

            var budget = ModelContextBudget.InputBudgetTokens(settings);
            var currentText = BuildCurrentText(userText, context, Math.Max(256, budget / 3));
            if (!string.Equals(settings.SystemPromptRole, "system", StringComparison.OrdinalIgnoreCase))
            {
                currentText = instruction + "\n\n" + currentText;
            }
            var current = new ChatMessage
            {
                Role = "user",
                Content = currentText,
                Attachments = attachments == null
                    ? new List<ChatAttachment>()
                    : new List<ChatAttachment>(attachments)
            };
            messages.Add(current);

            var used = ModelContextBudget.EstimateMessagesTokens(messages) + EstimateExtractedAttachmentTokens(attachments);
            var history = ConversationHistory(session);
            var insertAt = messages.Count - 1;
            for (var index = history.Count - 1; index >= 0; index--)
            {
                var source = history[index];
                var candidate = new ChatMessage
                {
                    Role = source.Role,
                    Content = source.Content ?? string.Empty,
                    Attachments = source.Attachments == null
                        ? new List<ChatAttachment>()
                        : new List<ChatAttachment>(source.Attachments)
                };
                var estimate = ModelContextBudget.EstimateMessagesTokens(new[] { candidate }) +
                    EstimateExtractedAttachmentTokens(candidate.Attachments);
                if (used + estimate > budget)
                {
                    break;
                }
                messages.Insert(insertAt, candidate);
                used += estimate;
            }
            return messages;
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

        private static bool IsConversationMessage(ChatMessage message)
        {
            return message != null &&
                message.Activity == null &&
                !string.IsNullOrWhiteSpace(message.Content) &&
                (string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase));
        }

        private static string BuildCurrentText(string userText, DocumentContext context, int contextBudgetTokens)
        {
            var builder = new StringBuilder();
            builder.Append(userText ?? string.Empty);
            var notes = context == null ? null : context.Notes;
            if (notes == null || notes.Count == 0)
            {
                return builder.ToString();
            }

            var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var wroteHeader = false;
            var usedTokens = 0;
            foreach (var note in notes)
            {
                if (note == null)
                {
                    continue;
                }
                var text = !string.IsNullOrWhiteSpace(note.Text) ? note.Text : note.Preview;
                var identity = !string.IsNullOrWhiteSpace(note.Reference)
                    ? note.Host + "|" + note.Kind + "|" + note.Reference
                    : note.Id;
                if (string.IsNullOrWhiteSpace(text) || !included.Add(identity))
                {
                    continue;
                }
                var line = "- " + (!string.IsNullOrWhiteSpace(note.Title) ? note.Title : note.Kind) + ": " + text;
                var remaining = contextBudgetTokens - usedTokens;
                if (remaining <= 0)
                {
                    builder.AppendLine("[additional context omitted by token budget]");
                    break;
                }
                if (ModelContextBudget.EstimateTextTokens(line) > remaining)
                {
                    var maxChars = Math.Max(0, remaining * 2);
                    line = line.Substring(0, Math.Min(line.Length, maxChars)) + "\n[context truncated]";
                }
                if (!wroteHeader)
                {
                    builder.AppendLine();
                    builder.AppendLine();
                    builder.AppendLine("USER_ADDED_CONTEXT:");
                    wroteHeader = true;
                }
                builder.AppendLine(line);
                usedTokens += ModelContextBudget.EstimateTextTokens(line);
            }
            return builder.ToString();
        }

        private static int EstimateExtractedAttachmentTokens(IEnumerable<ChatAttachment> attachments)
        {
            var total = 0;
            foreach (var attachment in attachments ?? new ChatAttachment[0])
            {
                if (attachment == null)
                {
                    continue;
                }
                total += Math.Max(attachment.ExtractedCharCount, (attachment.ExtractedText ?? string.Empty).Length) / 2;
            }
            return total;
        }
    }
}
