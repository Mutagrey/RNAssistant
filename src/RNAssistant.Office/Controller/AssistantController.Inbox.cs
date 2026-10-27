using RNAssistant.Application.Conversations;
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
        public Task InboxWorkersDrained { get { return _conversationApplication.Drained; } }
        public void StopInboxWorkers() { _conversationApplication.CloseAsync(); }
        private bool HasInboxWorkers { get { return _conversationApplication.HasWorkers; } }

        private void EndInboxRun(ChatSession session, ConversationRunHandle lease, ref RunCausalTrace trace)
        {
            try
            {
                var summary = session.LastRun == null || session.LastRun.KernelState == null ? null : session.LastRun.KernelState.Summary;
                _inbox.End(session, summary != null && summary.Lifecycle == RunLifecycle.Completed && summary.ExecutionHealth == ExecutionHealth.Clean,
                    summary != null && summary.Lifecycle == RunLifecycle.AwaitingConfirmation);
            }
            finally { ReleaseControllerRun(lease, ref trace); }
        }

        private static ChatInboxResponse InboxResponse(ConversationInboxSnapshot snapshot)
        {
            return new ChatInboxResponse { ChatId = snapshot.ChatId, Epoch = snapshot.Epoch,
                Revision = snapshot.Revision, Items = snapshot.Items, Paused = snapshot.Paused,
                PauseReason = snapshot.PauseReason, Phase = snapshot.Phase, Running = snapshot.Running,
                Receipts = snapshot.Receipts.Select(item => new ChatInputReceipt { Id = item.Id,
                    OperationId = item.OperationId, Revision = item.Revision, Status = item.Status }).ToList() };
        }

        public ChatInboxResponse GetChatInbox(string chatId, IReadOnlyList<string> receiptOperationIds = null)
        { if (string.IsNullOrWhiteSpace(chatId)) throw new ArgumentException("An explicit chat is required.");
            return InboxResponse(_inbox.Snapshot(LoadAddressedSession(chatId), receiptOperationIds)); }

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
                var run = _activeRuns.Get(chatId);
                if (run != null) _activeRuns.Cancel(chatId, run.RunId);
            }
            return InboxResponse(_inbox.Snapshot(session));
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

        public ChatResourceDraftResponse StageHtmlVisualSelection(StageHtmlVisualSelectionPayload request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ChatId))
                throw new ArgumentException("Требуется чат HTML-preview.");
            var session = LoadAddressedSession(request.ChatId);
            ConversationRunPolicy.RequireCurrentSession(session);
            EnsureCurrentDocument(session);
            return new ChatResourceDraftResponse { Resource = _chatResourceIngestion.StageHtmlVisualSelection(session, request) };
        }

        public ChatInboxResponse SubmitChatInput(SubmitChatInputPayload request,
            Action<string, string, string, ChatActivity> progress, Action<ChatStateResponse> changed)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ChatId) ||
                string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length > 128 ||
                !Enum.IsDefined(typeof(InputDelivery), request.Delivery))
                throw new ArgumentException("An explicit chat, operation id and delivery mode are required.");
            if (request.Answers != null && (request.Text != null || (request.ResourceDraftIds?.Count ?? 0) > 0 ||
                request.Delivery != InputDelivery.Steer || request.SteerRunId != null || request.SteerPendingId != null))
                throw new ArgumentException("Typed answers require their own input without text or attachments.");
            if (request.SteerRunId != null && (string.IsNullOrWhiteSpace(request.SteerRunId) || request.SteerRunId.Length > 128) ||
                request.SteerPendingId != null && (string.IsNullOrWhiteSpace(request.SteerPendingId) || request.SteerPendingId.Length > 128) ||
                request.SteerPendingId != null && request.SteerRunId == null ||
                request.Answers == null && (request.Delivery == InputDelivery.Steer) != (request.SteerRunId != null))
                throw new ArgumentException("Steer requires an exact active run; queue input has no run target.");
            var fingerprint = ConversationInputJournal.Fingerprint(new SubmitChatInputPayload {
                Text = request.Text, Delivery = request.Delivery, Answers = request.Answers,
                SteerRunId = request.SteerRunId, SteerPendingId = request.SteerPendingId,
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
                    text = VisualAnnotationSourceWorkflow.UserText(text, drafts);
                    if (string.IsNullOrWhiteSpace(text) && drafts.Count == 0)
                        throw new ArgumentException("Сообщение не может быть пустым.");
                    if (drafts.Any(a => a.Status == "error")) throw new InvalidOperationException("Вложение не готово.");
                    var message = new ChatMessage { Attachments = new List<ChatAttachment>(drafts) };
                    _attachmentStore.CommitToCas(message);
                    var input = new ConversationInput
                    {
                        Id = Guid.NewGuid().ToString("N"), OperationId = request.OperationId, RequestFingerprint = fingerprint,
                        Text = text, Delivery = request.Delivery, AcceptedRunId = request.SteerRunId,
                        PendingId = request.SteerPendingId, AnswerRunId = request.Answers?.RunId,
                        QuestionSetId = request.Answers?.QuestionSetId, CreatedUtc = DateTime.UtcNow, Attachments = message.Attachments,
                        DocumentRuntimeKey = CaptureExpectedRuntimeDocumentKey(session)
                    };
                    if (request.SteerPendingId != null)
                        _inbox.SubmitToPending(session, request.SteerRunId, request.SteerPendingId, input);
                    else if (request.SteerRunId != null)
                        _inbox.SubmitToRun(session, request.SteerRunId, input);
                    else _inbox.Submit(session, input);
                    _chatResourceIngestion.DeleteDrafts(message);
                }
                StartConversationQueue(session, progress, changed);
            }
            var response = InboxResponse(_inbox.Snapshot(session));
            response.AcceptedInput = _inbox.FindOperation(session, request.OperationId, fingerprint);
            return response;
        }

        public ChatInboxResponse UpdateChatInput(UpdateChatInputPayload request, string action,
            Action<string, string, string, ChatActivity> progress, Action<ChatStateResponse> changed)
        {
            var session = LoadInboxSession(request.ChatId);
            ConversationRunPolicy.RequireCurrentSession(session);
            var updated = _inbox.Update(session, new ConversationInputUpdate { InputId = request.InputId,
                ExpectedRevision = request.ExpectedRevision, Text = request.Text }, action);
            StartConversationQueue(session, progress, changed);
            return InboxResponse(_inbox.Snapshot(session, new[] { updated.OperationId }));
        }
        public ChatInboxResponse ResumeChatInbox(string chatId,
            Action<string, string, string, ChatActivity> progress, Action<ChatStateResponse> changed)
        {
            var session = LoadInboxSession(chatId);
            ConversationRunPolicy.RequireCurrentSession(session);
            _inbox.Resume(session);
            StartConversationQueue(session, progress, changed);
            return InboxResponse(_inbox.Snapshot(session));
        }
        private void StartConversationQueue(ChatSession session,
            Action<string, string, string, ChatActivity> progress, Action<ChatStateResponse> changed)
        {
            _conversationApplication.StartQueue(session, new OfficeQueuePort(this, progress, changed));
        }

        private sealed class OfficeQueuePort : IConversationQueuePort
        {
            private readonly AssistantController _owner;
            private readonly Action<string, string, string, ChatActivity> _progress;
            private readonly Action<ChatStateResponse> _changed;

            internal OfficeQueuePort(AssistantController owner,
                Action<string, string, string, ChatActivity> progress, Action<ChatStateResponse> changed)
            { _owner = owner; _progress = progress; _changed = changed; }

            public bool IsClosing { get { return _owner._lifetimeCancellation.IsCancellationRequested; } }

            public ChatSession Reload(string chatId)
            {
                var session = _owner.LoadAddressedSession(chatId);
                ConversationRunPolicy.RequireCurrentSession(session);
                return session;
            }

            public void ValidateBinding(ChatSession session, ConversationInput input)
            {
                if (!string.IsNullOrEmpty(input.DocumentRuntimeKey) &&
                    input.DocumentRuntimeKey != _owner.CaptureExpectedRuntimeDocumentKey(session))
                    throw new InvalidOperationException("Исходная сессия документа недоступна. Очередь приостановлена.");
            }

            public Task ContinuePendingAsync(ChatSession session, string pendingId, System.Threading.CancellationToken token)
            {
                var runId = session.LastRun.RunId;
                return _owner.ConfirmAgentToolAsync(pendingId, session.Id,
                    (phase, text, activity) => _progress?.Invoke(runId, phase, text, activity),
                    token, runId, _changed, true);
            }

            public Task StartTurnAsync(ChatSession session, ConversationInput item, System.Threading.CancellationToken token)
            {
                var runId = Guid.NewGuid().ToString("N");
                var input = new ChatTurnInput { Text = item.Text, Attachments = item.Attachments,
                    AppendUserMessage = true, CommitUserAttachments = true, InputId = item.Id };
                // Revalidate under the same run lease as the user append.
                Func<ChatSession, ChatTurnInput> prepare = item.QuestionSetId == null ? null :
                    new Func<ChatSession, ChatTurnInput>(current =>
                    {
                        var questions = UserQuestionAnswers.Pending(current, _owner._toolExecutor.Payloads);
                        if (questions == null || questions.RunId != item.AnswerRunId ||
                            questions.QuestionSet.QuestionSetId != item.QuestionSetId)
                            throw new InvalidOperationException("The accepted answer belongs to an earlier question run. Inspect the paused input.");
                        return input;
                    });
                return _owner.ExecuteChatTurnAsync(session, _owner._settingsService.Load(), input, prepare,
                    (phase, text, activity) => _progress?.Invoke(runId, phase, text, activity),
                    _changed, token, runId);
            }

            public void Changed(ChatSession session) { _changed?.Invoke(_owner.GetChatState(session.Id)); }
            public void Failed(Exception error)
            { Diagnostics.RuntimeLog.Error("Conversation queue paused: " + error.Message); }
        }
    }
}
