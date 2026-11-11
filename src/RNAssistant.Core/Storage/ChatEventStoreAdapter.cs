using System;
using System.Linq;
using System.Collections.Generic;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;

namespace RNAssistant.Core.Storage
{
    public sealed class ChatEventStoreAdapter : IEventStore
    {
        private readonly ChatStore _store;

        public ChatEventStoreAdapter(ChatStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public IReadOnlyList<SessionEvent> ReadToolPackAdmissions(ConversationMetadata session, IReadOnlyList<string> runs)
        {
            var selected = new HashSet<string>(runs, StringComparer.OrdinalIgnoreCase);
            var type = SessionEventDescriptors.For(SessionEventKind.ToolPackExtensionAccepted).Type;
            return Read(session, SessionEventReadMode.Validated).Where(item => item.Type == type && selected.Contains(item.RunId)).ToArray();
        }

        public SessionEvent Append(ChatSession session, SessionEventWrite write)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (write == null) throw new ArgumentNullException(nameof(write));
            SessionEventDescriptors.EnsureEventPortWritable(write.Descriptor);
            var correlation = write.Correlation;
            var payload = write.Payload;
            if (payload != null && payload.Bytes != null)
            {
                return _store.AppendTraceBytes(
                    session,
                    write.Descriptor.Type,
                    write.Data,
                    payload.Bytes,
                    payload.ContentType,
                    correlation == null ? null : correlation.RunId,
                    correlation == null ? null : correlation.TurnId,
                    correlation == null ? null : correlation.StepId);
            }
            return _store.AppendTrace(
                session,
                write.Descriptor.Type,
                write.Data,
                payload == null ? null : payload.Text,
                payload == null ? null : payload.ContentType,
                correlation == null ? null : correlation.RunId,
                correlation == null ? null : correlation.TurnId,
                correlation == null ? null : correlation.StepId);
        }

        public IReadOnlyList<SessionEvent> Read(ConversationMetadata session, SessionEventReadMode mode)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (mode == SessionEventReadMode.RequireComplete)
                return _store.ReadCompleteEvents(session.Host, session.DocumentKey, session.Id);
            if (mode == SessionEventReadMode.RequireCompleteInputJournal)
                return _store.ReadCompleteInputJournalEvents(session.Host, session.DocumentKey, session.Id);
            if (mode == SessionEventReadMode.RequireCompleteAgentCommunicationJournal)
                return _store.ReadCompleteInputJournalEvents(session.Host, session.DocumentKey, session.Id, agentCommunication: true);
            if (mode != SessionEventReadMode.Validated)
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported event read mode.");
            return _store.ReadEvents(session.Host, session.DocumentKey, session.Id);
        }

        public ISessionEventReader OpenReader(ConversationMetadata session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            return _store.OpenEventReader(session.Host, session.DocumentKey, session.Id);
        }

        public FinishEvidenceSnapshot ReadFinishEvidence(ChatSession session, string runId, IReadOnlyList<string> references)
        { return _store.ReadFinishEvidence(session, runId, references); }

        public string ReadPayload(ConversationMetadata session, SessionEvent sessionEvent)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (sessionEvent == null) throw new ArgumentNullException(nameof(sessionEvent));
            if (!string.Equals(session.Id, sessionEvent.SessionId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The event payload does not belong to the selected chat session.");
            return _store.ReadEventPayload(sessionEvent);
        }
    }
}
