using RNAssistant.Core.Persistence;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        private readonly object _inboxWorkersSync = new object();
        private readonly HashSet<Task> _inboxWorkers = new HashSet<Task>();
        private readonly System.Threading.CancellationTokenSource _inboxCancellation = new System.Threading.CancellationTokenSource();
        public Task InboxWorkersDrained { get { lock (_inboxWorkersSync) return Task.WhenAll(_inboxWorkers.ToArray()); } }
        public void StopInboxWorkers() { _inboxCancellation.Cancel(); }
        private bool HasInboxWorkers { get { lock (_inboxWorkersSync) return _inboxWorkers.Count != 0; } }

        private void EndInboxRun(ChatSession session, ChatRunLease lease, ref RunCausalTrace trace)
        {
            try
            {
                var summary = session.LastRun == null || session.LastRun.KernelState == null ? null : session.LastRun.KernelState.Summary;
                _inbox.End(session, summary != null && summary.Lifecycle == RunLifecycle.Completed && summary.ExecutionHealth == ExecutionHealth.Clean,
                    summary != null && summary.Lifecycle == RunLifecycle.AwaitingConfirmation);
            }
            finally { ReleaseControllerRun(lease, ref trace); }
        }

        public ChatInboxResponse GetChatInbox(string chatId)
        { if (string.IsNullOrWhiteSpace(chatId)) throw new ArgumentException("An explicit chat is required.");
            return _inbox.Snapshot(LoadAddressedSession(chatId)); }

        private ChatSession LoadInboxSession(string chatId)
        {
            if (string.IsNullOrWhiteSpace(chatId)) throw new ArgumentException("An explicit chat is required.");
            var session = LoadAddressedSession(chatId);
            if (!_conversationStore.IsPersisted(session))
            {
                using (ReserveChatOperation(session))
                {
                    session = ReloadReservedSession(session);
                    _conversationStore.Commit(session, new ConversationCommit());
                    _chatSessions.NotifySaved(session);
                }
            }
            return session;
        }

        public ChatInboxResponse StopChatInbox(string chatId)
        {
            var session = LoadInboxSession(chatId);
            try { _inbox.Pause(session, "Остановлено пользователем"); }
            finally
            {
                var run = _chatRuns.Get(chatId);
                if (run != null) _chatRuns.Cancel(chatId, run.RunId);
            }
            return _inbox.Snapshot(session);
        }

        public ChatResourceDraftResponse StageSelectionInput(string mode, string chatId)
        {
            if (string.IsNullOrWhiteSpace(chatId)) throw new ArgumentException("An explicit chat is required.");
            var session = LoadAddressedSession(chatId);
            EnsureCurrentDocument(session);
            var target = new OfficeDocumentExecutionExpectation { Host = session.Host, DocumentKey = session.DocumentKey,
                RuntimeDocumentKey = CaptureExpectedRuntimeDocumentKey(session) };
            var settings = ResolveChatSettings(session);
            var note = _officeContextCapture.CaptureSelection(target, mode,
                RNAssistant.Core.Llm.ModelContextBudget.ApproximateTextCharacterCapacity(
                    RNAssistant.Core.Llm.ModelContextBudget.InputBudgetTokens(settings), settings));
            if (note == null || string.IsNullOrWhiteSpace(note.Text)) throw new InvalidOperationException("Нет доступного выделения.");
            var text = "Снимок выделения документа на момент прикрепления.\n" + note.Title + "\n" + note.Reference + "\n\n" + note.Text;
            return new ChatResourceDraftResponse { Resource = _chatResourceIngestion.Stage(session, "Выделение.txt",
                "text/plain", System.Text.Encoding.UTF8.GetBytes(text)) };
        }

        public ChatInboxResponse SubmitChatInput(SubmitChatInputPayload request,
            Action<string, string, string, ChatActivity> progress, Action<ChatStateResponse> changed)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ChatId) ||
                string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length > 128 ||
                !Enum.IsDefined(typeof(InputDelivery), request.Delivery))
                throw new ArgumentException("An explicit chat, operation id and delivery mode are required.");
            if (request.Answers != null && (request.Text != null || (request.ResourceDraftIds?.Count ?? 0) > 0 || request.Delivery != InputDelivery.Steer))
                throw new ArgumentException("Typed answers require their own input without text or attachments.");
            var fingerprint = ConversationInputJournal.Fingerprint(new SubmitChatInputPayload {
                Text = request.Text, Delivery = request.Delivery, Answers = request.Answers,
                ResourceDraftIds = request.ResourceDraftIds ?? new List<string>() });
            var session = LoadInboxSession(request.ChatId);
            ConversationRunPolicy.RequireCurrentSession(session);
            var accepted = _inbox.FindOperation(session, request.OperationId, fingerprint);
            if (accepted == null)
            {
                using (request.Answers == null ? null : ReserveChatOperation(session))
                {
                    if (request.Answers != null) session = ReloadReservedSession(session);
                    var text = request.Answers == null ? request.Text ?? string.Empty :
                        UserQuestionAnswers.Accept(session, request.Answers, _toolExecutor.Payloads);
                    var drafts = _chatResourceIngestion.LoadDrafts(session, request.ResourceDraftIds);
                    if (string.IsNullOrWhiteSpace(text) && drafts.Count == 0)
                        throw new ArgumentException("Сообщение не может быть пустым.");
                    if (drafts.Any(a => a.Status == "error")) throw new InvalidOperationException("Вложение не готово.");
                    var message = new ChatMessage { Attachments = new List<ChatAttachment>(drafts) };
                    _attachmentStore.CommitToCas(message);
                    _inbox.Submit(session, new ConversationInput
                    {
                        Id = Guid.NewGuid().ToString("N"), OperationId = request.OperationId, RequestFingerprint = fingerprint,
                        Text = text, Delivery = request.Delivery, AnswerRunId = request.Answers?.RunId,
                        QuestionSetId = request.Answers?.QuestionSetId, CreatedUtc = DateTime.UtcNow, Attachments = message.Attachments,
                        DocumentRuntimeKey = CaptureExpectedRuntimeDocumentKey(session)
                    });
                    _chatResourceIngestion.DeleteDrafts(message);
                }
                StartInboxWorker(session, progress, changed);
            }
            var response = _inbox.Snapshot(session);
            response.AcceptedInput = _inbox.FindOperation(session, request.OperationId, fingerprint);
            return response;
        }

        public ChatInboxResponse UpdateChatInput(UpdateChatInputPayload request, string action,
            Action<string, string, string, ChatActivity> progress, Action<ChatStateResponse> changed)
        {
            var session = LoadInboxSession(request.ChatId);
            ConversationRunPolicy.RequireCurrentSession(session);
            _inbox.Update(session, request, action);
            StartInboxWorker(session, progress, changed);
            return _inbox.Snapshot(session);
        }
        public ChatInboxResponse ResumeChatInbox(string chatId,
            Action<string, string, string, ChatActivity> progress, Action<ChatStateResponse> changed)
        {
            var session = LoadInboxSession(chatId);
            ConversationRunPolicy.RequireCurrentSession(session);
            _inbox.Resume(session);
            StartInboxWorker(session, progress, changed);
            return _inbox.Snapshot(session);
        }
        private void StartInboxWorker(ChatSession session,
            Action<string, string, string, ChatActivity> progress, Action<ChatStateResponse> changed)
        {
            if (session.FormatVersion != ChatSession.CurrentFormatVersion) return;
            if (_inboxCancellation.IsCancellationRequested || _lifetimeCancellation.IsCancellationRequested || _chatRuns.Get(session.Id) != null || !_inbox.TryStartWorker(session)) return;
            // Owned by the controller lifetime, not by an intake bridge request or selected page.
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_inboxWorkersSync) _inboxWorkers.Add(done.Task);
            Task.Run(async () =>
            {
                try
                {
                    while (!_inboxCancellation.IsCancellationRequested && !_lifetimeCancellation.IsCancellationRequested)
                    {
                        session = LoadAddressedSession(session.Id);
                        ConversationRunPolicy.RequireCurrentSession(session);
                        var item = _inbox.TakeNext(session, HasPendingAgentConfirmation(session));
                        if (item == null) break;
                        if (!string.IsNullOrEmpty(item.DocumentRuntimeKey) &&
                            item.DocumentRuntimeKey != CaptureExpectedRuntimeDocumentKey(session))
                            throw new InvalidOperationException("Исходная сессия документа недоступна. Очередь приостановлена.");
                        if (HasPendingAgentConfirmation(session))
                        {
                            var pending = session.LastRun.KernelState.Summary.PendingConfirmation;
                            var resumedRunId = Guid.NewGuid().ToString("N");
                            await ConfirmAgentToolAsync(pending.PendingId, session.Id,
                                (phase, text, activity) => { if (progress != null) progress(resumedRunId, phase, text, activity); },
                                _inboxCancellation.Token, resumedRunId, changed, true).ConfigureAwait(false);
                            continue;
                        }
                        var runId = Guid.NewGuid().ToString("N");
                        var input = new ChatTurnInput
                        {
                            Text = item.Text, Attachments = item.Attachments, AppendUserMessage = true,
                            CommitUserAttachments = true, InputId = item.Id
                        };
                        // Revalidate after reservation/reload, in the same lease as
                        // the user append. A queued answer cannot follow a mode/history edit.
                        Func<ChatSession, ChatTurnInput> prepare = item.QuestionSetId == null ? null : new Func<ChatSession, ChatTurnInput>(current =>
                        {
                            var questions = UserQuestionAnswers.Pending(current, _toolExecutor.Payloads);
                            if (questions == null || questions.RunId != item.AnswerRunId || questions.QuestionSet.QuestionSetId != item.QuestionSetId)
                                throw new InvalidOperationException("The accepted answer belongs to an earlier question run. Inspect the paused input.");
                            return input;
                        });
                        await ExecuteChatTurnAsync(session, _settingsService.Load(), input, prepare,
                            (phase, text, activity) => { if (progress != null) progress(runId, phase, text, activity); },
                            changed, _inboxCancellation.Token, runId).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    try { _inbox.Pause(session, ex.Message); }
                    catch (Exception storageError) { Diagnostics.RuntimeLog.Error("Queue pause could not be persisted: " + storageError.Message); }
                    Diagnostics.RuntimeLog.Error("Conversation queue paused: " + ex.Message);
                }
                finally
                {
                    try
                    {
                        _inbox.EndWorker(session);
                        if (!_inboxCancellation.IsCancellationRequested && _inbox.HasRunnable(session, HasPendingAgentConfirmation(session)))
                            StartInboxWorker(session, progress, changed);
                        if (changed != null && !_inboxCancellation.IsCancellationRequested) changed(GetChatState(session.Id));
                    }
                    catch (Exception error) { Diagnostics.RuntimeLog.Error("Queue projection failed: " + error.Message); }
                    finally
                    {
                        done.TrySetResult(true);
                        lock (_inboxWorkersSync) _inboxWorkers.Remove(done.Task);
                    }
                }
            });
        }
    }
}
