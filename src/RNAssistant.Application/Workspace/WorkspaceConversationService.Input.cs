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
            if (request.SteerRunId == null) throw new ArgumentException("An exact active run is required.");
            var duplicate = FindAcceptedInput(workspace, request);
            if (duplicate != null) return duplicate;
            var session = GetSession(workspace, request.SessionId);
            if (session?.LastRun?.RunId != request.SteerRunId ||
                session.LastRun.KernelState?.Summary.Lifecycle != RunLifecycle.Running)
                throw new InvalidOperationException("Запуск уже завершён или изменился. Обновите чат перед отправкой уточнения.");
            cancellationToken.ThrowIfCancellationRequested();
            // Only the lease owner appends intake against its live session cursor.
            // The same pipe is used from a recreated bridge or another process.
            return await WorkspaceRunControl.SendInputAsync(_paths.WorkspaceDirectory, request, cancellationToken).ConfigureAwait(false);
        }

        private IReadOnlyList<WorkspacePendingInput> ReadPendingInputs(ChatSession session)
        {
            return _conversationInbox.ReadUndelivered(session, InputDelivery.Steer)
                .Select(input => new WorkspacePendingInput { Id = input.Id,
                    RunId = input.AcceptedRunId, Text = input.Text }).ToArray();
        }

        private sealed class WorkspaceInputChannel : IRunInputChannel, IDisposable
        {
            private readonly WorkspaceConversationService _owner;
            private readonly ChatSession _session;
            private readonly ConversationInboxService _inbox;
            private readonly ConversationRunInputChannel _channel;
            internal string RunId { get { return _session.LastRun.RunId; } }
            internal bool Accepting { get { return _channel.Accepting; } }
            internal WorkspaceInputChannel(WorkspaceConversationService owner, ChatSession session)
            {
                _owner = owner; _session = session;
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
                if (request.SessionId != _session.Id || request.SteerRunId != RunId)
                    throw new InvalidOperationException("Уточнение адресовано другому запуску.");
                var box = _inbox.Get(_session);
                lock (box.Sync)
                {
                    var fingerprint = InputFingerprint(request);
                    var existing = _inbox.FindOperation(_session, request.RequestId, fingerprint);
                    if (existing != null) return _owner.FindAcceptedInput(_session, request.RequestId, fingerprint);
                    if (request.Mode != null && ChatModes.Require(request.Mode) != _session.Mode ||
                        request.ReasoningEnabled.HasValue && request.ReasoningEnabled != _session.ReasoningEnabled)
                        throw new InvalidOperationException("Уточнение не меняет режим текущего запуска.");
                    _inbox.SubmitToRun(_session, RunId, new ConversationInput {
                        Id = Guid.NewGuid().ToString("N"), OperationId = request.RequestId,
                        AcceptedRunId = RunId, RequestFingerprint = fingerprint,
                        Text = request.Message, Delivery = InputDelivery.Steer, Status = ConversationInputStatus.Pending,
                        CreatedUtc = DateTime.UtcNow, DocumentRuntimeKey = _session.WorkspaceId });
                    return _owner.FindAcceptedInput(_session, request.RequestId, fingerprint);
                }
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
                    var message = new ChatMessage { Id = input.Id, Role = "user", Content = input.Text, RunId = RunId };
                    _session.Messages.Add(message);
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
