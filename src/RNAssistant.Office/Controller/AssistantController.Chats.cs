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
            return new ChatStateResponse { ActiveChatId = activeId, ActiveChatModel = session.Model, ActiveChatHtmlMode = session.HtmlModeEnabled, Chats = _chatSessions.GetChatSummaries(activeId), Context = LoadContext(session), Messages = session.Messages, ContextUsage = ContextUsageEstimator.FromSession(session, _settingsService.Load()), HtmlWorkspace = HtmlArtifactToolExecutor.NormalizeWorkspace(session.HtmlWorkspace) };
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

            var fork = _chatStore.Create(source.Host, source.DocumentKey, source.DocumentTitle, ChatSessionService.BuildForkTitle(source));
            fork.Model = source.Model;
            fork.HtmlModeEnabled = source.HtmlModeEnabled;
            fork.Context = ChatCloneService.CloneContext(LoadContext(source)) ?? CreateEmptyContext();
            fork.HtmlWorkspace = ChatCloneService.CloneHtmlWorkspace(source.HtmlWorkspace);
            fork.Messages = targetIndex < 0
                ? new List<ChatMessage>()
                : ChatCloneService.CloneMessages(sourceMessages.Take(targetIndex + 1));
            NormalizeContext(fork.Context, fork);
            _chatStore.Save(fork);
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
            _chatStore.Save(session);
            return ChatState(session);
        }

        public ChatStateResponse ListChats()
        {
            var session = LoadSession(null);
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

        public ChatStateResponse SetChatHtmlMode(string chatId, bool enabled)
        {
            var session = LoadSession(chatId);
            session.HtmlModeEnabled = enabled;
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
            var next = _chatSessions.DeleteAndSelectNext(ChatStore.GetSessionId(current));
            return ChatState(next);
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
                ActiveChatHtmlMode = session != null && session.HtmlModeEnabled,
                Chats = _chatSessions.GetChatSummaries(activeId),
                Context = session == null ? CreateEmptyContext() : LoadContext(session),
                Messages = session == null ? new List<ChatMessage>() : session.Messages,
                ContextUsage = ContextUsageEstimator.FromSession(session, _settingsService.Load()),
                HtmlWorkspace = session == null ? new HtmlWorkspace() : HtmlArtifactToolExecutor.NormalizeWorkspace(session.HtmlWorkspace)
            };
        }

    }
}
