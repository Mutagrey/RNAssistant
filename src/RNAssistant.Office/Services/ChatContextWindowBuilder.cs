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
            IReadOnlyList<ChatAttachment> attachments,
            int historyBudgetTokens = 0)
        {
            settings = settings ?? new AppSettings();
            var messages = new List<ChatMessage>();
            var instruction = string.IsNullOrWhiteSpace(settings.ChatSystemPrompt)
                ? new AppSettings().ChatSystemPrompt
                : settings.ChatSystemPrompt.Trim();
            var instructionRole = NormalizeInstructionRole(settings.SystemPromptRole);
            if (!string.Equals(instructionRole, "user", StringComparison.Ordinal))
            {
                messages.Add(new ChatMessage { Role = instructionRole, Content = instruction });
            }

            var budget = ModelContextBudget.InputBudgetTokens(settings);
            var currentText = BuildCurrentText(userText, context, Math.Max(256, budget / 3), settings);
            var artifactIndex = ChatArtifactService.BuildPromptIndex(
                session,
                Math.Max(256, Math.Min(2000, budget / 10)),
                settings);
            if (!string.IsNullOrWhiteSpace(artifactIndex)) currentText += "\n\n" + artifactIndex;
            if (string.Equals(instructionRole, "user", StringComparison.Ordinal))
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

            new PromptBudgetComposer().AddConversationHistory(
                messages,
                messages.Count - 1,
                session,
                settings,
                historyBudgetTokens,
                false);
            return messages;
        }

        private static string NormalizeInstructionRole(string role)
        {
            if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase)) return "system";
            if (string.Equals(role, "developer", StringComparison.OrdinalIgnoreCase)) return "developer";
            return "user";
        }

        private static string BuildCurrentText(
            string userText,
            DocumentContext context,
            int contextBudgetTokens,
            AppSettings settings)
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
                if (ModelContextBudget.EstimateTextTokens(line, settings) > remaining)
                {
                    line = ModelContextBudget.TruncateText(line, remaining, settings) + "\n[context truncated]";
                }
                if (!wroteHeader)
                {
                    builder.AppendLine();
                    builder.AppendLine();
                    builder.AppendLine("USER_ADDED_CONTEXT:");
                    wroteHeader = true;
                }
                builder.AppendLine(line);
                usedTokens += ModelContextBudget.EstimateTextTokens(line, settings);
            }
            return builder.ToString();
        }

    }
}
