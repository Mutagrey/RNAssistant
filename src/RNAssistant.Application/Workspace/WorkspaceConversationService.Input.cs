using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Application.Conversations;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Runtime
{
    public sealed partial class WorkspaceConversationService
    {
        private readonly ConcurrentDictionary<string, WorkspaceInputChannel> _inputChannels =
            new ConcurrentDictionary<string, WorkspaceInputChannel>(StringComparer.Ordinal);

        internal IReadOnlyList<ConversationActiveSnapshot> ActiveChatRuns()
        { return _activeRuns.Active().Where(run => !string.IsNullOrWhiteSpace(run.WorkspaceId)).ToArray(); }

        internal bool StopActiveChatRun(string workspaceId, string sessionId, string runId)
        {
            var run = _activeRuns.Get(sessionId);
            if (run == null || run.RunId != runId || run.WorkspaceId != workspaceId || !_activeRuns.Cancel(sessionId, runId)) return false;
            WorkspaceInputChannel channel;
            if (_inputChannels.TryGetValue(sessionId, out channel))
                _conversationInbox.PauseForRun(channel.Session, runId);
            return true;
        }

        private bool CanSteer(ChatSession session)
        {
            WorkspaceInputChannel channel;
            return _inputChannels.TryGetValue(session.Id, out channel) &&
                channel.RunId == session.LastRun?.RunId && channel.Accepting;
        }

        public async Task<WorkspaceInputReceipt> SteerInputAsync(WorkspaceDescriptor workspace,
            WorkspaceSubmitInputRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            ValidateInput(request);
            if (request.SteerRunId == null && !request.Enqueue) throw new ArgumentException("An active input delivery is required.");
            var duplicate = FindAcceptedInput(workspace, request);
            if (duplicate != null) return duplicate;
            var session = GetSession(workspace, request.SessionId);
            if (session?.LastRun == null || !request.Enqueue && session.LastRun.RunId != request.SteerRunId ||
                session.LastRun.KernelState?.Summary.Lifecycle != RunLifecycle.Running)
                throw new InvalidOperationException("Запуск уже завершён или изменился. Обновите чат перед отправкой уточнения.");
            cancellationToken.ThrowIfCancellationRequested();
            // Only the lease owner appends intake against its live session cursor.
            // The same pipe is used from a recreated bridge or another process.
            return await WorkspaceRunControl.SendInputAsync(_paths.WorkspaceDirectory, request,
                cancellationToken, session.LastRun.RunId).ConfigureAwait(false);
        }

        private IReadOnlyList<WorkspacePendingInput> ReadPendingInputs(ChatSession session)
        {
            return _conversationInbox.ReadUndelivered(session)
                .Select(input => new WorkspacePendingInput { Id = input.Id,
                    RunId = input.AcceptedRunId, Text = input.Text, Revision = input.Revision,
                    Delivery = input.Delivery, Status = input.Status, Attachments = input.Attachments }).ToArray();
        }

        private void StartQueuedInputs(WorkspaceDescriptor workspace, string sessionId, AppSettings settings,
            Func<string> key, Action<WorkspaceRunEvent> progress, string modelDigest)
        {
            // Completion with no pending work must not reload the whole transcript.
            var session = _conversationInbox.RunnableSession(sessionId, workspace.WorkspaceId);
            if (session != null) _conversationApplication.StartQueue(session,
                new WorkspaceQueuePort(this, workspace, settings.Clone(), key, progress, modelDigest));
        }

        internal async Task UpdateInboxAsync(WorkspaceDescriptor workspace, WorkspaceInboxCommand command,
            AppSettings settings, Func<string> key, Action<WorkspaceRunEvent> progress, CancellationToken token)
        {
            ValidateInbox(command);
            var session = GetSession(workspace, command.SessionId);
            if (session == null) throw new ArgumentException("Чат проекта не найден.");
            if (session.LastRun?.KernelState?.Summary.Lifecycle == RunLifecycle.Running)
            {
                await WorkspaceRunControl.SendInboxAsync(_paths.WorkspaceDirectory, session.LastRun.RunId, command, token).ConfigureAwait(false);
                return;
            }
            using (ReserveRun(session.Id, false, token))
            {
                session = GetSession(workspace, command.SessionId);
                ChangeInbox(session, command);
            }
            StartQueuedInputs(workspace, command.SessionId, settings, key, progress, null);
        }

        private static void ValidateInbox(WorkspaceInboxCommand command)
        {
            if (command == null || string.IsNullOrWhiteSpace(command.SessionId) ||
                !Enum.IsDefined(typeof(WorkspaceInboxAction), command.Action) ||
                (command.Text?.Length ?? 0) > 128 * 1024 ||
                command.Action != WorkspaceInboxAction.Resume && (string.IsNullOrWhiteSpace(command.InputId) || command.ExpectedRevision < 1) ||
                command.Action == WorkspaceInboxAction.Resume && command.InputId != null)
                throw new ArgumentException("Требуется точное сообщение очереди и его версия.");
        }

        private WorkspaceInboxReceipt ChangeInbox(ChatSession session, WorkspaceInboxCommand command)
        {
            ValidateInbox(command);
            if (session == null || command.SessionId != session.Id) throw new ArgumentException("Очередь принадлежит другому чату.");
            ConversationRunPolicy.RequireCurrentSession(session);
            if (command.Action == WorkspaceInboxAction.Resume)
            {
                _conversationInbox.Resume(session);
                return new WorkspaceInboxReceipt { SessionId = session.Id, Revision = session.Revision };
            }
            var input = _conversationInbox.Find(session, command.InputId);
            if (command.Action == WorkspaceInboxAction.Steer && input?.AcceptedRunId != null && input.AcceptedRunId != session.LastRun?.RunId)
                throw new InvalidOperationException("Уточнение принадлежит предыдущему запуску. Вставьте его в новое сообщение.");
            var changed = _conversationInbox.Update(session, new ConversationInputUpdate { InputId = command.InputId,
                ExpectedRevision = command.ExpectedRevision, Text = command.Text }, command.Action.ToString().ToLowerInvariant());
            return new WorkspaceInboxReceipt { SessionId = session.Id, InputId = changed.Id, Revision = changed.Revision };
        }

        private sealed class WorkspaceQueuePort : IConversationQueuePort
        {
            private readonly WorkspaceConversationService _owner;
            private readonly WorkspaceDescriptor _workspace;
            private readonly AppSettings _settings;
            private readonly Func<string> _key;
            private readonly Action<WorkspaceRunEvent> _progress;
            private readonly string _digest;
            internal WorkspaceQueuePort(WorkspaceConversationService owner, WorkspaceDescriptor workspace,
                AppSettings settings, Func<string> key, Action<WorkspaceRunEvent> progress, string digest)
            { _owner = owner; _workspace = workspace; _settings = settings; _key = key; _progress = progress; _digest = digest; }
            public bool IsClosing { get { return _owner._titleLifetime.IsCancellationRequested; } }
            public ChatSession Reload(string id) { return _owner.GetSession(_workspace, id); }
            public void ValidateBinding(ChatSession session, ConversationInput input)
            {
                if (session == null || session.WorkspaceId != _workspace.WorkspaceId ||
                    input.DocumentRuntimeKey != _workspace.WorkspaceId)
                    throw new InvalidOperationException("Сообщение очереди принадлежит другому проекту.");
                ConversationRunPolicy.RequireCurrentSession(session);
            }
            public async Task StartTurnAsync(ChatSession session, ConversationInput input, CancellationToken token)
            {
                using (_owner._runtimeUseLease?.Invoke())
                    await _owner.ExecuteInputAsync(_workspace, new WorkspaceSubmitInputRequest {
                        SessionId = session.Id, RequestId = input.OperationId, Message = input.Text },
                        _settings.Clone(), _key, _progress, token, _digest, queuedInput: input).ConfigureAwait(false);
            }
            public Task ContinuePendingAsync(ChatSession session, string pendingId, CancellationToken token)
            { throw new InvalidOperationException("Сначала подтвердите или отклоните ожидающее действие."); }
            public void Changed(ChatSession session)
            { _progress?.Invoke(new WorkspaceRunEvent("QueueChanged", session.LastRun?.KernelState?.Summary,
                null, ConversationEventFeed.Cursor(session))); }
            public void Failed(Exception error)
            { System.Diagnostics.Trace.TraceError("Workspace queue paused: {0}", error.Message); }
        }

        private sealed class WorkspaceInputChannel : IRunInputChannel, IDisposable
        {
            private readonly WorkspaceConversationService _owner;
            private readonly ChatSession _session;
            private readonly WorkspaceDescriptor _workspace;
            private readonly ConversationInboxService _inbox;
            private readonly ConversationRunInputChannel _channel;
            internal string RunId { get { return _session.LastRun.RunId; } }
            internal ChatSession Session { get { return _session; } }
            internal bool Accepting { get { return _channel.Accepting; } }
            internal WorkspaceInputChannel(WorkspaceConversationService owner, ChatSession session, WorkspaceDescriptor workspace)
            {
                _owner = owner; _session = session; _workspace = workspace;
                _inbox = owner._conversationInbox;
                _channel = new ConversationRunInputChannel(_inbox, session, ApplyInputAsync,
                    () => _owner.SetModelProgress(_session.Id, null), ownsBinding: true);
                if (!owner._inputChannels.TryAdd(session.Id, this))
                {
                    _channel.Dispose();
                    throw new InvalidOperationException("The workspace input channel is already owned.");
                }
            }

            internal WorkspaceInputReceipt Submit(WorkspaceSubmitInputRequest request)
            {
                ValidateInput(request);
                if (request.SessionId != _session.Id || !request.Enqueue && request.SteerRunId != RunId || !Accepting)
                    throw new InvalidOperationException("Уточнение адресовано другому запуску.");
                var fingerprint = InputFingerprint(request);
                var existing = _inbox.FindOperation(_session, request.RequestId, fingerprint);
                if (existing != null) return _owner.FindAcceptedInput(_session, request.RequestId, fingerprint);
                if (request.Mode != null && ChatModes.Require(request.Mode) != _session.Mode ||
                    request.ReasoningEnabled.HasValue && request.ReasoningEnabled != _session.ReasoningEnabled)
                    throw new InvalidOperationException("Уточнение не меняет режим текущего запуска.");
                var store = new AttachmentStore(_owner._paths);
                var drafts = _owner.LoadResourceDrafts(_workspace, _session, request.ResourceDraftIds ?? new List<string>(), store);
                var message = new ChatMessage { Attachments = drafts };
                store.CommitToCas(message);
                var input = new ConversationInput {
                    Id = Guid.NewGuid().ToString("N"), OperationId = request.RequestId,
                    AcceptedRunId = request.Enqueue ? null : RunId, RequestFingerprint = fingerprint,
                    Text = VisualAnnotationSourceWorkflow.UserText(request.Message, drafts),
                    Delivery = request.Enqueue ? InputDelivery.Queue : InputDelivery.Steer, Status = ConversationInputStatus.Pending,
                    Attachments = message.Attachments, CreatedUtc = DateTime.UtcNow, DocumentRuntimeKey = _session.WorkspaceId };
                if (request.Enqueue) _inbox.SubmitQueuedToRun(_session, RunId, input);
                else _inbox.SubmitToRun(_session, RunId, input);
                store.DeleteDrafts(message);
                return _owner.FindAcceptedInput(_session, request.RequestId, fingerprint);
            }

            public CancellationToken InterruptToken { get { return _channel.InterruptToken; } }
            public bool HasPendingInput { get { return _channel.HasPendingInput; } }
            public bool TryCloseInput() { return _channel.TryCloseInput(); }
            public Task<IReadOnlyList<AgentMessage>> TakeInputAsync(CancellationToken token)
            { return _channel.TakeInputAsync(token); }

            private Task<AgentMessage> ApplyInputAsync(ConversationInput input, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (!ConversationInputJournal.HasPersistedUserMessage(_session, input))
                {
                    var message = new ChatMessage { Id = input.Id, Role = "user", Content = input.Text, RunId = RunId,
                        Attachments = input.Attachments };
                    _session.Messages.Add(message);
                    LinkInputAttachments(_session, message);
                    _owner._chats.Commit(_session, ConversationCommit.UserInput(message));
                }
                return Task.FromResult(AgentMessage.User(input.Text));
            }

            public void Dispose()
            {
                try { _channel.Dispose(); }
                finally
                {
                    WorkspaceInputChannel removed; _owner._inputChannels.TryRemove(_session.Id, out removed);
                }
            }
        }
    }
}
