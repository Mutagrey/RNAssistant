using System.Collections.Generic;
using System.Linq;
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
                Summary = context.Summary,
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

        public static ChatMessage CloneMessage(ChatMessage message)
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
                PromptTokens = message.PromptTokens,
                CompletionTokens = message.CompletionTokens,
                TotalTokens = message.TotalTokens,
                UsageJson = message.UsageJson,
                CreatedUtc = message.CreatedUtc
            };
        }

        public static ContextNote CloneContextNote(ContextNote note)
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
