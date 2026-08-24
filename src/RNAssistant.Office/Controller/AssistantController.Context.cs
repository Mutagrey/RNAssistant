using System;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Office.Services;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        public DocumentContext GetContext(string chatId = null)
        {
            return ChatCloneService.CloneContext(LoadContext(LoadAddressedSession(chatId)));
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
            return WithReservedSession(LoadAddressedSession(chatId), session =>
            {
                var settings = ResolveChatSettings(session);
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
                return ChatCloneService.CloneContext(context);
            });
        }

        public DocumentContext AddSelectionContext(string mode, string chatId = null)
        {
            return WithReservedSession(LoadAddressedSession(chatId), session =>
            {
                var settings = ResolveChatSettings(session);
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
                return ChatCloneService.CloneContext(context);
            });
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
            return WithReservedSession(LoadAddressedSession(chatId), session =>
            {
                var context = LoadContext(session);
                if (context.Notes != null && !string.IsNullOrWhiteSpace(id))
                {
                    context.Notes.RemoveAll(n => n != null && string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase));
                    SaveSessionContext(session);
                }

                return ChatCloneService.CloneContext(context);
            });
        }

        public DocumentContext ClearContext(string chatId = null)
        {
            return WithReservedSession(LoadAddressedSession(chatId), session =>
            {
                session.Context = CreateEmptyContext();
                SaveSessionContext(session);
                return ChatCloneService.CloneContext(session.Context);
            });
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

        private string CaptureRuntimeDocumentKey()
        {
            try
            {
                return _adapter.RuntimeDocumentKey;
            }
            catch
            {
                return string.Empty;
            }
        }

        private string CaptureExpectedRuntimeDocumentKey(ChatSession session)
        {
            EnsureCurrentDocument(session);
            var runtimeDocumentKey = CaptureRuntimeDocumentKey();
            EnsureCurrentDocument(session);
            return runtimeDocumentKey;
        }
    }
}
