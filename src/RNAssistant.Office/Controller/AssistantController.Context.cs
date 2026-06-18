using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        public string GetContextJson(string chatId = null)
        {
            return JsonConvert.SerializeObject(LoadContext(LoadSession(chatId)));
        }

        public string AddSelectionContextJson(string mode, string chatId = null)
        {
            return JsonConvert.SerializeObject(AddSelectionContext(mode, chatId));
        }

        public string AddTextContextJson(string kind, string title, string reference, string text, string detailsJson, string chatId = null)
        {
            var settings = _settingsService.Load();
            var session = LoadSession(chatId);
            var context = AddContextNote(session, new ContextNote
            {
                Host = _adapter.HostName,
                Kind = string.IsNullOrWhiteSpace(kind) ? "context" : kind.Trim(),
                Title = string.IsNullOrWhiteSpace(title) ? "Context" : title.Trim(),
                Reference = string.IsNullOrWhiteSpace(reference) ? title : reference.Trim(),
                Source = string.IsNullOrWhiteSpace(reference) ? title : reference.Trim(),
                Text = TrimForContext(text ?? string.Empty, Math.Max(1000, settings.ContextCharLimit)),
                Preview = TrimForContext(text ?? string.Empty, 360),
                DetailsJson = detailsJson
            }, kind);
            return JsonConvert.SerializeObject(context);
        }

        public string AddVbaContextJson(string chatId = null, int maxChars = 0)
        {
            var settings = _settingsService.Load();
            var session = LoadSession(chatId);
            var limit = maxChars <= 0 ? settings.VbaContextCharLimit : maxChars;
            var snapshot = _adapter.GetVbaSnapshot(Math.Max(1000, limit));
            if (string.IsNullOrWhiteSpace(snapshot) ||
                snapshot.IndexOf("VBA project could not be read", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(snapshot) ? "VBA project is empty or unavailable." : snapshot);
            }

            var text =
                "Attached VBA project snapshot for this chat. Modules are separated by lines like ===== ModuleName (Type) =====.\n" +
                "When editing VBA, use `" + _toolExecutor.VbaToolId("vba_apply_patch") + "` for targeted changes and avoid whole-module replacement unless necessary.\n\n" +
                snapshot;
            var context = AddContextNote(session, new ContextNote
            {
                Host = _adapter.HostName,
                Kind = "vba_project",
                Title = "VBA project",
                Reference = "vba:project",
                Source = _adapter.DocumentTitle,
                Text = TrimForContext(text, Math.Max(1000, limit)),
                Preview = "VBA project attached for this chat. Use VBA tools to patch modules.",
                DetailsJson = JsonConvert.SerializeObject(new
                {
                    type = "vba_project",
                    patchTool = _toolExecutor.VbaToolId("vba_apply_patch"),
                    replaceModuleTool = _toolExecutor.VbaToolId("vba_replace_module")
                })
            }, "vba_project");
            return JsonConvert.SerializeObject(context);
        }

        public DocumentContext AddSelectionContext(string mode, string chatId = null)
        {
            var settings = _settingsService.Load();
            var session = LoadSession(chatId);
            var context = LoadContext(session);
            if (context.Notes == null)
            {
                context.Notes = new List<ContextNote>();
            }
            try
            {
                _adapter.PrepareForContextCapture();
            }
            catch
            {
            }
            var note = _adapter.CaptureSelectionContext(mode, Math.Min(Math.Max(1000, settings.ContextCharLimit), 12000));
            if (note == null)
            {
                throw new InvalidOperationException("No selectable Office context was found.");
            }

            NormalizeContextNote(note, mode);
            UpsertContextNote(context, note);
            SaveSessionContext(session);
            return context;
        }

        private DocumentContext AddContextNote(ChatSession session, ContextNote note, string mode)
        {
            var context = LoadContext(session);
            if (context.Notes == null)
            {
                context.Notes = new List<ContextNote>();
            }

            NormalizeContextNote(note, mode);
            UpsertContextNote(context, note);
            SaveSessionContext(session);
            return context;
        }

        public string RemoveContextItemJson(string id, string chatId = null)
        {
            var session = LoadSession(chatId);
            var context = LoadContext(session);
            if (context.Notes != null && !string.IsNullOrWhiteSpace(id))
            {
                context.Notes.RemoveAll(n => n != null && string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase));
                SaveSessionContext(session);
            }

            return JsonConvert.SerializeObject(context);
        }

        public string ClearContextJson(string chatId = null)
        {
            var session = LoadSession(chatId);
            session.Context = CreateEmptyContext();
            SaveSessionContext(session);
            return JsonConvert.SerializeObject(session.Context);
        }

        private DocumentContext LoadContext(ChatSession session)
        {
            if (session == null)
            {
                session = LoadSession(null);
            }

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

        private DocumentContext CreateEmptyContext()
        {
            return new DocumentContext
            {
                Host = _adapter.HostName,
                DocumentKey = _adapter.DocumentKey,
                Title = _adapter.DocumentTitle
            };
        }

        private void SaveSessionContext(ChatSession session)
        {
            NormalizeContext(LoadContext(session), session);
            _chatStore.Save(session);
        }

        private void NormalizeContext(DocumentContext context, ChatSession session)
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

        private static void UpsertContextNote(DocumentContext context, ContextNote note)
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

        private void NormalizeContextNote(ContextNote note, string mode)
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

        private static string TrimForContext(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            {
                return text ?? string.Empty;
            }

            return text.Substring(0, maxChars) + "\n...[truncated]";
        }
    }
}
