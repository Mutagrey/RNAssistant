using System;
using Newtonsoft.Json;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Office.Services;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        public DocumentContext GetContext(string chatId = null)
        {
            return LoadContext(LoadSession(chatId, true));
        }

        public DocumentContext AddSelectionContextFromBridge(string mode, string chatId = null)
        {
            return AddSelectionContext(mode, chatId);
        }

        public DocumentContext AddTextContext(string kind, string title, string reference, string text, string detailsJson, string chatId = null)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException("Context text is empty.", "text");
            }
            var settings = _settingsService.Load();
            var session = LoadSession(chatId, true);
            var context = AddContextNote(session, new ContextNote
            {
                Host = _adapter.HostName,
                Kind = string.IsNullOrWhiteSpace(kind) ? "context" : kind.Trim(),
                Title = string.IsNullOrWhiteSpace(title) ? "Context" : title.Trim(),
                Reference = string.IsNullOrWhiteSpace(reference) ? title : reference.Trim(),
                Source = string.IsNullOrWhiteSpace(reference) ? title : reference.Trim(),
                Text = ContextNormalizer.TrimForContext(text ?? string.Empty, ModelContextBudget.InputBudgetTokens(settings) * 3),
                Preview = ContextNormalizer.TrimForContext(text ?? string.Empty, 360),
                DetailsJson = detailsJson
            }, kind);
            return context;
        }

        public DocumentContext AddVbaContext(string chatId = null, int maxChars = 0)
        {
            var settings = _settingsService.Load();
            var session = LoadSession(chatId, true);
            EnsureCurrentDocument(session);
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
                Text = ContextNormalizer.TrimForContext(text, Math.Max(1000, limit)),
                Preview = "VBA project attached for this chat. Use VBA tools to patch modules.",
                DetailsJson = JsonConvert.SerializeObject(new
                {
                    type = "vba_project",
                    patchTool = _toolExecutor.VbaToolId("vba_apply_patch"),
                    replaceModuleTool = _toolExecutor.VbaToolId("vba_replace_module")
                })
            }, "vba_project");
            return context;
        }

        public DocumentContext AddSelectionContext(string mode, string chatId = null)
        {
            var settings = _settingsService.Load();
            var session = LoadSession(chatId, true);
            EnsureCurrentDocument(session);
            var context = LoadContext(session);
            try
            {
                _adapter.PrepareForContextCapture();
            }
            catch
            {
            }
            var note = _adapter.CaptureSelectionContext(mode, ModelContextBudget.InputBudgetTokens(settings) * 3);
            if (note == null)
            {
                throw new InvalidOperationException("No selectable Office context was found.");
            }

            _contextService.NormalizeContextNote(note, mode);
            ContextNormalizer.UpsertContextNote(context, note);
            SaveSessionContext(session);
            return context;
        }

        private DocumentContext AddContextNote(ChatSession session, ContextNote note, string mode)
        {
            var context = LoadContext(session);
            _contextService.NormalizeContextNote(note, mode);
            ContextNormalizer.UpsertContextNote(context, note);
            SaveSessionContext(session);
            return context;
        }

        public DocumentContext RemoveContextItem(string id, string chatId = null)
        {
            var session = LoadSession(chatId, true);
            var context = LoadContext(session);
            if (context.Notes != null && !string.IsNullOrWhiteSpace(id))
            {
                context.Notes.RemoveAll(n => n != null && string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase));
                SaveSessionContext(session);
            }

            return context;
        }

        public DocumentContext ClearContext(string chatId = null)
        {
            var session = LoadSession(chatId, true);
            session.Context = CreateEmptyContext();
            SaveSessionContext(session);
            return session.Context;
        }

        private DocumentContext LoadContext(ChatSession session)
        {
            if (session == null)
            {
                session = LoadSession(null);
            }
            return _contextService.LoadContext(session);
        }

        private DocumentContext CreateEmptyContext()
        {
            return _contextService.CreateEmptyContext();
        }

        private void SaveSessionContext(ChatSession session)
        {
            _contextService.NormalizeContext(LoadContext(session), session);
            SaveSessionChanges(session);
        }

        private void NormalizeContext(DocumentContext context, ChatSession session)
        {
            _contextService.NormalizeContext(context, session);
        }

        private void EnsureCurrentDocument(ChatSession session)
        {
            if (!_chatSessions.IsCurrentDocument(session))
            {
                throw new InvalidOperationException("Документ закрыт. Откройте файл, чтобы использовать Office context и инструменты.");
            }
        }
    }
}
