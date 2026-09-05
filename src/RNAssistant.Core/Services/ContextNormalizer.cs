using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public sealed class ContextNormalizer
    {
        private readonly string _host;
        private readonly string _documentKey;
        private readonly string _title;

        public ContextNormalizer(string host, string documentKey, string title)
        {
            _host = host ?? string.Empty;
            _documentKey = documentKey ?? string.Empty;
            _title = title ?? string.Empty;
        }

        public DocumentContext LoadContext(ChatSession session)
        {
            if (session.Context == null)
            {
                session.Context = CreateEmptyContext();
            }

            var context = session.Context;
            if (string.IsNullOrWhiteSpace(context.Host))
            {
                context.Host = FirstNonEmpty(session.Host, _host);
            }
            if (string.IsNullOrWhiteSpace(context.DocumentKey))
            {
                context.DocumentKey = FirstNonEmpty(session.DocumentKey, _documentKey);
            }
            if (string.IsNullOrWhiteSpace(context.Title))
            {
                context.Title = FirstNonEmpty(session.Title, _title);
            }
            if (context.Notes == null)
            {
                context.Notes = new List<ContextNote>();
            }
            return context;
        }

        public DocumentContext CreateEmptyContext()
        {
            return new DocumentContext
            {
                Host = _host,
                DocumentKey = _documentKey,
                Title = _title
            };
        }

        public void NormalizeContext(DocumentContext context, ChatSession session)
        {
            if (context == null || session == null)
            {
                return;
            }

            context.Host = FirstNonEmpty(session.Host, _host);
            context.DocumentKey = FirstNonEmpty(session.DocumentKey, _documentKey);
            context.Title = FirstNonEmpty(session.Title, _title);
            context.UpdatedUtc = DateTime.UtcNow;
            if (context.Notes == null)
            {
                context.Notes = new List<ContextNote>();
            }
            foreach (var note in context.Notes)
            {
                if (note != null)
                {
                    NormalizeContextNote(note, note.Kind);
                }
            }
        }

        public void NormalizeContextNote(ContextNote note, string mode)
        {
            if (string.IsNullOrWhiteSpace(note.Id))
            {
                note.Id = Guid.NewGuid().ToString("N");
            }
            if (note.CreatedUtc == default(DateTime))
            {
                note.CreatedUtc = DateTime.UtcNow;
            }
            if (string.IsNullOrWhiteSpace(note.Host))
            {
                note.Host = _host;
            }
            if (string.IsNullOrWhiteSpace(note.Kind))
            {
                note.Kind = string.Equals(mode, "reference", StringComparison.OrdinalIgnoreCase) ? "reference" : "selection";
            }
            if (string.IsNullOrWhiteSpace(note.Title))
            {
                note.Title = _title;
            }
            if (string.IsNullOrWhiteSpace(note.Reference))
            {
                note.Reference = note.Source ?? _title;
            }
            if (string.IsNullOrWhiteSpace(note.Source))
            {
                note.Source = note.Reference;
            }
            if (string.IsNullOrWhiteSpace(note.Preview))
            {
                note.Preview = TrimForContext(note.Text, 360);
            }
            if (string.IsNullOrWhiteSpace(note.Text))
            {
                note.Text = note.Preview;
            }
        }

        public static void UpsertContextNote(DocumentContext context, ContextNote note)
        {
            if (context == null || note == null)
            {
                return;
            }

            if (context.Notes == null)
            {
                context.Notes = new List<ContextNote>();
            }

            var existing = context.Notes.FirstOrDefault(item => IsSameContextNote(item, note));
            if (existing == null)
            {
                context.Notes.Add(note);
                return;
            }

            existing.Host = note.Host;
            existing.Kind = note.Kind;
            existing.Role = note.Role;
            existing.Title = note.Title;
            existing.Reference = note.Reference;
            existing.Source = note.Source;
            existing.Text = note.Text;
            existing.Preview = note.Preview;
            existing.DetailsJson = note.DetailsJson;
            existing.Evidence = note.Evidence;
            existing.InstructionPayload = note.InstructionPayload;
        }

        public static string TrimForContext(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            {
                return text ?? string.Empty;
            }

            return text.Substring(0, maxChars) + "\n...[truncated]";
        }

        private static bool IsSameContextNote(ContextNote left, ContextNote right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            return string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
                left.Role == right.Role &&
                string.Equals(left.Kind, right.Kind, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(left.Reference, right.Reference, StringComparison.OrdinalIgnoreCase);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values ?? new string[0])
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }
    }
}
