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
        public CancellationToken InterruptToken { get { return _inbox == null ? CancellationToken.None : _inbox.InterruptToken(_session); } }
        public bool HasPendingInput { get { return _inbox != null && _inbox.HasSteer(_session); } }
        public bool TryCloseInput() { return _inbox == null || _inbox.TryClose(_session); }
        public async Task<IReadOnlyList<AgentMessage>> TakeInputAsync(CancellationToken token)
        {
            var messages = new List<AgentMessage>();
            if (_inbox == null) return messages;
            token.ThrowIfCancellationRequested();
            foreach (var input in _inbox.TakeSteer(_session))
            {
                var message = _session.Messages.FirstOrDefault(m => m.Id == input.Id);
                if (message == null)
                {
                    message = new ChatMessage { Id = input.Id, Role = "user", Content = input.Text,
                        RunId = _session.LastRun.TurnId, Attachments = input.Attachments };
                    _session.Messages.Add(message);
                    if (_ingestion != null) _ingestion.CommitAndLink(_session, message, _session.Messages.Count - 1);
                    Save();
                }
                var routing = AttachmentModelRoutingService.Select(_input.Settings, _session, message.Attachments);
                await _attachments.EnsureAsync(message.Content, _session, message, routing, _progress, token).ConfigureAwait(false);
                _text = message.Content;
                _steered = true;
                _input = new ConversationRunInput(routing.Settings, _input.Context, _input.Tools, _input.Skills, routing.PrimaryAttachments);
                if (_modelSession != null) _modelSession.ApplyUserInput(message, _input.Attachments);
                _inbox.Applied(_session, input.Id);
                messages.Add(AgentMessage.User(message.Content));
            }
            if (messages.Count > 0 && _progress != null) _progress("streaming", "", null);
            return messages;
        }
    }
}
