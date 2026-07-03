using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        public ChatStateResponse DeleteMessage(string id, int index, string chatId = null)
        {
            var session = LoadSession(chatId);
            var removed = false;
            ChatMessage removedMessage = null;
            if (!string.IsNullOrWhiteSpace(id))
            {
                removedMessage = session.Messages.FirstOrDefault(m => m != null && string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
                removed = removedMessage != null && session.Messages.Remove(removedMessage);
            }

            if (!removed && index >= 0 && index < session.Messages.Count)
            {
                removedMessage = session.Messages[index];
                session.Messages.RemoveAt(index);
                removed = true;
            }

            if (removed)
            {
                _attachmentStore.DeleteMessage(removedMessage);
                RemovePendingAgentToolsForSession(ChatStore.GetSessionId(session));
                CancelPendingActivities(session, "Pending action cancelled because chat history changed.");
                SaveSessionChanges(session);
            }

            var activeId = ChatStore.GetSessionId(session);
            return new ChatStateResponse { ActiveChatId = activeId, ActiveChatModel = session.Model, ActiveChatMode = ChatModes.Normalize(session.Mode), ActiveChatHtmlMode = session.HtmlModeEnabled, Chats = _chatSessions.GetChatSummaries(activeId), Documents = ListOpenDocuments(), Context = LoadContext(session), Messages = session.Messages, ContextUsage = ContextUsageEstimator.FromSession(session, _settingsService.Load()), HtmlWorkspace = HtmlArtifactToolExecutor.NormalizeWorkspace(session.HtmlWorkspace) };
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

            var fork = _chatStore.CreateTransient(source.Host, source.DocumentKey, source.DocumentTitle, ChatSessionService.BuildForkTitle(source));
            fork.Model = source.Model;
            fork.Mode = ChatModes.Normalize(source.Mode);
            fork.HtmlModeEnabled = source.HtmlModeEnabled;
            fork.Context = ChatCloneService.CloneContext(LoadContext(source)) ?? CreateEmptyContext();
            fork.HtmlWorkspace = ChatCloneService.CloneHtmlWorkspace(source.HtmlWorkspace);
            fork.Messages = targetIndex < 0
                ? new List<ChatMessage>()
                : ChatCloneService.CloneMessages(sourceMessages.Take(targetIndex + 1));
            foreach (var message in fork.Messages)
            {
                _attachmentStore.CloneMessageAttachments(ChatStore.GetSessionId(fork), message);
            }
            NormalizeContext(fork.Context, fork);
            SaveSessionChanges(fork);
            _chatSessions.SetActiveSession(fork);
            return ChatState(fork);
        }

        public ChatStateResponse UpdateMessageActivityData(string messageId, string dataJson, string chatId = null)
        {
            if (string.IsNullOrWhiteSpace(messageId))
            {
                throw new InvalidOperationException("messageId is required.");
            }

            if ((dataJson ?? string.Empty).Length > 2000000)
            {
                throw new InvalidOperationException("Chart artifact is too large.");
            }

            var parsed = JObject.Parse(dataJson ?? string.Empty);
            var type = (string)parsed["Type"] ?? (string)parsed["type"];
            if (!string.Equals(type, "rnassistant.chart", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Only rnassistant.chart activity data can be updated.");
            }

            var session = LoadSession(chatId);
            var message = (session.Messages ?? new List<ChatMessage>()).FirstOrDefault(m =>
                m != null && string.Equals(m.Id, messageId, StringComparison.OrdinalIgnoreCase));
            if (message == null || message.Activity == null)
            {
                throw new InvalidOperationException("Message activity was not found.");
            }

            message.Activity.DataJson = parsed.ToString(Formatting.None);
            SaveSessionChanges(session);
            return ChatState(session);
        }

        public ChatStateResponse ListChats()
        {
            var session = _chatSessions.GetActiveSession() ?? LoadSession(null);
            return ChatState(session);
        }

        public ChatStateResponse CreateChat(string title)
        {
            var session = _chatSessions.CreateChat(title);
            return ChatState(session);
        }

        public ChatStateResponse SelectChat(string chatId)
        {
            var session = LoadSession(chatId);
            return ChatState(session);
        }

        public OpenDocumentResponse OpenDocument(string chatId)
        {
            var session = LoadSession(chatId);
            var catalog = _adapter as IOfficeDocumentCatalog;
            if (catalog != null && catalog.ActivateDocument(session.DocumentKey))
            {
                return new OpenDocumentResponse { Path = _chatSessions.GetDocumentPath(session), Launched = false };
            }
            if (_chatSessions.IsCurrentDocument(session))
            {
                return new OpenDocumentResponse { Path = _chatSessions.GetDocumentPath(session), Launched = false };
            }

            var path = _chatSessions.GetDocumentPath(session);
            DocumentOpenService.Open(path);
            return new OpenDocumentResponse { Path = path, Launched = true };
        }

        public ChatStateResponse RenameChat(string chatId, string title)
        {
            var session = LoadSession(chatId);
            if (!string.IsNullOrWhiteSpace(title))
            {
                session.Title = title.Trim();
                SaveSessionChanges(session);
            }

            return ChatState(session);
        }

        public ChatStateResponse SetChatModel(string chatId, string model)
        {
            var session = LoadSession(chatId);
            session.Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
            SaveSessionChanges(session);
            return ChatState(session);
        }

        public ChatStateResponse SetChatMode(string chatId, string mode)
        {
            var session = LoadSession(chatId);
            session.Mode = ChatModes.Normalize(mode);
            RemovePendingAgentToolsForSession(ChatStore.GetSessionId(session));
            CancelPendingActivities(session, "Pending action cancelled because chat mode changed.");
            SaveSessionChanges(session);
            return ChatState(session);
        }

        public ChatStateResponse SetChatHtmlMode(string chatId, bool enabled)
        {
            var session = LoadSession(chatId);
            session.HtmlModeEnabled = enabled;
            SaveSessionChanges(session);
            return ChatState(session);
        }

        public ChatStateResponse ClearChat(string chatId)
        {
            var session = LoadSession(chatId);
            var sessionId = ChatStore.GetSessionId(session);
            _attachmentStore.DeleteSession(sessionId);
            RemovePendingAgentToolsForSession(sessionId);
            session.Messages.Clear();
            session.Context = CreateEmptyContext();
            session.HtmlWorkspace = new HtmlWorkspace();
            NormalizeContext(session.Context, session);
            SaveSessionChanges(session);
            return ChatState(session);
        }

        public ChatStateResponse DeleteChat(string chatId)
        {
            var current = LoadSession(chatId);
            var sessionId = ChatStore.GetSessionId(current);
            _attachmentStore.DeleteSession(sessionId);
            RemovePendingAgentToolsForSession(sessionId);
            var next = _chatSessions.DeleteAndSelectNext(sessionId);
            return ChatState(next);
        }

        public ChatStateResponse DeleteDocument(string host, string documentKey)
        {
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(documentKey))
            {
                throw new InvalidOperationException("Документ не указан.");
            }

            var sessions = _chatStore.List(host, documentKey, string.Empty);
            foreach (var session in sessions)
            {
                _attachmentStore.DeleteSession(ChatStore.GetSessionId(session));
                RemovePendingAgentToolsForSession(ChatStore.GetSessionId(session));
            }

            _chatStore.DeleteDocument(host, documentKey);
            _chatSessions.Reset();
            return ChatState(LoadSession(null));
        }

        private ChatSession LoadSession(string requestedSessionId)
        {
            return LoadSession(requestedSessionId, false);
        }

        private ChatSession LoadSession(string requestedSessionId, bool allowMissingRequestedFallback)
        {
            return _chatSessions.LoadSession(requestedSessionId, allowMissingRequestedFallback);
        }

        private ChatStateResponse ChatState(ChatSession session)
        {
            var activeId = ChatStore.GetSessionId(session);
            return new ChatStateResponse
            {
                ActiveChatId = activeId,
                ActiveChatModel = session == null ? string.Empty : session.Model,
                ActiveChatMode = ChatModes.Normalize(session == null ? null : session.Mode),
                ActiveChatHtmlMode = session != null && session.HtmlModeEnabled,
                Chats = _chatSessions.GetChatSummaries(activeId),
                Documents = ListOpenDocuments(),
                Context = session == null ? CreateEmptyContext() : LoadContext(session),
                Messages = session == null ? new List<ChatMessage>() : session.Messages,
                ContextUsage = ContextUsageEstimator.FromSession(session, _settingsService.Load()),
                HtmlWorkspace = session == null ? new HtmlWorkspace() : HtmlArtifactToolExecutor.NormalizeWorkspace(session.HtmlWorkspace)
            };
        }

        private void SaveSessionChanges(ChatSession session)
        {
            if (session == null || (!_chatStore.IsPersisted(session) && !HasCompletedExchange(session)))
            {
                return;
            }

            _chatStore.Save(session);
            _chatSessions.SetActiveSession(session);
        }

        private static bool HasCompletedExchange(ChatSession session)
        {
            var messages = session == null ? null : session.Messages;
            return messages != null &&
                messages.Any(message => message != null && string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)) &&
                messages.Any(message => message != null && string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase));
        }

    }
}
