using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Services
{
    // Shared persistence for existing input transitions. Scheduling and delivery
    // stay with the application owner; this adds no queue, cache or durable store.
    public sealed class ConversationInputJournal
    {
        private readonly IEventStore _events;
        public ConversationInputJournal(IEventStore events)
        { _events = events ?? throw new ArgumentNullException(nameof(events)); }

        public static string Fingerprint<TIntent>(TIntent intent)
        { return TextPatternEngine.Sha256(JsonConvert.SerializeObject(intent)); }

        public static void RequireSameRequest(ConversationInput accepted, string fingerprint)
        {
            if (accepted == null || string.IsNullOrEmpty(accepted.RequestFingerprint) || string.IsNullOrEmpty(fingerprint))
                throw new InvalidOperationException("The original input receipt is unavailable; inspect the conversation without replaying input.");
            if (!string.Equals(accepted.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                throw new ArgumentException("request_id_conflict: this request ID already belongs to different input; use the original request or a new ID.");
        }

        public IReadOnlyList<ConversationInputRecord> Read(ChatSession session, bool includeText = true)
        {
            return _events.Read(session, SessionEventReadMode.RequireComplete)
                .Where(fact => fact.Type == SessionEventTypes.ConversationInputChanged).Select(fact =>
                {
                    var change = fact.Data.ToObject<ConversationInputEvent>();
                    if (change == null) throw new InvalidOperationException("Invalid conversation input event.");
                    if (includeText && change.Input != null)
                        change.Input.Text = _events.ReadPayload(session, fact) ??
                            throw new InvalidOperationException("Conversation input body is missing.");
                    return new ConversationInputRecord(fact, change);
                }).ToArray();
        }

        public SessionEvent Append(ChatSession session, ConversationInputEvent change)
        {
            if (change == null) throw new ArgumentNullException(nameof(change));
            var metadata = JsonConvert.DeserializeObject<ConversationInputEvent>(JsonConvert.SerializeObject(change));
            if (metadata.Input != null) metadata.Input.Text = null;
            var body = change.Input == null ? null : SessionEventPayload.FromText(change.Input.Text ?? string.Empty, "text/plain; charset=utf-8");
            return _events.Append(session, new SessionEventWrite(
                SessionEventDescriptors.For(SessionEventKind.ConversationInputChanged), metadata, body,
                change.Input?.AcceptedRunId == null ? null : new SessionEventCorrelation(change.Input.AcceptedRunId, change.Input.AcceptedRunId, null)));
        }
    }

    public sealed class ConversationInputRecord
    {
        public SessionEvent Event { get; private set; }
        public ConversationInputEvent Change { get; private set; }
        internal ConversationInputRecord(SessionEvent fact, ConversationInputEvent change)
        { Event = fact; Change = change; }
    }
}
