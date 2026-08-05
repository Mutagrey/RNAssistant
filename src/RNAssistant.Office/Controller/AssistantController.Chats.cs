using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
            using (ReserveChatOperation(session))
            {
                var targetIndex = -1;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    targetIndex = session.Messages.FindIndex(message =>
                        message != null && string.Equals(message.Id, id, StringComparison.OrdinalIgnoreCase));
                    if (targetIndex < 0)
                    {
                        throw new InvalidOperationException("Message was not found.");
                    }
                }
                else if (index >= 0 && index < session.Messages.Count)
                {
                    targetIndex = index;
                }

                if (targetIndex < 0)
                {
                    throw new InvalidOperationException("Message was not found.");
                }

                var removedMessage = session.Messages[targetIndex];
                session.Messages.RemoveAt(targetIndex);
                _attachmentStore.DeleteMessage(removedMessage);
                RemovePendingAgentToolsForSession(session.Id);
                CancelPendingActivities(session, "Pending action cancelled because chat history changed.");
                SaveSessionChanges(session);
            }

            return ChatState(session);
        }

        public ChatStateResponse ForkChat(string id, int index, string chatId = null)
        {
            var source = LoadSession(chatId);
            ChatSession fork;
            using (ReserveChatOperation(source))
            {
                var sourceMessages = source.Messages ?? new List<ChatMessage>();
                var targetIndex = -1;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    targetIndex = sourceMessages.FindIndex(message =>
                        message != null && string.Equals(message.Id, id, StringComparison.OrdinalIgnoreCase));
                    if (targetIndex < 0)
                    {
                        throw new InvalidOperationException("Message was not found.");
                    }
                }
                else if (index >= 0 && index < sourceMessages.Count)
                {
                    targetIndex = index;
                }
                else
                {
                    targetIndex = sourceMessages.Count - 1;
                }

                fork = _chatStore.CreateTransient(source.Host, source.DocumentKey, source.DocumentTitle, ChatSessionService.BuildForkTitle(source));
                fork.Model = source.Model;
                fork.Mode = ChatModes.Normalize(source.Mode);
                fork.HtmlModeEnabled = source.HtmlModeEnabled;
                fork.Context = ChatCloneService.CloneContext(LoadContext(source)) ?? CreateEmptyContext();
                fork.HtmlWorkspace = ChatCloneService.CloneWorkspaceForFork(source.HtmlWorkspace);
                fork.Messages = targetIndex < 0
                    ? new List<ChatMessage>()
                    : ChatCloneService.CloneMessages(sourceMessages.Take(targetIndex + 1));
                foreach (var message in fork.Messages)
                {
                    _attachmentStore.CloneMessageAttachments(fork.Id, message);
                }
                NormalizeContext(fork.Context, fork);
                SaveSessionChanges(fork);
                _chatSessions.SetActiveSession(fork);
            }

            return ChatState(fork);
        }

        public async Task<ChatStateResponse> EditMessageAsync(
            string text,
            string id,
            int index,
            string chatId = null,
            Action<string, string, ChatActivity> progress = null,
            Action<ChatStateResponse> chatStateChanged = null,
            CancellationToken cancellationToken = default(CancellationToken),
            string runId = null)
        {
            var session = LoadAddressedSession(chatId);
            var sessionId = session.Id;
            return await ExecuteChatTurnAsync(
                session,
                _settingsService.Load(),
                null,
                currentSession =>
                {
                    var edit = _chatHistoryEditService.RewriteUserMessage(currentSession, sessionId, id, index, text);
                    return new ChatTurnInput
                    {
                        Text = edit.Message == null ? string.Empty : edit.Message.Content,
                        Attachments = edit.Message == null
                            ? (IReadOnlyList<ChatAttachment>)new ChatAttachment[0]
                            : edit.Message.Attachments,
                        AppendUserMessage = false,
                        CommitUserAttachments = false
                    };
                },
                progress,
                chatStateChanged,
                cancellationToken,
                runId).ConfigureAwait(false);
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

        public ChatStateResponse CreateDocumentChat(string title, string host, string documentKey, string documentTitle, string documentPath)
        {
            var session = _chatSessions.CreateChatForDocument(title, host, documentKey, documentTitle, documentPath);
            return ChatState(session);
        }

        public ChatStateResponse SelectChat(string chatId)
        {
            var session = LoadSession(chatId);
            _chatSessions.SetActiveSession(session);
            return ChatState(session);
        }

        public OpenDocumentResponse OpenDocument(string chatId)
        {
            var session = LoadSession(chatId);
            var catalog = _adapter as IOfficeDocumentCatalog;
            var sameHost = string.Equals(session.Host, _adapter.HostName, StringComparison.OrdinalIgnoreCase);
            if (sameHost && catalog != null && catalog.ActivateDocument(session.DocumentKey))
            {
                return new OpenDocumentResponse { Path = _chatSessions.GetDocumentPath(session), Launched = false };
            }
            if (_chatSessions.IsCurrentDocument(session))
            {
                return new OpenDocumentResponse { Path = _chatSessions.GetDocumentPath(session), Launched = false };
            }

            var path = _chatSessions.GetDocumentPath(session);
            if (sameHost && catalog != null && catalog.OpenDocument(path))
            {
                return new OpenDocumentResponse { Path = path, Launched = true };
            }
            DocumentOpenService.Open(path);
            return new OpenDocumentResponse { Path = path, Launched = true };
        }

        public ChatStateResponse RenameChat(string chatId, string title)
        {
            lock (_syncRoot)
            {
                var session = LoadSession(chatId);
                if (!string.IsNullOrWhiteSpace(title))
                {
                    session.Title = title.Trim();
                    SaveSessionChanges(session);
                }

                return ChatState(session);
            }
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
            RemovePendingAgentToolsForSession(session.Id);
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
            using (ReserveChatOperation(session))
            {
                var sessionId = session.Id;
                _attachmentStore.DeleteSession(sessionId);
                RemovePendingAgentToolsForSession(sessionId);
                session.Messages.Clear();
                session.Context = CreateEmptyContext();
                session.HtmlWorkspace = new HtmlWorkspace();
                NormalizeContext(session.Context, session);
                SaveSessionChanges(session);
            }

            return ChatState(session);
        }

        public ChatStateResponse DeleteChat(string chatId)
        {
            var current = LoadSession(chatId);
            ChatSession next;
            using (ReserveChatOperation(current))
            {
                var sessionId = current.Id;
                _attachmentStore.DeleteSession(sessionId);
                RemovePendingAgentToolsForSession(sessionId);
                next = _chatSessions.DeleteAndSelectNext(sessionId);
            }

            return ChatState(next);
        }

        public ChatStateResponse DeleteDocument(string host, string documentKey)
        {
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(documentKey))
            {
                throw new InvalidOperationException("Документ не указан.");
            }
            if (_chatRuns.IsDocumentRunning(host, documentKey))
            {
                throw new InvalidOperationException("Сначала остановите запросы в чатах этого документа.");
            }

            var sessions = _chatStore.List(host, documentKey, string.Empty);
            foreach (var session in sessions)
            {
                _attachmentStore.DeleteSession(session.Id);
                RemovePendingAgentToolsForSession(session.Id);
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

        private ChatSession LoadAddressedSession(string requestedSessionId)
        {
            return _chatSessions.LoadAddressedSession(requestedSessionId);
        }

        private ChatRunLease ReserveChatOperation(ChatSession session)
        {
            var sessionId = session.Id;
            return _chatRuns.Start(sessionId, Guid.NewGuid().ToString("N"), session);
        }

        private ChatStateResponse ChatState(ChatSession session)
        {
            var activeId = session.Id;
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
            _chatSessions.NotifySaved(session);
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
