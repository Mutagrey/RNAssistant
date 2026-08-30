using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;

namespace RNAssistant.Office.Services
{
    // The append-only chat event stream is the only replay authority for optional
    // callable membership. Raw tool-result messages are deliberately not read here.
    internal sealed class ToolPackAdmissionJournal
    {
        private readonly IEventStore _events;
        private readonly ChatSession _session;

        public ToolPackAdmissionJournal(IEventStore eventStore, ChatSession session)
        {
            _events = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public IReadOnlyList<ToolPackExtensionEventData> ReadAccepted()
        {
            var turnId = CurrentTurnId();
            if (string.IsNullOrWhiteSpace(turnId) || _session.Revision <= 0)
                return new ToolPackExtensionEventData[0];
            var accepted = SessionEventDescriptors.For(SessionEventKind.ToolPackExtensionAccepted);
            var events = _events.Read(_session, SessionEventReadMode.Validated)
                .Where(item => item != null &&
                    string.Equals(item.Type, accepted.Type, StringComparison.Ordinal) &&
                    string.Equals(item.TurnId, turnId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.Sequence)
                .ToList();
            var result = new List<ToolPackExtensionEventData>(events.Count);
            foreach (var item in events)
            {
                ToolPackExtensionEventData data;
                try
                {
                    data = item.Data == null ? null : item.Data.ToObject<ToolPackExtensionEventData>();
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException("A durable tool-pack admission event is malformed.", ex);
                }
                if (data == null || !data.Admitted)
                    throw new InvalidOperationException("A durable accepted tool-pack event has an invalid outcome.");
                result.Add(data);
            }
            return result;
        }

        public SessionEvent Append(ToolPackAdmission admission, string stepId)
        {
            if (admission == null || admission.EventData == null)
                throw new ArgumentNullException(nameof(admission));
            var run = _session.LastRun;
            if (run == null || string.IsNullOrWhiteSpace(run.RunId) || string.IsNullOrWhiteSpace(CurrentTurnId()))
            {
                throw new InvalidOperationException("A durable tool-pack admission requires an active run and turn.");
            }
            var descriptor = SessionEventDescriptors.For(admission.Admitted
                ? SessionEventKind.ToolPackExtensionAccepted
                : SessionEventKind.ToolPackExtensionRejected);
            return _events.Append(_session, new SessionEventWrite(
                descriptor,
                admission.EventData,
                null,
                new SessionEventCorrelation(run.RunId, CurrentTurnId(), stepId)));
        }

        private string CurrentTurnId()
        {
            var run = _session.LastRun;
            return run == null ? null : string.IsNullOrWhiteSpace(run.TurnId) ? run.RunId : run.TurnId;
        }
    }
}
