using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        public async Task<SendChatResponse> SendChatAsync(
            string text,
            string chatId = null,
            IReadOnlyList<string> resourceDraftIds = null,
            Action<string, string, ChatActivity> progress = null,
            Action<ChatStateResponse> chatStateChanged = null,
            CancellationToken cancellationToken = default(CancellationToken),
            string runId = null)
        {
            if (string.IsNullOrWhiteSpace(text) &&
                (resourceDraftIds == null || resourceDraftIds.Count == 0))
            {
                return EmptySendResponse(LoadAddressedSession(chatId), _settingsService.Load());
            }

            var settings = _settingsService.Load();
            var session = LoadAddressedSession(chatId);
            runId = string.IsNullOrWhiteSpace(runId) ? Guid.NewGuid().ToString("N") : runId;
            var attachments = _chatResourceIngestion.LoadDrafts(session, resourceDraftIds);
            var invalidAttachment = attachments.FirstOrDefault(a => a != null && a.Status == "error");
            if (invalidAttachment != null)
            {
                throw new InvalidOperationException(invalidAttachment.FileName + ": " + invalidAttachment.Error);
            }

            return await ExecuteChatTurnAsync(
                session,
                settings,
                new ChatTurnInput
                {
                    Text = text ?? string.Empty,
                    Attachments = attachments,
                    AppendUserMessage = true,
                    CommitUserAttachments = true
                },
                null,
                progress,
                chatStateChanged,
                cancellationToken,
                runId).ConfigureAwait(false);
        }

        public bool CancelChatRun(string chatId, string runId)
        {
            return _chatRuns.Cancel(chatId, runId);
        }

        private static void AnnotateRunMessages(ChatSession session, int firstIndex, string runId)
        {
            if (session == null || session.Messages == null) return;
            var sequence = 1;
            for (var index = Math.Max(0, firstIndex); index < session.Messages.Count; index++)
            {
                var message = session.Messages[index];
                if (message == null) continue;
                message.RunId = runId;
                message.Sequence = sequence++;
                if (message.Activity != null)
                {
                    AnnotateActivity(message.Activity, runId, message.Sequence);
                }
            }
        }

        private static void AnnotateActivity(ChatActivity activity, string runId, int? sequence)
        {
            if (activity == null) return;
            activity.RunId = runId;
            activity.Sequence = sequence;
            foreach (var child in activity.Children ?? new List<ChatActivity>())
            {
                AnnotateActivity(child, runId, sequence);
            }
        }

        private void PersistRunCheckpoint(ChatSession session, string runId, string phase)
        {
            if (!string.Equals(phase, "tool_running", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(phase, "tool_result", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (session.LastRun == null || session.LastRun.KernelState == null) SaveSessionChanges(session);
            _chatRuns.UpdateSessionSnapshot(session.Id, runId, session);
        }

        private static void CloseRunningActivities(ChatSession session, int firstMessageIndex, bool cancelled)
        {
            if (session == null || session.Messages == null) return;
            for (var index = Math.Max(0, firstMessageIndex); index < session.Messages.Count; index++)
            {
                CloseRunningActivity(session.Messages[index] == null ? null : session.Messages[index].Activity, cancelled);
            }
        }

        private static void CloseRunningActivity(ChatActivity activity, bool cancelled)
        {
            if (activity == null) return;
            if (string.Equals(activity.Status, "running", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(activity.ExecutionStatus, "executing", StringComparison.OrdinalIgnoreCase))
            {
                activity.Status = cancelled ? "cancelled" : "failed";
                activity.ExecutionStatus = cancelled ? "cancelled" : "runtime_error";
                activity.Retryable = cancelled;
                activity.PendingId = null;
                activity.ConfirmationCatalogSha256 = null;
                activity.ResultMessage = cancelled
                    ? "Execution was cancelled before a result was recorded."
                    : "Execution stopped before a result was recorded.";
            }
            foreach (var child in activity.Children ?? new List<ChatActivity>())
            {
                CloseRunningActivity(child, cancelled);
            }
        }

        public ChatResourceDraftResponse StageChatResource(
            string chatId,
            string fileName,
            string contentType,
            string base64)
        {
            var session = LoadAddressedSession(chatId);
            return new ChatResourceDraftResponse
            {
                Resource = _chatResourceIngestion.Stage(session, fileName, contentType, base64)
            };
        }

        public DeleteResponse DiscardChatResourceDraft(string chatId, string id)
        {
            var session = LoadAddressedSession(chatId);
            _chatResourceIngestion.Discard(session, id);
            return new DeleteResponse { Deleted = true };
        }

        private void StartChatTitleGeneration(
            ChatSession session,
            string userText,
            string assistantText,
            AppSettings settings,
            string expectedCurrentTitle,
            Action<ChatStateResponse> chatStateChanged)
        {
            if (session == null || !ChatTitleBuilder.CanReplaceAutoTitle(session, expectedCurrentTitle))
            {
                return;
            }

            var host = session.Host;
            var documentKey = session.DocumentKey;
            var documentTitle = session.DocumentTitle;
            var sessionId = session.Id;
            CancellationToken lifetimeToken;
            try
            {
                lifetimeToken = _lifetimeCancellation.Token;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            Task.Run(async delegate
            {
                var title = string.Empty;
                try
                {
                    var traceSession = _conversationStore.Load(host, documentKey, sessionId);
                    title = traceSession == null
                        ? ChatTitleBuilder.BuildFallbackTitle(userText, assistantText)
                        : await ChatTitleBuilder.GenerateLlmTitleAsync(
                            settings,
                            userText,
                            assistantText,
                            _llmCompletion,
                            traceSession,
                            lifetimeToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    if (lifetimeToken.IsCancellationRequested)
                    {
                        return;
                    }
                    title = ChatTitleBuilder.BuildFallbackTitle(userText, assistantText);
                }

                if (lifetimeToken.IsCancellationRequested || string.IsNullOrWhiteSpace(title))
                {
                    return;
                }

                try
                {
                    ChatStateResponse state;
                    using (_chatRuns.ReserveMaintenance())
                    {
                        lock (_syncRoot)
                        {
                            if (!_chatSessions.TryApplyGeneratedTitle(
                                host,
                                documentKey,
                                sessionId,
                                expectedCurrentTitle,
                                title))
                            {
                                return;
                            }
                            state = CreateStoredChatState(host, documentKey, documentTitle);
                        }
                    }

                    if (chatStateChanged != null)
                    {
                        chatStateChanged(state);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch
                {
                    // Title generation is best-effort and must not fault the chat run.
                }
            });
        }

        private static void RecordFailedTurn(ChatSession session, Exception error)
        {
            if (session == null)
            {
                return;
            }
            var cancelled = error is OperationCanceledException;
            session.Messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = cancelled ? "Запрос отменён." : "Запрос завершился технической ошибкой.",
                Activity = new ChatActivity
                {
                    Kind = "diagnostic",
                    Title = cancelled ? "Request cancelled" : "Request failed",
                    Status = cancelled ? "cancelled" : "failed",
                    ExecutionStatus = cancelled ? "cancelled" : "runtime_error",
                    ResultMessage = error == null ? string.Empty : error.Message
                }
            });
        }

        private sealed class ChatTurnInput
        {
            public string Text { get; set; }
            public IReadOnlyList<ChatAttachment> Attachments { get; set; }
            public bool AppendUserMessage { get; set; }
            public bool CommitUserAttachments { get; set; }
            public IReadOnlyList<ChatMessage> MessagesToDeleteAfterSave { get; set; }
        }

        private async Task<SendChatResponse> ExecuteChatTurnAsync(
            ChatSession session,
            AppSettings settings,
            ChatTurnInput input,
            Func<ChatSession, ChatTurnInput> prepareTurn,
            Action<string, string, ChatActivity> progress,
            Action<ChatStateResponse> chatStateChanged,
            CancellationToken cancellationToken,
            string runId)
        {
            session = session ?? LoadAddressedSession(null);
            var sessionId = session.Id;
            runId = string.IsNullOrWhiteSpace(runId) ? Guid.NewGuid().ToString("N") : runId;

            var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ChatRunLease runLease;
            RunCausalTrace causalTrace = null;
            try
            {
                runLease = _chatRuns.Start(sessionId, runId, session, runCancellation);
            }
            catch
            {
                runCancellation.Dispose();
                throw;
            }

            try
            {
                session = ReloadReservedSession(session);
                EnsureNotQualificationChat(session);
                settings = ResolveChatSettings(session, settings);
                settings.EnsureAgentPromptsReviewed();
                ConversationProtocolContext.EnsureCurrentHistory(session);
                if (prepareTurn == null && HasPendingAgentConfirmation(session))
                {
                    throw new InvalidOperationException("Сначала подтвердите или отмените ожидающее действие агента.");
                }
                input = prepareTurn == null ? input : prepareTurn(session);
                input = input ?? new ChatTurnInput();
                var text = input.Text ?? string.Empty;
                var attachments = input.Attachments ?? new ChatAttachment[0];
                var attachmentRouting = AttachmentModelRoutingService.Select(settings, session, attachments);
                settings = attachmentRouting.Settings;
                var executionMode = ChatModes.Normalize(session.Mode);
                var documentRuntimeKey = string.Empty;
                if (executionMode != ChatModes.Chat)
                {
                    documentRuntimeKey = CaptureExpectedRuntimeDocumentKey(session);
                }
                HtmlWorkspaceArtifactService.CaptureCurrent(session, "Before chat turn");
                var firstRunMessageIndex = session.Messages == null ? 0 : session.Messages.Count;
                ChatMessage appendedUserMessage = null;
                var commitUserAttachments = input.CommitUserAttachments;
                if (!input.AppendUserMessage && session.Messages != null && session.Messages.Count > 0)
                {
                    firstRunMessageIndex = Math.Max(0, session.Messages.Count - 1);
                }
                if (input.AppendUserMessage)
                {
                    var userMessage = new ChatMessage
                    {
                        Role = "user",
                        Content = text,
                        RunId = runId,
                        Sequence = 1,
                        HtmlWorkspaceCheckpoint = ChatResourceUri.ResolveArtifactRevision(session, session.ActiveHtmlArtifactId),
                        Attachments = new List<ChatAttachment>(attachments)
                    };
                    session.Messages.Add(userMessage);
                    appendedUserMessage = userMessage;
                }
                var documentContext = LoadContext(session);
                var titleUserSeed = ChatTitleBuilder.ResolveUserSeed(session, text);
                var shouldGenerateLlmTitle = settings.SmartChatTitles && ChatTitleBuilder.ShouldAssign(session);
                var provisionalTitle = ChatTitleBuilder.ShouldAssign(session)
                    ? ChatTitleBuilder.BuildDraftTitle(titleUserSeed)
                    : string.Empty;
                if (!string.IsNullOrWhiteSpace(provisionalTitle))
                {
                    session.Title = provisionalTitle;
                }

                session.LastRun = new ChatRunRecord
                {
                    RunId = runId,
                    TurnId = runId,
                    RuntimeId = _runtimeId,
                    ResponseProtocolVersion = AgentResponseProtocol.CurrentVersion,
                    Status = "running",
                    Phase = "starting",
                    CurrentAction = "Preparing request.",
                    DocumentRuntimeKey = executionMode != ChatModes.Chat ? documentRuntimeKey : null,
                    IterationsUsed = 0,
                    ToolStepsUsed = 0,
                    StartedUtc = DateTime.UtcNow
                };
                var preparedTurnPersisted = false;
                try
                {
                    if (commitUserAttachments && appendedUserMessage != null)
                    {
                        // Keep drafts until the chat durably references their verified CAS blobs.
                        _chatResourceIngestion.CommitAndLink(session, appendedUserMessage, firstRunMessageIndex);
                    }
                    else
                    {
                        ChatResourceReferenceService.LinkMessageResources(session, firstRunMessageIndex);
                    }
                    _conversationStore.Save(session);
                    preparedTurnPersisted = true;
                    causalTrace = RunCausalTrace.Begin(_eventStore, session);
                    RunCausalTrace.Record(new CausalTraceRecord(SessionEventKind.RunStartedObservation)
                    {
                        Status = "running"
                    });
                    _chatSessions.NotifySaved(session);
                    if (commitUserAttachments && appendedUserMessage != null)
                    {
                        _chatResourceIngestion.DeleteDrafts(appendedUserMessage);
                    }
                    foreach (var removedMessage in input.MessagesToDeleteAfterSave ?? new ChatMessage[0])
                    {
                        _attachmentStore.DeleteMessage(removedMessage);
                    }
                    input.MessagesToDeleteAfterSave = null;
                    _chatRuns.UpdateSessionSnapshot(sessionId, runId, session);
                    if (!string.IsNullOrWhiteSpace(provisionalTitle) && chatStateChanged != null)
                    {
                        try
                        {
                            chatStateChanged(CreateStoredChatState(session.Host, session.DocumentKey, session.DocumentTitle));
                        }
                        catch
                        {
                            // A UI notification cannot invalidate an already persisted request.
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (preparedTurnPersisted)
                    {
                        RecordFailedTurn(session, ex);
                        if (session.LastRun != null)
                        {
                            session.LastRun.Status = "failed";
                            session.LastRun.Phase = "failed";
                            session.LastRun.CurrentAction = ex.Message;
                        }
                        AnnotateRunMessages(session, firstRunMessageIndex, runId);
                        try
                        {
                            SaveSessionChanges(session);
                            RunCausalTrace.Summary(session);
                        }
                        catch
                        {
                            // Keep the original preparation failure. Draft files remain available.
                        }
                        _chatRuns.UpdateSessionSnapshot(sessionId, runId, session);
                    }
                    throw;
                }

                Action<string, string, ChatActivity> runProgress = (phase, message, activity) =>
                {
                    _chatRuns.Update(sessionId, runId, phase, message);
                    if (session.LastRun != null && string.Equals(session.LastRun.RunId, runId, StringComparison.OrdinalIgnoreCase))
                    {
                        session.LastRun.Phase = string.IsNullOrWhiteSpace(phase) ? session.LastRun.Phase : phase;
                        session.LastRun.CurrentAction = string.IsNullOrWhiteSpace(message) ? session.LastRun.CurrentAction : message;
                    }
                    if (activity != null)
                    {
                        AnnotateActivity(activity, runId, null);
                    }
                    if (string.Equals(phase, "tool_running", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(phase, "tool_result", StringComparison.OrdinalIgnoreCase))
                    {
                        AnnotateRunMessages(session, firstRunMessageIndex, runId);
                    }
                    PersistRunCheckpoint(session, runId, phase);
                    ReportExternalProgress(progress, phase, message, activity);
                };

                ChatTurnResult completion;
                try
                {
                    if (attachmentRouting.HasMedia)
                    {
                        ReportProgress(runProgress, "routing", attachmentRouting.ProgressMessage);
                    }
                    var turnUserMessage = appendedUserMessage ?? (session.Messages ?? new List<ChatMessage>())
                        .LastOrDefault(message => message != null && !message.ProtocolMessage &&
                            string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase));
                    var attachmentAnalysis = await _attachmentAnalysisService.EnsureAsync(
                        text,
                        session,
                        turnUserMessage,
                        attachmentRouting,
                        runProgress,
                        runCancellation.Token).ConfigureAwait(false);
                    var primaryText = AttachmentAnalysisService.BuildPrimaryRequest(
                        text,
                        attachmentAnalysis);
                    var primaryAttachments = attachmentRouting.PrimaryAttachments ?? new ChatAttachment[0];
                    try
                    {
                        await _contextCompactionService.EnsureWithinBudgetAsync(
                            session,
                            settings,
                            string.Empty,
                            false,
                            runProgress,
                            runCancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception compactionError)
                    {
                        var activity = new ChatActivity
                        {
                            Kind = "compaction",
                            Title = "Не удалось сжать контекст",
                            Subtitle = "Полная история сохранена",
                            Status = "failed",
                            ExecutionStatus = "compaction_failed",
                            ResultMessage = compactionError.Message
                        };
                        session.Messages.Add(AgentTranscript.CreateAssistantMessage(
                            "Не удалось обновить сжатый контекст; продолжаю с сохранённой историей.", null, activity));
                        runProgress("compaction_failed", activity.ResultMessage, activity);
                    }
                    var tools = (executionMode == ChatModes.Agent
                            ? _toolCatalog.GetFreshConversationTools()
                            : _toolCatalog.GetVisibleTools())
                        .Where(tool => tool.Enabled)
                        .ToList();
                    var skills = executionMode != ChatModes.Chat
                        ? _skillCatalog.GetVisibleSkills().Where(skill => skill.Enabled).ToList()
                        : new List<SkillDefinition>();
                    try
                    {
                        completion = await _conversationRunService.ExecuteAsync(
                            executionMode,
                            primaryText,
                            session,
                            documentContext,
                            settings,
                            tools,
                            primaryAttachments,
                            runProgress,
                            executionMode == ChatModes.Agent
                                ? (ConversationRunService.PendingToolRegistrar)RegisterPendingAgentTool
                                : null,
                            skills,
                            runCancellation.Token,
                            false).ConfigureAwait(false);
                    }
                    finally
                    {
                        // Auto-confirmed VBA mutations bypass the explicit VBA bridge methods.
                        if (executionMode != ChatModes.Chat) _toolCatalog.InvalidateDocumentVbaTools();
                    }
                }
                catch (RunStoreException)
                {
                    if (causalTrace != null)
                    {
                        causalTrace.Dispose();
                        causalTrace = null;
                    }
                    RecoverAfterRunStoreFailure(runLease, session, sessionId);
                    throw;
                }
                catch (Exception ex)
                {
                    PersistTokenEstimateCalibration(settings);
                    CloseRunningActivities(session, firstRunMessageIndex, ex is OperationCanceledException);
                    RecordFailedTurn(session, ex);
                    if (session.LastRun != null)
                    {
                        session.LastRun.Status = ex is OperationCanceledException ? "cancelled" : "failed";
                        session.LastRun.Phase = session.LastRun.Status;
                        session.LastRun.CurrentAction = ex.Message;
                    }
                    AnnotateRunMessages(session, firstRunMessageIndex, runId);
                    HtmlWorkspaceArtifactService.StampUncheckpointed(session, firstRunMessageIndex, session.ActiveHtmlArtifactId);
                    ChatResourceReferenceService.LinkMessageResources(session, firstRunMessageIndex);
                    SaveSessionChanges(session);
                    RunCausalTrace.Summary(session);
                    _chatRuns.UpdateSessionSnapshot(sessionId, runId, session);
                    throw;
                }

                PersistTokenEstimateCalibration(settings);
                if (settings.SmartChatTitles == false)
                {
                    ChatTitleBuilder.ApplyFallback(session, text, completion.AssistantText);
                }
                ReportProgress(runProgress, "saving", "Сохраняю историю чата...");
                HtmlWorkspaceArtifactService.StampUncheckpointed(session, firstRunMessageIndex, session.ActiveHtmlArtifactId);
                AnnotateRunMessages(session, firstRunMessageIndex, runId);
                ChatResourceReferenceService.LinkMessageResources(session, firstRunMessageIndex);
                if (completion == null || !completion.WaitingForConfirmation)
                {
                    ApplyTerminalRunResult(session);
                }
                SaveSessionChanges(session);
                RunCausalTrace.Summary(session);
                _chatRuns.UpdateSessionSnapshot(sessionId, runId, session);
                runLease.Dispose();
                var response = CreateSendChatResponse(session, settings, completion);
                RunCausalTrace.Projected("SendChatResponse");
                causalTrace.Dispose();
                if (shouldGenerateLlmTitle)
                {
                    StartChatTitleGeneration(
                        session,
                        titleUserSeed,
                        ChatTitleBuilder.ResolveAssistantSeed(session, completion.AssistantText),
                        settings,
                        provisionalTitle,
                        chatStateChanged);
                }

                return response;
            }
            finally
            {
                if (causalTrace != null) causalTrace.Dispose();
                runLease.Dispose();
            }
        }

        private SendChatResponse CreateSendChatResponse(ChatSession session, AppSettings settings, ChatTurnResult completion)
        {
            settings = ResolveChatSettings(session, settings);
            var activeId = session.Id;
            return new SendChatResponse
            {
                SessionRevision = session == null ? 0 : session.Revision,
                RunViewState = RunViewStateProjector.Create(session),
                Message = completion == null ? string.Empty : completion.AssistantText,
                ToolResults = completion == null
                    ? (IReadOnlyList<object>)new object[0]
                    : completion.ToolResults ?? new object[0],
                Tools = _toolCatalog.GetVisibleTools(),
                Skills = _skillCatalog.GetVisibleSkills(),
                ActiveChatId = activeId,
                ActiveChatModel = session == null ? string.Empty : session.Model,
                ActiveChatMode = ChatModes.Normalize(session == null ? null : session.Mode),
                ActiveChatReasoning = session != null && session.ReasoningEnabled,
                Chats = _chatSessions.GetChatSummaries(activeId),
                Documents = ListOpenDocuments(),
                Context = session == null ? CreateEmptyContext() : ChatCloneService.CloneContext(LoadContext(session)),
                Messages = session == null ? new List<ChatMessage>() : ChatCloneService.CloneMessages(session.Messages),
                Artifacts = ChatArtifactDto.From(session),
                ActiveContextCheckpointId = session == null ? string.Empty : session.ActiveContextCheckpointId,
                ActiveHtmlArtifactId = session == null ? string.Empty : session.ActiveHtmlArtifactId,
                ActiveTaskListArtifactId = session == null ? string.Empty : session.ActiveTaskListArtifactId,
                ActivePlanDocumentArtifactId = session == null ? string.Empty : session.ActivePlanDocumentArtifactId,
                ContextUsage = completion == null
                    ? ContextUsageEstimator.FromSession(session, settings)
                    : completion.ContextUsage ?? ContextUsageEstimator.FromSession(session, settings),
                HtmlWorkspace = HtmlWorkspaceDto.From(
                    session == null ? null : HtmlArtifactToolExecutor.NormalizeWorkspace(session.HtmlWorkspace),
                    session == null ? null : session.HtmlWorkspaceRecovery)
            };
        }

        private SendChatResponse EmptySendResponse(ChatSession session, AppSettings settings)
        {
            settings = ResolveChatSettings(session, settings);
            return CreateSendChatResponse(session, settings, new ChatTurnResult
            {
                AssistantText = string.Empty,
                ToolResults = new object[0],
                ContextUsage = ContextUsageEstimator.FromSession(session, settings)
            });
        }

        private static void ApplyTerminalRunResult(ChatSession session)
        {
            if (session == null || session.LastRun == null || session.LastRun.KernelState == null)
                throw new InvalidOperationException("Conversation ended without kernel evidence. Reload the chat before continuing.");
            ConversationRunProjection.Apply(session.LastRun);
        }

    }
}
