using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Services
{
    // The append-only chat event stream is the only replay authority for optional
    // callable membership. Raw tool-result messages are deliberately not read here.
    internal sealed class ToolPackAdmissionJournal
    {
        private readonly ChatStore _store;
        private readonly ChatSession _session;

        public ToolPackAdmissionJournal(ChatStore store, ChatSession session)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public IReadOnlyList<ToolPackExtensionEventData> ReadAccepted()
        {
            var turnId = CurrentTurnId();
            if (string.IsNullOrWhiteSpace(turnId) || _session.Revision <= 0)
                return new ToolPackExtensionEventData[0];
            var events = _store.ReadEvents(_session.Host, _session.DocumentKey, _session.Id)
                .Where(item => item != null &&
                    string.Equals(item.Type, SessionEventTypes.ToolPackExtensionAccepted, StringComparison.Ordinal) &&
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
            var type = admission.Admitted
                ? SessionEventTypes.ToolPackExtensionAccepted
                : SessionEventTypes.ToolPackExtensionRejected;
            return _store.AppendTrace(_session, type, admission.EventData, null, null,
                run.RunId, CurrentTurnId(), stepId);
        }

        private string CurrentTurnId()
        {
            var run = _session.LastRun;
            return run == null ? null : string.IsNullOrWhiteSpace(run.TurnId) ? run.RunId : run.TurnId;
        }
    }
}
