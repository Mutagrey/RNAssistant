using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        public ChatStateResponse DeleteMessage(string id, int index, string chatId = null)
        {
            var session = LoadSession(chatId);
            var removed = false;
            if (!string.IsNullOrWhiteSpace(id))
            {
                removed = session.Messages.RemoveAll(m => m != null && string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
            }

            if (!removed && index >= 0 && index < session.Messages.Count)
            {
                session.Messages.RemoveAt(index);
                removed = true;
            }

            if (removed)
            {
                _chatStore.Save(session);
            }

            var activeId = ChatStore.GetSessionId(session);
            return new ChatStateResponse { ActiveChatId = activeId, ActiveChatModel = session.Model, Chats = GetChatSummaries(activeId), Context = LoadContext(session), Messages = session.Messages, ContextUsage = ContextUsageEstimator.FromSession(session, _settingsService.Load()) };
        }

        public ChatStateResponse ForkChat(string id, int index, string chatId = null)
        {
            var source = LoadSession(chatId);
            var sourceMessages = source.Messages ?? new List<ChatMessage>();
            var targetIndex = -1;
            if (!string.IsNullOrWhiteSpace(id))
            {
                targetIndex = sourceMessages.FindIndex(m => m != null && string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
            }
            if (targetIndex < 0 && index >= 0 && index < sourceMessages.Count)
            {
                targetIndex = index;
            }
            if (targetIndex < 0)
            {
                targetIndex = sourceMessages.Count - 1;
            }

            var fork = _chatStore.Create(source.Host, source.DocumentKey, source.DocumentTitle, BuildForkTitle(source));
            fork.Model = source.Model;
            fork.Context = ChatCloneService.CloneContext(LoadContext(source)) ?? CreateEmptyContext();
            fork.Messages = targetIndex < 0
                ? new List<ChatMessage>()
                : ChatCloneService.CloneMessages(sourceMessages.Take(targetIndex + 1));
            NormalizeContext(fork.Context, fork);
            _chatStore.Save(fork);
            SetActiveSession(fork);
            return ChatState(fork);
        }

        public ChatStateResponse ListChats()
        {
            var session = LoadSession(null);
            return ChatState(session);
        }

        public ChatStateResponse CreateChat(string title)
        {
            LoadSession(null);
            var session = _chatStore.Create(_adapter.HostName, _adapter.DocumentKey, _adapter.DocumentTitle, string.IsNullOrWhiteSpace(title) ? "New chat" : title.Trim());
            SetActiveSession(session);
            return ChatState(session);
        }

        public ChatStateResponse SelectChat(string chatId)
        {
            var session = LoadSession(chatId);
            return ChatState(session);
        }

        public ChatStateResponse RenameChat(string chatId, string title)
        {
            var session = LoadSession(chatId);
            if (!string.IsNullOrWhiteSpace(title))
            {
                session.Title = title.Trim();
                _chatStore.Save(session);
            }

            return ChatState(session);
        }

        public ChatStateResponse SetChatModel(string chatId, string model)
        {
            var session = LoadSession(chatId);
            session.Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
            _chatStore.Save(session);
            return ChatState(session);
        }

        public ChatStateResponse ClearChat(string chatId)
        {
            var session = LoadSession(chatId);
            session.Messages.Clear();
            _chatStore.Save(session);
            return ChatState(session);
        }

        public ChatStateResponse DeleteChat(string chatId)
        {
            var current = LoadSession(chatId);
            _chatStore.Delete(_adapter.HostName, _adapter.DocumentKey, ChatStore.GetSessionId(current));
            var next = _chatStore.List(_adapter.HostName, _adapter.DocumentKey, _adapter.DocumentTitle).FirstOrDefault();
            if (next == null)
            {
                next = _chatStore.Create(_adapter.HostName, _adapter.DocumentKey, _adapter.DocumentTitle, "New chat");
            }

            SetActiveSession(next);
            return ChatState(next);
        }

        private ChatSession LoadSession(string requestedSessionId)
        {
            var host = _adapter.HostName;
            var documentKey = _adapter.DocumentKey;
            var runtimeKey = _adapter.RuntimeDocumentKey;
            var title = _adapter.DocumentTitle;

            if (!string.IsNullOrWhiteSpace(_activeSessionId) &&
                string.Equals(_activeHost, host, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_activeRuntimeDocumentKey, runtimeKey, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(_activeDocumentKey, documentKey, StringComparison.OrdinalIgnoreCase))
            {
                var oldDocumentKey = _activeDocumentKey;
                _chatStore.MoveDocument(_activeHost, oldDocumentKey, host, documentKey, title);
                _activeHost = host;
                _activeDocumentKey = documentKey;
                _activeRuntimeDocumentKey = runtimeKey;
            }

            ChatSession session = null;
            if (!string.IsNullOrWhiteSpace(requestedSessionId))
            {
                session = _chatStore.Load(host, documentKey, requestedSessionId);
                if (session == null)
                {
                    throw new InvalidOperationException("Chat session was not found.");
                }
            }
            else if (!string.IsNullOrWhiteSpace(_activeSessionId) &&
                     string.Equals(_activeHost, host, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(_activeDocumentKey, documentKey, StringComparison.OrdinalIgnoreCase))
            {
                session = _chatStore.Load(host, documentKey, _activeSessionId);
            }

            if (session == null)
            {
                session = _chatStore.LoadOrCreateActive(host, documentKey, title);
            }

            SetActiveSession(session);
            return session;
        }

        private static string BuildForkTitle(ChatSession source)
        {
            var title = source == null || string.IsNullOrWhiteSpace(source.Title) ? "Chat" : source.Title.Trim();
            if (title.EndsWith(" fork", StringComparison.OrdinalIgnoreCase))
            {
                return title;
            }

            return (title.Length > 52 ? title.Substring(0, 52).TrimEnd() : title) + " fork";
        }

        private void SetActiveSession(ChatSession session)
        {
            if (session == null)
            {
                return;
            }

            _activeSessionId = ChatStore.GetSessionId(session);
            _activeHost = session.Host;
            _activeDocumentKey = session.DocumentKey;
            _activeRuntimeDocumentKey = _adapter.RuntimeDocumentKey;
            _chatStore.SaveActiveSessionId(session.Host, session.DocumentKey, _activeSessionId);
        }

        private ChatStateResponse ChatState(ChatSession session)
        {
            var activeId = ChatStore.GetSessionId(session);
            return new ChatStateResponse
            {
                ActiveChatId = activeId,
                ActiveChatModel = session == null ? string.Empty : session.Model,
                Chats = GetChatSummaries(activeId),
                Context = session == null ? CreateEmptyContext() : LoadContext(session),
                Messages = session == null ? new List<ChatMessage>() : session.Messages,
                ContextUsage = ContextUsageEstimator.FromSession(session, _settingsService.Load())
            };
        }

        private IReadOnlyList<ChatSessionSummary> GetChatSummaries(string activeId)
        {
            return _chatStore.List(_adapter.HostName, _adapter.DocumentKey, _adapter.DocumentTitle)
                .Select(s => new ChatSessionSummary
                {
                    Id = ChatStore.GetSessionId(s),
                    Host = s.Host,
                    DocumentKey = s.DocumentKey,
                    DocumentTitle = s.DocumentTitle,
                    Title = s.Title,
                    Model = s.Model,
                    CreatedUtc = s.CreatedUtc,
                    UpdatedUtc = s.UpdatedUtc,
                    MessageCount = s.Messages == null ? 0 : s.Messages.Count
                })
                .ToList();
        }
    }
}
