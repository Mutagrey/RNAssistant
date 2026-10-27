using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal sealed partial class ConversationKernelAdapter
    {
        public CancellationToken InterruptToken { get { return _runInput?.InterruptToken ?? CancellationToken.None; } }
        public bool HasPendingInput { get { return _runInput != null && _runInput.HasPendingInput; } }
        public bool TryCloseInput() { return _runInput == null || _runInput.TryCloseInput(); }
        public Task<IReadOnlyList<AgentMessage>> TakeInputAsync(CancellationToken token)
        {
            return _runInput == null
                ? Task.FromResult<IReadOnlyList<AgentMessage>>(new AgentMessage[0])
                : _runInput.TakeInputAsync(token);
        }

        private async Task<AgentMessage> ApplySteeredInputAsync(ConversationInput input, CancellationToken token)
        {
            var message = _session.Messages.FirstOrDefault(m => m.Id == input.Id);
            if (message == null)
            {
                message = new ChatMessage { Id = input.Id, Role = "user", Content = input.Text,
                    RunId = _session.LastRun.TurnId, Attachments = input.Attachments };
                _session.Messages.Add(message);
                if (_ingestion != null) _ingestion.CommitAndLink(_session, message, _session.Messages.Count - 1);
                Save(appended: new[] { message });
            }
            var routing = AttachmentModelRoutingService.Select(_input.Settings, _session, message.Attachments);
            await _attachments.EnsureAsync(message.Content, _session, message, routing, _progress, token).ConfigureAwait(false);
            _pendingMessageUpdates.Add(message);
            _text = message.Content;
            _steered = true;
            _input = new ConversationRunInput(routing.Settings, _input.Context, _input.Tools, _input.Skills, routing.PrimaryAttachments);
            if (_modelSession != null) _modelSession.ApplyUserInput(message, _input.Attachments);
            return AgentMessage.User(message.Content);
        }
    }
}
