using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        public async Task<ChatStateResponse> CompactChatContextAsync(
            string chatId = null,
            Action<string, string, ChatActivity> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var session = LoadAddressedSession(chatId);
            using (ReserveChatOperation(session))
            {
                session = ReloadReservedSession(session);
                var settings = ResolveChatSettings(session);
                try
                {
                    await _contextCompactionService.EnsureWithinBudgetAsync(
                        session,
                        settings,
                        string.Empty,
                        true,
                        progress,
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    PersistTokenEstimateCalibration(settings);
                }
                SaveSessionChanges(session);
            }
            return ChatState(session);
        }

        public ChatStateResponse DeleteMessage(string id, int index, string chatId = null)
        {
            return WithReservedChatState(LoadSession(chatId), session =>
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

                var removedMessages = ChatHistoryEditService.SelectMessagesForDeletion(
                    session.Messages,
                    targetIndex);
                session.Messages.RemoveAll(message => removedMessages.Contains(message));
                RemovePendingAgentToolsForSession(session.Id);
                CancelPendingActivities(session, "Pending action cancelled because chat history changed.");
                session.LastRun = null;
                session.ContextCheckpoints = new List<ContextCheckpoint>();
                session.ActiveContextCheckpointId = null;
                ChatArtifactService.PruneUnreachable(session);
                SaveSessionChanges(session);
                foreach (var removedMessage in removedMessages) _attachmentStore.DeleteMessage(removedMessage);
            });
        }

        public ChatStateResponse ForkChat(string id, int index, string chatId = null)
        {
            var source = LoadSession(chatId);
            ChatSession fork;
            using (ReserveChatOperation(source))
            {
                source = ReloadReservedSession(source);
                if (HasPendingAgentConfirmation(source))
                {
                    throw new InvalidOperationException("Сначала подтвердите или отмените ожидающее действие агента.");
                }
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
                fork.ReasoningEnabled = source.ReasoningEnabled;
                fork.Context = ChatCloneService.CloneContext(LoadContext(source)) ?? CreateEmptyContext();
                fork.Messages = targetIndex < 0
                    ? new List<ChatMessage>()
                    : ChatCloneService.CloneMessages(sourceMessages.Take(targetIndex + 1));
                ChatHistoryEditService.ExcludeUnmatchedToolCalls(fork.Messages);
                _chatStore.LoadHtmlArtifactBodies(
                    source,
                    ChatArtifactService.ReachableForMessages(source.Artifacts, fork.Messages)
                        .Where(artifact => string.Equals(artifact.Kind, ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase))
                        .Select(artifact => artifact.Id));
                fork.Artifacts = ChatCloneService.CloneArtifactsForMessages(source.Artifacts, fork.Messages);
                fork.ContextCheckpoints = ChatCloneService.CloneContextCheckpoints(source.ContextCheckpoints, fork.Messages);
                fork.ActiveContextCheckpointId = fork.ContextCheckpoints.OrderByDescending(checkpoint => checkpoint.CreatedUtc).Select(checkpoint => checkpoint.Id).FirstOrDefault();
                var workspaceCheckpoint = HtmlWorkspaceArtifactService.CheckpointAtOrBefore(fork.Messages, fork.Messages.Count - 1);
                if (!string.IsNullOrWhiteSpace(workspaceCheckpoint) && HtmlWorkspaceArtifactService.Restore(fork, workspaceCheckpoint))
                {
                    fork.ActiveHtmlArtifactId = workspaceCheckpoint;
                }
                else
                {
                    fork.HtmlWorkspace = ChatCloneService.CloneWorkspaceForFork(source.HtmlWorkspace);
                    HtmlWorkspaceArtifactService.CaptureCurrent(fork, "Forked HTML workspace");
                }
                foreach (var message in fork.Messages)
                {
                    _attachmentStore.CloneMessageAttachments(fork.Id, message);
                }
                ChatArtifactService.LinkMessageArtifacts(fork, 0);
                ChatArtifactService.RestoreActivePlanFromMessages(fork);
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
                        CommitUserAttachments = false,
                        MessagesToDeleteAfterSave = edit.RemovedMessages
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

            return WithReservedChatState(LoadSession(chatId), session =>
            {
                var message = (session.Messages ?? new List<ChatMessage>()).FirstOrDefault(m =>
                    m != null && string.Equals(m.Id, messageId, StringComparison.OrdinalIgnoreCase));
                if (message == null || message.Activity == null)
                {
                    throw new InvalidOperationException("Message activity was not found.");
                }

                message.Activity.DataJson = parsed.ToString(Formatting.None);
                SaveSessionChanges(session);
            });
        }

        public ChatStateResponse ListChats()
        {
            var session = _chatSessions.GetActiveSessionForOfficeState();
            return ChatState(session);
        }

        public ChatStateResponse CreateChat(string title)
        {
            using (_chatRuns.ReserveMaintenance())
            {
                var session = _chatSessions.CreateChat(title);
                return ChatState(session);
            }
        }

        public ChatStateResponse CreateDocumentChat(string title, string host, string documentKey, string documentTitle, string documentPath)
        {
            using (_chatRuns.ReserveMaintenance())
            {
                var session = _chatSessions.CreateChatForDocument(title, host, documentKey, documentTitle, documentPath);
                return ChatState(session);
            }
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
            var path = _chatSessions.GetDocumentPath(session);
            if (sameHost && catalog != null && TryActivateDocument(catalog, session.DocumentKey, path))
            {
                session = LoadSession(session.Id);
                return new OpenDocumentResponse { Path = path, Launched = false, State = ChatState(session) };
            }
            if (_chatSessions.IsCurrentDocument(session))
            {
                return new OpenDocumentResponse { Path = path, Launched = false, State = ChatState(session) };
            }

            if (sameHost && catalog != null && catalog.OpenDocument(path))
            {
                session = LoadSession(session.Id);
                return new OpenDocumentResponse { Path = path, Launched = true, State = ChatState(session) };
            }
            DocumentOpenService.Open(path);
            return new OpenDocumentResponse { Path = path, Launched = true };
        }

        private bool TryActivateDocument(IOfficeDocumentCatalog catalog, string documentKey, string path)
        {
            if (catalog.ActivateDocument(documentKey))
            {
                return true;
            }

            var match = ListOpenDocuments().FirstOrDefault(document =>
                document != null &&
                string.Equals(document.Host, _adapter.HostName, StringComparison.OrdinalIgnoreCase) &&
                DocumentOpenService.SamePath(document.Path, path));
            return match != null && catalog.ActivateDocument(match.DocumentKey);
        }

        public ChatStateResponse RenameChat(string chatId, string title)
        {
            return WithReservedChatState(LoadSession(chatId), session =>
            {
                if (!string.IsNullOrWhiteSpace(title))
                {
                    session.Title = title.Trim();
                    SaveSessionChanges(session);
                }
            });
        }

        public ChatStateResponse SetChatModel(string chatId, string model)
        {
            return WithReservedChatState(LoadSession(chatId), session =>
            {
                session.Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
                SaveSessionChanges(session);
            });
        }

        public ChatStateResponse SetChatMode(string chatId, string mode)
        {
            return WithReservedChatState(LoadSession(chatId), session =>
            {
                session.Mode = ChatModes.Normalize(mode);
                RemovePendingAgentToolsForSession(session.Id);
                CancelPendingActivities(session, "Pending action cancelled because chat mode changed.");
                session.LastRun = null;
                SaveSessionChanges(session);
            });
        }

        public ChatStateResponse SetChatHtmlMode(string chatId, bool enabled)
        {
            return WithReservedChatState(LoadSession(chatId), session =>
            {
                session.HtmlModeEnabled = enabled;
                SaveSessionChanges(session);
            });
        }

        public ChatStateResponse SetChatReasoning(string chatId, bool enabled)
        {
            return WithReservedChatState(LoadSession(chatId), session =>
            {
                session.ReasoningEnabled = enabled;
                SaveSessionChanges(session);
            });
        }

        public ChatStateResponse ClearChat(string chatId)
        {
            return WithReservedChatState(LoadSession(chatId), session =>
            {
                var sessionId = session.Id;
                RemovePendingAgentToolsForSession(sessionId);
                session.Messages.Clear();
                session.Context = CreateEmptyContext();
                session.HtmlWorkspace = new HtmlWorkspace();
                session.Artifacts = new List<ChatArtifact>();
                session.ContextCheckpoints = new List<ContextCheckpoint>();
                session.ActiveContextCheckpointId = null;
                session.ActiveHtmlArtifactId = null;
                session.ActivePlanArtifactId = null;
                session.LastRun = null;
                NormalizeContext(session.Context, session);
                SaveSessionChanges(session);
                _attachmentStore.DeleteSession(sessionId);
            });
        }

        public ChatStateResponse DeleteChat(string chatId)
        {
            var next = WithReservedSession(LoadSession(chatId), current =>
            {
                var sessionId = current.Id;
                var selected = _chatSessions.DeleteAndSelectNext(sessionId);
                RemovePendingAgentToolsForSession(sessionId);
                _attachmentStore.DeleteSession(sessionId);
                return selected;
            });
            return ChatState(next);
        }

        public ChatStateResponse DeleteDocument(string host, string documentKey)
        {
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(documentKey))
            {
                throw new InvalidOperationException("Документ не указан.");
            }
            using (_chatRuns.ReserveMaintenance())
            {
                // Deletion is rare and destructive. A short global coordination window is safer
                // than racing a newly created chat that was not present during enumeration.
                EnsureNoActiveRuns();

                var sessions = _chatStore.ListHeaders(host, documentKey, string.Empty)
                    .OrderBy(session => session.Id, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                _chatStore.DeleteDocument(host, documentKey);
                foreach (var header in sessions)
                {
                    _attachmentStore.DeleteSession(header.Id);
                    RemovePendingAgentToolsForSession(header.Id);
                }
            }
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

        private ChatSession ReloadReservedSession(ChatSession session)
        {
            if (session == null || !_chatStore.IsPersisted(session)) return session;
            return _chatStore.Load(session.Host, session.DocumentKey, session.Id) ?? session;
        }

        private T WithReservedSession<T>(ChatSession session, Func<ChatSession, T> action)
        {
            if (action == null) throw new ArgumentNullException("action");
            using (ReserveChatOperation(session))
            {
                return action(ReloadReservedSession(session));
            }
        }

        private void EnsureNoActiveRuns()
        {
            if (_chatRuns.HasRuns() || _chatRuns.HasExternalRuns())
            {
                throw new InvalidOperationException("Сначала остановите выполняющиеся запросы во всех окнах RNAssistant.");
            }
        }

        private ChatStateResponse WithReservedChatState(ChatSession session, Action<ChatSession> action)
        {
            var updated = WithReservedSession(session, current =>
            {
                action(current);
                return current;
            });
            return ChatState(updated);
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
                ActiveChatReasoning = session != null && session.ReasoningEnabled,
                Chats = _chatSessions.GetChatSummaries(activeId),
                Documents = ListOpenDocuments(),
                Context = session == null ? CreateEmptyContext() : ChatCloneService.CloneContext(LoadContext(session)),
                Messages = session == null ? new List<ChatMessage>() : ChatCloneService.CloneMessages(session.Messages),
                Artifacts = ChatArtifactDto.From(session == null ? null : session.Artifacts),
                ActiveContextCheckpointId = session == null ? string.Empty : session.ActiveContextCheckpointId,
                ActiveHtmlArtifactId = session == null ? string.Empty : session.ActiveHtmlArtifactId,
                ActivePlanArtifactId = session == null ? string.Empty : session.ActivePlanArtifactId,
                ContextUsage = ContextUsageEstimator.FromSession(session, ResolveChatSettings(session)),
                HtmlWorkspace = HtmlWorkspaceDto.From(session == null ? null : HtmlArtifactToolExecutor.NormalizeWorkspace(session.HtmlWorkspace))
            };
        }

        private AppSettings ResolveChatSettings(ChatSession session, AppSettings settings = null)
        {
            return ChatSettingsResolver.Resolve(settings ?? _settingsService.Load(), session);
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
