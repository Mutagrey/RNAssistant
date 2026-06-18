using System;
using System.Collections.Generic;
using System.Linq;
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
                if (string.IsNullOrEmpty(message.Content))
                {
                    continue;
                }

                remaining -= message.Content.Length;
                if (remaining < 0)
                {
                    break;
                }

                history.Insert(0, message);
            }

            result.AddRange(history);
            return result;
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
