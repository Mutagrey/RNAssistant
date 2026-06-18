using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    public sealed class ContextService
    {
        private readonly IOfficeApplicationAdapter _adapter;

        public ContextService(IOfficeApplicationAdapter adapter)
        {
            _adapter = adapter;
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
                context.Host = session.Host ?? _adapter.HostName;
            }
            if (string.IsNullOrWhiteSpace(context.DocumentKey))
            {
                context.DocumentKey = session.DocumentKey ?? _adapter.DocumentKey;
            }
            if (string.IsNullOrWhiteSpace(context.Title))
            {
                context.Title = session.Title ?? _adapter.DocumentTitle;
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
                Host = _adapter.HostName,
                DocumentKey = _adapter.DocumentKey,
                Title = _adapter.DocumentTitle
            };
        }

        public void NormalizeContext(DocumentContext context, ChatSession session)
        {
            if (context == null || session == null)
            {
                return;
            }

            context.Host = string.IsNullOrWhiteSpace(session.Host) ? _adapter.HostName : session.Host;
            context.DocumentKey = string.IsNullOrWhiteSpace(session.DocumentKey) ? _adapter.DocumentKey : session.DocumentKey;
            context.Title = string.IsNullOrWhiteSpace(session.Title) ? _adapter.DocumentTitle : session.Title;
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
                note.Host = _adapter.HostName;
            }
            if (string.IsNullOrWhiteSpace(note.Kind))
            {
                note.Kind = string.Equals(mode, "reference", StringComparison.OrdinalIgnoreCase) ? "reference" : "selection";
            }
            if (string.IsNullOrWhiteSpace(note.Title))
            {
                note.Title = _adapter.DocumentTitle;
            }
            if (string.IsNullOrWhiteSpace(note.Reference))
            {
                note.Reference = note.Source ?? _adapter.DocumentTitle;
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
            existing.Title = note.Title;
            existing.Reference = note.Reference;
            existing.Source = note.Source;
            existing.Text = note.Text;
            existing.Preview = note.Preview;
            existing.DetailsJson = note.DetailsJson;
            existing.CreatedUtc = note.CreatedUtc == default(DateTime) ? DateTime.UtcNow : note.CreatedUtc;
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
                string.Equals(left.Kind, right.Kind, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(left.Reference, right.Reference, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(left.DetailsJson, right.DetailsJson, StringComparison.Ordinal);
        }
    }
}
