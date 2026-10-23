using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Runtime
{
    public sealed partial class WorkspaceConversationService
    {
        private void StartTitleGeneration(WorkspaceDescriptor workspace, ChatSession session,
            AppSettings settings, Func<string> apiKeyProvider, RunSummary summary)
        {
            var draft = session?.LastRun?.ProvisionalTitle;
            if (settings?.SmartChatTitles != true || summary?.Lifecycle != RunLifecycle.Completed ||
                string.IsNullOrWhiteSpace(draft) ||
                string.IsNullOrWhiteSpace(summary.AssistantMessage) ||
                !ChatTitleBuilder.CanReplaceAutoTitle(session, draft)) return;

            var sessionId = session.Id;
            if (!_pendingTitles.TryAdd(sessionId, 0)) return;
            var userText = ChatTitleBuilder.ResolveUserSeed(session, string.Empty);
            var assistantText = summary.AssistantMessage;
            var titleSettings = settings.Clone();
            var token = _titleLifetime.Token;
            Task.Run(async () =>
            {
                try
                {
                    string title;
                    try
                    {
                        title = await ChatTitleBuilder.GenerateLlmTitleAsync(titleSettings,
                            userText, assistantText, new LlmClient(apiKeyProvider).CompleteAsync,
                            null, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { return; }
                    catch { title = ChatTitleBuilder.BuildFallbackTitle(userText, assistantText); }
                    if (token.IsCancellationRequested || string.IsNullOrWhiteSpace(title)) return;

                    // The run lease is released before the title can change metadata.
                    // A later run or manual rename always wins over this best-effort update.
                    for (var attempt = 0; attempt < 120 && !token.IsCancellationRequested; attempt++)
                    {
                        try
                        {
                            using (var runtimeLease = _titleApplyLease?.Invoke())
                            using (AcquireSessionLease(sessionId))
                            {
                                var current = _workspaces.LoadSession(_chats, workspace, sessionId);
                                if (!ChatTitleBuilder.CanReplaceAutoTitle(current, draft)) return;
                                if (string.Equals(current.Title, title, StringComparison.Ordinal)) return;
                                _chats.SetTitle(current, title);
                                return;
                            }
                        }
                        catch (ChatConcurrencyException) { }
                        catch (IOException) { }
                        catch { return; }
                        try { await Task.Delay(500, token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                    }
                }
                finally
                {
                    byte ignored;
                    _pendingTitles.TryRemove(sessionId, out ignored);
                }
            });
        }

        public WorkspaceSessionSnapshot ReadState(WorkspaceDescriptor workspace, string sessionId)
        {
            var session = GetSession(workspace, sessionId);
            if (session == null) throw new ArgumentException("Session not found.");
            return new WorkspaceSessionSnapshot
            {
                Id = session.Id, WorkspaceId = session.WorkspaceId, Title = session.Title,
                TitleGenerationPending = _pendingTitles.ContainsKey(session.Id), Model = session.Model,
                ReadOnly = session.FormatVersion != ChatSession.CurrentFormatVersion,
                Mode = session.Mode, MutationPolicy = session.MutationPolicy, Revision = session.Revision, Cursor = ConversationEventFeed.Cursor(session),
                ConversationPolicy = session.LastRun?.ConversationPolicy,
                Status = session.LastRun?.Status, ModelConfiguration = session.LastRun?.ModelConfiguration,
                RunViewState = RunViewStateProjector.Create(session),
                Office = IsPluginSource(workspace) || session.LastRun?.OfficeTarget == null ? null : new WorkspaceOfficeContext {
                    Host = session.LastRun.OfficeTarget.Host,
                    DocumentTitle = session.LastRun.OfficeTarget.DocumentTitle },
                RunId = session.LastRun?.RunId, IterationsUsed = session.LastRun?.KernelState?.Summary.IterationsUsed,
                ContextCheckpointId = ContextCompactionRuntime.ActiveCheckpoint(session)?.Id,
                ToolStepsUsed = session.LastRun?.KernelState?.Summary.ToolStepsUsed, Limits = session.LastRun?.KernelState?.Limits,
                ContextReceipt = session.LastContextReceipt, Acceptance = session.LastRun?.WorkspaceAcceptance,
                UserInput = GetPendingQuestions(session), TaskList = GetTaskList(session), Plan = GetPlan(workspace, session),
                InterruptedToolId = session.LastRun?.InterruptedToolId, InterruptedPath = session.LastRun?.InterruptedFilePath,
                InterruptedEffectPossible = session.LastRun?.InterruptedEffectPossible, MessageCount = session.Messages.Count,
                Presentation = Presentation(workspace, session)
            };
        }

        public ConversationEventPage ReadEvents(WorkspaceDescriptor workspace, string sessionId, string after,
            string through = null, int limit = 50)
        {
            // Event metadata needs no run lease, LLM or reconciliation. Reconnecting
            // cannot start a run, close a pending approval or repeat a tool.
            if (limit < 1 || limit > 100) throw new ArgumentException("Event page limit must be 1–100.");
            try
            {
                var session = GetSession(workspace, sessionId);
                if (session == null) return new ConversationEventPage { SessionId = sessionId, After = after,
                    Reason = ConversationResyncReason.HistoryUnavailable };
                return new ConversationEventFeed(new ChatEventStoreAdapter(_chats)).Read(session, after, through, limit);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is JsonException || ex is UnauthorizedAccessException)
            { return new ConversationEventPage { SessionId = sessionId, After = after, Reason = ConversationResyncReason.HistoryUnavailable }; }
        }

        public async Task SubscribeEventsAsync(WorkspaceDescriptor workspace, string sessionId, string after,
            Func<ConversationEventPage, Task> onPage, int limit = 50,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (onPage == null) throw new ArgumentNullException(nameof(onPage));
            if (limit < 1 || limit > 100) throw new ArgumentException("Event page limit must be 1–100.");
            cancellationToken.ThrowIfCancellationRequested();
            ChatSession session = null;
            try { session = GetSession(workspace, sessionId); }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is JsonException || ex is UnauthorizedAccessException) { }
            if (session == null)
            {
                await onPage(new ConversationEventPage { SessionId = sessionId, After = after,
                    Reason = ConversationResyncReason.HistoryUnavailable }).ConfigureAwait(false);
                return;
            }
            await new ConversationEventFeed(new ChatEventStoreAdapter(_chats))
                .SubscribeAsync(session, after, onPage, limit, cancellationToken).ConfigureAwait(false);
        }

        public WorkspaceInputReceipt FindAcceptedInput(WorkspaceDescriptor workspace, WorkspaceSubmitInputRequest request)
        {
            ValidateInput(request);
            if (request.RequestId == null) return null;
            var session = GetSession(workspace, request.SessionId);
            if (session == null) throw new ArgumentException("Session not found.");
            return FindAcceptedInput(session, request.RequestId, InputFingerprint(request));
        }

        private WorkspaceInputReceipt FindAcceptedInput(ChatSession session, string requestId, string fingerprint)
        {
            var records = new ConversationInputJournal(new ChatEventStoreAdapter(_chats)).Read(session, false)
                .Where(item => item.Change.Input?.OperationId == requestId).ToArray();
            if (records.Length == 0) return null;
            var first = records[0];
            var input = first.Change.Input;
            if (input.Revision != 1 || string.IsNullOrEmpty(input.AcceptedRunId) ||
                string.IsNullOrEmpty(input.RequestFingerprint))
                throw new InvalidOperationException("The accepted input receipt is unavailable; inspect the session without replaying input.");
            ConversationInputJournal.RequireSameRequest(input, fingerprint);
            return Receipt(first.Event, input, records[records.Length - 1].Change.Input.Status == ConversationInputStatus.Applied);
        }

        private static WorkspaceInputReceipt Receipt(SessionEvent fact, ConversationInput input, bool delivered)
        { return new WorkspaceInputReceipt { SessionId = fact.SessionId, RequestId = input.OperationId,
            InputId = input.Id, RunId = input.AcceptedRunId, AcceptedSequence = fact.Sequence,
            AcceptedCursor = ConversationEventFeed.Cursor(fact), DeliveryRecorded = delivered }; }

        private static void ValidateInput(WorkspaceSubmitInputRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.RequestId != null && (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128 ||
                string.IsNullOrWhiteSpace(request.SessionId)))
                throw new ArgumentException("A request ID must be 1–128 characters and requires an existing --session (use session create first).");
            if (request.Answers == null && string.IsNullOrWhiteSpace(request.Message)) throw new ArgumentException("A task message is required.");
            if (request.Answers != null && (string.IsNullOrWhiteSpace(request.SessionId) || request.Message != null))
                throw new ArgumentException("Typed answers require an existing session and cannot include a task message.");
            if (request.Mode != null) ConversationRunPolicy.For(request.Mode);
            if (request.MutationPolicy.HasValue && !Enum.IsDefined(typeof(MutationPolicy), request.MutationPolicy.Value))
                throw new ArgumentException("Invalid mutation policy.");
            PrepareAcceptance(request.Acceptance);
        }

        private static string InputFingerprint(WorkspaceSubmitInputRequest request)
        {
            // Transport identity is bound to submitted intent. Environment/model
            // settings are execution configuration; a duplicate never reapplies them.
            var canonical = new WorkspaceSubmitInputRequest { Message = request.Message,
                Mode = request.Mode == null ? null : ChatModes.Require(request.Mode), MutationPolicy = request.MutationPolicy,
                AutoConfirmToolActions = request.AutoConfirmToolActions, ReasoningEnabled = request.ReasoningEnabled,
                Answers = request.Answers, Acceptance = PrepareAcceptance(request.Acceptance) };
            return ConversationInputJournal.Fingerprint(canonical);
        }
    }
}
