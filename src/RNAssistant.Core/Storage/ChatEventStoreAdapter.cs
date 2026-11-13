using System;
using System.Linq;
using System.Collections.Generic;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Core.Storage
{
    public sealed class ChatEventStoreAdapter : IEventStore
    {
        public bool RetainedReplayGuards { get { return false; } }
        public RNAssistant.Core.Tools.ToolExecutionProgress ReadReplayGuard(ConversationMetadata session,
            RNAssistant.Core.Agent.ToolCall call) { throw new NotSupportedException("Retained replay guards require the journal backend."); }
        public bool AtomicAgentDelivery { get { return false; } }
        public AgentChatAddress FindAgent(ConversationMetadata source, string agentId) { throw new NotSupportedException(); }
        public RNAssistant.Core.Services.ConversationInputRecord AcceptAgentDelivery(ChatSession source, ChatSession recipient,
            AgentDeliveryCommand command, Action dispatch) { throw new NotSupportedException(); }
        public RNAssistant.Core.Services.ConversationInputRecord AcceptAgentSpawn(ChatSession source, ChatSession child,
            AgentSpawnCommand command, Action dispatch) { throw new NotSupportedException(); }
        public RNAssistant.Core.Services.ConversationInputRecord AcceptAgentControl(ChatSession source, ChatSession target,
            AgentControlCommand command, Action dispatch) { throw new NotSupportedException(); }
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

        public void ValidateInputDelivery(ChatSession session, IReadOnlyList<SessionEvent> facts,
            IReadOnlyList<ConversationInputRecord> records)
        {
            var applied = records.Select(record => record.Change.Input)
                .Where(input => input?.Status == ConversationInputStatus.Applied).ToArray();
            if (applied.Length == 0) return;
            var appliedIds = new HashSet<string>(applied.Select(input => input.Id), StringComparer.Ordinal);
            var messages = (session.Messages ?? new List<ChatMessage>())
                .Where(message => message != null && message.Id != null && appliedIds.Contains(message.Id))
                .ToLookup(message => message.Id, StringComparer.Ordinal);
            foreach (var input in applied)
                if (messages[input.Id].Any()) ConversationInputJournal.HasPersistedUserMessage(input, messages[input.Id]);
            var inputBySequence = records.ToDictionary(record => record.Event.Sequence,
                record => record.Change.Input);
            // Check delivery at its event cursor; a later history rewrite may remove the message.
            var persisted = new Dictionary<string, PersistedInputMessage>(StringComparer.Ordinal);
            foreach (var fact in facts)
            {
                if (fact.Type == SessionEventTypes.SessionCommit)
                {
                    var operations = fact.Data?["Operations"] as JArray;
                    foreach (var operation in operations ?? new JArray())
                    {
                        var type = (string)operation["Type"];
                        var data = operation["Data"];
                        if (type == SessionOperationTypes.MessageRemove)
                        {
                            var id = (string)data?["Id"];
                            if (id != null) persisted.Remove(id);
                        }
                        else if (type == SessionOperationTypes.UserMessageAppended ||
                            type == SessionOperationTypes.MessageUpdated ||
                            type == SessionOperationTypes.AssistantMessageAppended ||
                            type == SessionOperationTypes.ToolCallRecorded ||
                            type == SessionOperationTypes.ToolResultRecorded ||
                            type == SessionOperationTypes.ToolExecutionStarted ||
                            type == SessionOperationTypes.ToolExecutionFinished)
                        {
                            var value = data?["Value"];
                            var id = (string)value?["Id"];
                            if (id != null && appliedIds.Contains(id))
                                persisted[id] = new PersistedInputMessage {
                                    Role = (string)value["Role"], ProtocolMessage = (bool?)value["ProtocolMessage"] == true,
                                    RunId = (string)value["RunId"] };
                        }
                    }
                }
                if (fact.Type != SessionEventTypes.ConversationInputChanged) continue;
                var input = inputBySequence[fact.Sequence];
                if (input?.Status != ConversationInputStatus.Applied) continue;
                PersistedInputMessage message;
                if (!persisted.TryGetValue(input.Id, out message) || message.Role != "user" ||
                    message.ProtocolMessage || input.AcceptedRunId != null && message.RunId != input.AcceptedRunId)
                    throw new InvalidOperationException("Applied input has no persisted user message; inspect the conversation without replaying input.");
            }
            foreach (var input in applied)
                if (persisted.ContainsKey(input.Id) != messages[input.Id].Any())
                    throw new InvalidOperationException("Applied input has no persisted user message; inspect the conversation without replaying input.");
        }

        private sealed class PersistedInputMessage
        {
            internal string Role;
            internal bool ProtocolMessage;
            internal string RunId;
        }

        public AgentAssignmentPage ReadAgentAssignments(ConversationMetadata session, bool incoming, string afterRequest)
        { throw new NotSupportedException("The retiring backend uses its explicit assignment-event projection."); }

        public IReadOnlyList<AgentRequestReference> ReadAgentRequests(ConversationMetadata session, IReadOnlyList<string> messageIds)
        {
            if (messageIds == null || messageIds.Count < 1 || messageIds.Count > 32 || messageIds.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Expected 1–32 exact request identities.");
            var ids = new HashSet<string>(messageIds, StringComparer.Ordinal);
            return Read(session, SessionEventReadMode.RequireCompleteAgentCommunicationJournal)
                .Where(item => item.Type == SessionEventTypes.AgentCommunicationChanged && item.Sequence <= session.Revision &&
                    (string)item.Data?["State"] == "Prepared" && ids.Contains((string)item.Data?["MessageId"]))
                .Select(item => new AgentRequestReference((string)item.Data["MessageId"], item.Data["Target"]?.ToObject<AgentChatAddress>(),
                    (bool?)item.Data["ExpectReply"] == true, (bool?)item.Data["Assignment"] == true)).ToArray();
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
