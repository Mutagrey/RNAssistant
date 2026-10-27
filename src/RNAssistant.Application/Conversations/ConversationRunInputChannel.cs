using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;

namespace RNAssistant.Application.Conversations
{
    // One kernel input boundary for both environments. Domain callbacks must
    // persist the exact user message before the journal is marked applied.
    internal sealed class ConversationRunInputChannel : IRunInputChannel, IDisposable
    {
        private readonly ConversationInboxService _inbox;
        private readonly ChatSession _session;
        private readonly Func<ConversationInput, CancellationToken, Task<AgentMessage>> _apply;
        private readonly Action _onConsumed;
        private readonly bool _ownsBinding;

        internal ConversationRunInputChannel(ConversationInboxService inbox, ChatSession session,
            Func<ConversationInput, CancellationToken, Task<AgentMessage>> apply,
            Action onConsumed = null, bool ownsBinding = false)
        {
            _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _apply = apply ?? throw new ArgumentNullException(nameof(apply));
            _onConsumed = onConsumed;
            _ownsBinding = ownsBinding;
            if (_ownsBinding) _inbox.Bind(_session);
        }

        internal bool Accepting
        {
            get
            {
                var box = _inbox.Get(_session);
                lock (box.Sync) return box.Accepting &&
                    _session.LastRun?.KernelState?.Summary.Lifecycle == RunLifecycle.Running;
            }
        }

        public CancellationToken InterruptToken { get { return _inbox.InterruptToken(_session); } }
        public bool HasPendingInput { get { return _inbox.HasSteer(_session); } }
        public bool TryCloseInput() { return _inbox.TryClose(_session); }

        public async Task<IReadOnlyList<AgentMessage>> TakeInputAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var messages = new List<AgentMessage>();
            foreach (var input in _inbox.TakeSteer(_session))
            {
                var message = await _apply(input, token).ConfigureAwait(false);
                if (message == null) throw new InvalidOperationException("Input preparation returned no user message.");
                _inbox.Applied(_session, input.Id);
                messages.Add(message);
            }
            if (messages.Count > 0) _onConsumed?.Invoke();
            return messages;
        }

        public void Dispose()
        {
            if (!_ownsBinding) return;
            try
            {
                var summary = _session.LastRun?.KernelState?.Summary;
                _inbox.End(_session, summary?.Lifecycle == RunLifecycle.Completed &&
                    summary.ExecutionHealth == ExecutionHealth.Clean,
                    summary?.Lifecycle == RunLifecycle.AwaitingConfirmation);
            }
            finally
            {
                var box = _inbox.Get(_session);
                lock (box.Sync) box.Interrupt.Dispose();
            }
        }
    }
}
