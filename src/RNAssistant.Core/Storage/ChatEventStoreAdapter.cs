using System;
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

        public IReadOnlyList<SessionEvent> Read(ChatSession session, SessionEventReadMode mode)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (mode == SessionEventReadMode.RequireComplete)
                return _store.ReadCompleteEvents(session.Host, session.DocumentKey, session.Id);
            if (mode != SessionEventReadMode.Validated)
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported event read mode.");
            return _store.ReadEvents(session.Host, session.DocumentKey, session.Id);
        }

        public string ReadPayload(ChatSession session, SessionEvent sessionEvent)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (sessionEvent == null) throw new ArgumentNullException(nameof(sessionEvent));
            if (!string.Equals(session.Id, sessionEvent.SessionId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The event payload does not belong to the selected chat session.");
            return _store.ReadEventPayload(sessionEvent);
        }
    }
}
