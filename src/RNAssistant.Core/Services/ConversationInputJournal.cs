using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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

        public IReadOnlyList<ConversationInputRecord> Read(ChatSession session, bool includeText = true,
            long? upToSequence = null)
        {
            if (upToSequence < 0) throw new ArgumentOutOfRangeException(nameof(upToSequence));
            var facts = _events.Read(session, SessionEventReadMode.RequireComplete)
                .Where(fact => !upToSequence.HasValue || fact.Sequence <= upToSequence.Value).ToArray();
            var records = facts.Where(fact => fact.Type == SessionEventTypes.ConversationInputChanged).Select(fact =>
                {
                    var change = fact.Data.ToObject<ConversationInputEvent>();
                    if (change == null) throw new InvalidOperationException("Invalid conversation input event.");
                    if (includeText && change.Input != null)
                        change.Input.Text = _events.ReadPayload(session, fact) ??
                            throw new InvalidOperationException("Conversation input body is missing.");
                    return new ConversationInputRecord(fact, change);
                }).ToArray();
            RequireStableIdentity(records);
            var applied = records.Select(record => record.Change.Input)
                .Where(input => input?.Status == ConversationInputStatus.Applied).ToArray();
            if (applied.Length == 0) return records;
            var appliedIds = new HashSet<string>(applied.Select(input => input.Id), StringComparer.Ordinal);
            var messages = (session.Messages ?? new List<ChatMessage>())
                .Where(message => message != null && message.Id != null && appliedIds.Contains(message.Id))
                .ToLookup(message => message.Id, StringComparer.Ordinal);
            foreach (var input in applied)
                if (messages[input.Id].Any()) HasPersistedUserMessage(input, messages[input.Id]);
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
            return records;
        }

        private sealed class PersistedInputMessage
        {
            internal string Role;
            internal bool ProtocolMessage;
            internal string RunId;
        }

        // Missing is recoverable only through explicit input resume. A colliding
        // message ID is not proof of delivery and must never become a receipt.
        public static bool HasPersistedUserMessage(ChatSession session, ConversationInput input)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (input == null || string.IsNullOrWhiteSpace(input.Id)) throw new ArgumentException("Input ID is required.", nameof(input));
            return HasPersistedUserMessage(input, (session.Messages ?? new List<ChatMessage>())
                .Where(message => message != null && message.Id == input.Id));
        }

        private static bool HasPersistedUserMessage(ConversationInput input, IEnumerable<ChatMessage> matches)
        {
            ChatMessage found = null;
            foreach (var message in matches)
            {
                if (found != null)
                    throw new InvalidOperationException("Input ID matches multiple messages; inspect the conversation without replaying input.");
                found = message;
            }
            if (found == null) return false;
            if (found.Role != "user" || found.ProtocolMessage ||
                input.AcceptedRunId != null && found.RunId != input.AcceptedRunId)
                throw new InvalidOperationException("Input ID does not identify its persisted user message; inspect the conversation without replaying input.");
            return true;
        }

        private static void RequireStableIdentity(IEnumerable<ConversationInputRecord> records)
        {
            var byId = new Dictionary<string, ConversationInput>(StringComparer.Ordinal);
            var byOperation = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var record in records)
            {
                var input = record.Change.Input;
                if (input == null) continue;
                if (string.IsNullOrWhiteSpace(input.Id) || string.IsNullOrWhiteSpace(input.OperationId))
                    throw new InvalidOperationException("Accepted input identity is incomplete; inspect the conversation without replaying input.");
                ConversationInput prior;
                if (byId.TryGetValue(input.Id, out prior))
                {
                    if (prior.OperationId != input.OperationId ||
                        prior.RequestFingerprint != input.RequestFingerprint ||
                        prior.AcceptedRunId != input.AcceptedRunId ||
                        prior.PendingId != input.PendingId ||
                        prior.AnswerRunId != input.AnswerRunId ||
                        prior.QuestionSetId != input.QuestionSetId ||
                        prior.DocumentRuntimeKey != input.DocumentRuntimeKey)
                        throw new InvalidOperationException("Accepted input identity changed; inspect the conversation without replaying input.");
                }
                else byId.Add(input.Id, input);
                string priorId;
                if (byOperation.TryGetValue(input.OperationId, out priorId))
                {
                    if (priorId != input.Id)
                        throw new InvalidOperationException("An operation ID belongs to multiple inputs; inspect the conversation without replaying input.");
                }
                else byOperation.Add(input.OperationId, input.Id);
            }
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
