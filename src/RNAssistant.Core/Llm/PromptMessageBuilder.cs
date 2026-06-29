using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Llm
{
    public static class PromptMessageBuilder
    {
        public static List<ChatMessage> Build(string systemPrompt, string contextPrompt, IEnumerable<ChatMessage> sessionMessages, int charLimit)
        {
            var result = new List<ChatMessage> { new ChatMessage { Role = "system", Content = systemPrompt } };
            if (!string.IsNullOrWhiteSpace(contextPrompt))
            {
                result.Add(new ChatMessage { Role = "user", Content = contextPrompt });
            }

            var history = new List<ChatMessage>();
            var remaining = Math.Max(4000, charLimit);
            foreach (var message in (sessionMessages ?? new ChatMessage[0]).Reverse())
            {
                var promptContent = ContentForPrompt(message);
                if (string.IsNullOrEmpty(promptContent) && (message.Attachments == null || message.Attachments.Count == 0))
                {
                    continue;
                }

                remaining -= promptContent.Length;
                if (remaining < 0)
                {
                    break;
                }

                history.Insert(0, new ChatMessage
                {
                    Id = message.Id,
                    Role = message.Role,
                    Content = promptContent,
                    Attachments = message.Attachments == null ? new List<ChatAttachment>() : new List<ChatAttachment>(message.Attachments),
                    PromptTokens = message.PromptTokens,
                    CompletionTokens = message.CompletionTokens,
                    TotalTokens = message.TotalTokens,
                    UsageJson = message.UsageJson,
                    CreatedUtc = message.CreatedUtc
                });
            }

            result.AddRange(history);
            return result;
        }

        private static string ContentForPrompt(ChatMessage message)
        {
            if (message == null)
            {
                return string.Empty;
            }

            if (message.Activity == null)
            {
                return message.Content ?? string.Empty;
            }

            var builder = new StringBuilder();
            builder.AppendLine("Agent activity summary:");
            AppendActivity(builder, message.Activity, 0);
            return builder.ToString();
        }

        private static void AppendActivity(StringBuilder builder, ChatActivity activity, int depth)
        {
            if (activity == null)
            {
                return;
            }

            var indent = new string(' ', depth * 2);
            builder.Append(indent).Append("- ");
            builder.Append(string.IsNullOrWhiteSpace(activity.Title) ? "Agent step" : activity.Title);
            if (!string.IsNullOrWhiteSpace(activity.ToolId))
            {
                builder.Append(" [").Append(activity.ToolId).Append("]");
            }
            if (!string.IsNullOrWhiteSpace(activity.Status))
            {
                builder.Append(" status=").Append(activity.Status);
            }
            builder.AppendLine();

            var status = activity.Status ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(activity.ResultMessage))
            {
                builder.Append(indent).Append("  message: ").AppendLine(Truncate(activity.ResultMessage, 800));
            }
            if (IsCompleted(status) && !string.IsNullOrWhiteSpace(activity.DataJson))
            {
                builder.Append(indent).AppendLine("  output data:");
                builder.AppendLine(Truncate(activity.DataJson, 4000));
            }

            foreach (var child in activity.Children ?? new List<ChatActivity>())
            {
                AppendActivity(builder, child, depth + 1);
            }
        }

        private static bool IsCompleted(string status)
        {
            return string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase);
        }

        private static string Truncate(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            {
                return value ?? string.Empty;
            }

            return value.Substring(0, Math.Max(0, maxChars)) + "\n[truncated]";
        }
    }

    public static class ContextUsageEstimator
    {
        public static object FromPrompt(IEnumerable<ChatMessage> promptMessages, AppSettings settings)
        {
            var limit = Math.Max(4000, settings == null ? 24000 : settings.ContextCharLimit);
            var used = 0;
            var count = 0;
            if (promptMessages != null)
            {
                foreach (var message in promptMessages)
                {
                    if (message == null)
                    {
                        continue;
                    }

                    used += (message.Content ?? string.Empty).Length;
                    used += (message.Attachments ?? new List<ChatAttachment>())
                        .Where(a => a != null)
                        .Sum(a => (a.ExtractedText ?? string.Empty).Length);
                    count += 1;
                }
            }

            return Usage(used, limit, count, true);
        }

        public static object FromSession(ChatSession session, AppSettings settings)
        {
            var limit = Math.Max(4000, settings == null ? 24000 : settings.ContextCharLimit);
            var used = 0;
            var count = 0;
            if (session != null && session.Messages != null)
            {
                foreach (var message in session.Messages)
                {
                    if (message == null)
                    {
                        continue;
                    }

                    used += (message.Content ?? string.Empty).Length;
                    used += (message.Attachments ?? new List<ChatAttachment>())
                        .Where(a => a != null)
                        .Sum(a => (a.ExtractedText ?? string.Empty).Length);
                    count += 1;
                }
            }
            if (session != null && session.Context != null && session.Context.Notes != null)
            {
                foreach (var note in session.Context.Notes)
                {
                    if (note == null)
                    {
                        continue;
                    }

                    used += (note.Text ?? note.Preview ?? string.Empty).Length;
                }
            }

            return Usage(used, limit, count, false);
        }

        private static object Usage(int used, int limit, int count, bool actual)
        {
            return new
            {
                usedChars = used,
                limitChars = limit,
                percent = limit <= 0 ? 0 : Math.Min(100, (int)Math.Round(used * 100.0 / limit)),
                messageCount = count,
                actual = actual
            };
        }
    }
}
