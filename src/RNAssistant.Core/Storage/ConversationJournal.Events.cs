using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.Data.Sqlite;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ConversationJournal : IEventStore
    {
        SessionEvent IEventStore.Append(ChatSession session, SessionEventWrite write)
        {
            if (write == null) throw new ArgumentNullException(nameof(write));
            SessionEventDescriptors.EnsureEventPortWritable(write.Descriptor);
            if (write.Descriptor.Kind == SessionEventKind.ConversationInputChanged)
            {
                var change = write.Data as ConversationInputEvent;
                if (change == null || change.AgentPause != null || (change.Input == null ? write.Payload != null || write.Correlation != null :
                    write.Payload?.Text == null || write.Payload.Bytes != null || change.Input.Text != null ||
                    write.Correlation?.RunId != change.Input.AcceptedRunId || write.Correlation?.TurnId != change.Input.AcceptedRunId))
                    throw TextStoreScope();
                RequireTextRoot();
                var body = change.Input == null ? null : PayloadRef.FromBlob(_bodies.StoreText(write.Payload.Text, "text/plain"));
                var receipt = CommitChat(session, new ConversationCommit(), queueControl: change, inputBody: body);
                ApplyChatReceipt(session, receipt);
                return ProjectChatEvent(receipt);
            }
            var trace = write.Data as LlmTraceRecord;
            var admission = write.Data as ToolPackExtensionEventData;
            var admissionKind = write.Descriptor.Kind == SessionEventKind.ToolPackExtensionAccepted ||
                write.Descriptor.Kind == SessionEventKind.ToolPackExtensionRejected;
            if (write.Payload != null || (admissionKind ? admission == null : trace == null) ||
                trace?.PayloadUtf8Bytes != null || trace?.PayloadJson != null)
                throw TextStoreScope();
            if (session?.LastRun == null || trace != null && (trace.SessionId != session.Id || trace.RunId != session.LastRun.RunId) ||
                write.Correlation?.RunId != session.LastRun.RunId || write.Correlation.TurnId != session.LastRun.TurnId)
                throw new InvalidOperationException("Model event belongs to another run.");
            RequireTextRoot();
            using (RuntimeStorageActivity.Acquire(_paths))
            {
                var item = new ConversationModelEvent(write.Descriptor.Kind, write.Correlation.RunId,
                    write.Correlation.TurnId, write.Correlation.StepId, StoreChatJson(write.Data));
                var command = new ConversationCommit();
                ConversationChatReceipt receipt;
                try { receipt = CommitChat(session, command, item); }
                catch (IOException)
                {
                    var own = Access((db, cursor) => FindChatWrite(db, command.OperationId), CancellationToken.None);
                    if (own?.Fact.Card.ChatId == session.Id) ApplyChatReceipt(session, own);
                    throw;
                }
                ApplyChatReceipt(session, receipt);
                return ProjectChatEvent(receipt);
            }
        }

        private SessionEvent ProjectChatEvent(ConversationChatReceipt receipt)
        {
            var item = receipt.Fact.ModelEvent;
            if (receipt.Fact.Control != null) return new SessionEvent {
                SessionId = receipt.Fact.Card.ChatId, Sequence = receipt.Position.Sequence,
                EventId = receipt.Operation, CreatedUtc = receipt.Fact.CreatedUtc, Hash = receipt.Position.Hash,
                Type = SessionEventTypes.ConversationInputChanged, Payload = receipt.Fact.Input?.Body.ToBlobReference(),
                RunId = receipt.Fact.Input?.RunId, TurnId = receipt.Fact.Input?.RunId,
                Data = JObject.FromObject(new ConversationInputEvent { Input = receipt.Fact.Input?.Header, Paused = receipt.Fact.Control.Paused, PauseReason = receipt.Fact.Control.Reason }) };
            return new SessionEvent { SessionId = receipt.Fact.Card.ChatId, Sequence = receipt.Position.Sequence,
                EventId = receipt.Operation, CreatedUtc = receipt.Fact.CreatedUtc, Hash = receipt.Position.Hash,
                Type = item == null ? "chat.committed" : SessionEventDescriptors.For(item.Kind).Type,
                RunId = item?.RunId ?? receipt.Fact.Run?.RunId, TurnId = item?.TurnId ?? receipt.Fact.Run?.TurnId, StepId = item?.StepId,
                Data = item == null ? JObject.FromObject(receipt.Fact) : JObject.Parse(ReadChatBody(item.Body)) };
        }

        IReadOnlyList<SessionEvent> IEventStore.ReadToolPackAdmissions(ConversationMetadata session, IReadOnlyList<string> runs)
        {
            if (runs == null) throw new ArgumentNullException(nameof(runs));
            RequireTextRoot();
            using (RuntimeStorageActivity.Acquire(_paths))
            {
                var receipts = Access((db, cursor) => {
                    RequireChatRevision(session, TextChat(db, session.Id));
                    var operations = new List<Tuple<string, string>>();
                    foreach (var run in runs.Distinct(StringComparer.Ordinal))
                        using (var command = Command(db, "SELECT operation FROM chat_event WHERE stream=$stream AND chat=$chat AND kind=$kind AND run=$run ORDER BY sequence"))
                        {
                            command.Parameters.AddWithValue("$chat", session.Id); command.Parameters.AddWithValue("$run", run);
                            command.Parameters.AddWithValue("$kind", (int)SessionEventKind.ToolPackExtensionAccepted);
                            using (var reader = command.ExecuteReader()) while (reader.Read()) operations.Add(Tuple.Create(run, reader.GetString(0)));
                        }
                    return operations.Select(operation => {
                        var receipt = FindChatWrite(db, operation.Item2);
                        if (receipt?.Fact.Card.ChatId != session.Id || receipt.Fact.ModelEvent?.RunId != operation.Item1 ||
                            receipt.Fact.ModelEvent.Kind != SessionEventKind.ToolPackExtensionAccepted)
                            throw new InvalidDataException("Tool admission locator mismatch.");
                        return receipt;
                    }).OrderBy(item => item.Position.Sequence).ToArray();
                }, CancellationToken.None);
                return receipts.Select(ProjectChatEvent).ToArray();
            }
        }

        private static void RequireChatRevision(ConversationMetadata metadata, ConversationChatReceipt chat)
        {
            if (metadata == null || chat == null || metadata.Id != chat.Fact.Card.ChatId || metadata.Revision != chat.Position.Sequence ||
                !MatchesAddress(chat, metadata.Host, metadata.DocumentKey) || metadata.WorkspaceId != chat.Fact.Card.Address.WorkspaceId ||
                metadata.DocumentAuthorityId != chat.Fact.Card.Address.DocumentAuthorityId)
                throw new ChatConcurrencyException("The event read requires this exact chat revision and scope.");
        }

        FinishEvidenceSnapshot IEventStore.ReadFinishEvidence(ChatSession session, string runId, IReadOnlyList<string> references)
        {
            return Access((db, cursor) => {
                var current = TextChat(db, session.Id); RequireChatRevision(ConversationMetadata.From(session), current);
                var run = CurrentRun(db, session.Id);
                if (run == null || run.Fact.RunId != runId || current.Fact.Run?.RunId != runId)
                    throw new InvalidOperationException("Finish evidence belongs to another run.");
                var calls = new HashSet<Tuple<string, string>>();
                using (var command = Command(db, "SELECT call FROM call_fact WHERE stream=$stream AND chat=$chat AND run=$run"))
                {
                    command.Parameters.AddWithValue("$chat", session.Id); command.Parameters.AddWithValue("$run", runId);
                    using (var reader = command.ExecuteReader()) while (reader.Read()) calls.Add(Tuple.Create(runId, reader.GetString(0)));
                }
                foreach (var alias in references)
                {
                    string normalized;
                    if (!ModelReference.TryNormalize(alias, out normalized) || normalized[0] != 'E') continue;
                    using (var command = Command(db, "SELECT operation FROM result_fact WHERE stream=$stream AND chat=$chat AND ordinal=$ordinal"))
                    {
                        command.Parameters.AddWithValue("$chat", session.Id);
                        command.Parameters.AddWithValue("$ordinal", new ModelReference(ModelReferenceKind.Evidence, normalized).Ordinal);
                        var result = Find(db, command.ExecuteScalar() as string)?.Fact;
                        if (result == null) continue;
                        if (result.ChatId != session.Id || result.Result.EvidenceReference?.Alias != normalized)
                            throw new InvalidDataException("Evidence alias locator mismatch.");
                        calls.Add(Tuple.Create(result.Result.RunId, result.Result.ToolCallId));
                    }
                }
                var facts = new List<ConversationFinishEvidenceAdmission.Fact>();
                foreach (var call in calls) AddFinishFacts(db, session.Id, call.Item1, call.Item2, facts);
                var retained = facts.Select(item => item.Message.Id).Distinct(StringComparer.Ordinal)
                    .Where(id => ChatMessageOperation(db, session.Id, id) != null).ToArray();
                return new FinishEvidenceSnapshot(new SessionEventPosition(session.Id, current.Position.Sequence, current.Position.Hash),
                    facts, retained);
            }, CancellationToken.None);
        }

        private void AddFinishFacts(SqliteConnection db, string chat, string run, string call,
            List<ConversationFinishEvidenceAdmission.Fact> facts)
        {
            var accepted = FindCall(db, chat, run, call) ?? throw new InvalidDataException("Missing accepted call.");
            var frame = FindChatWrite(db, accepted.Operation) ?? throw TextStoreScope();
            facts.Add(new ConversationFinishEvidenceAdmission.Fact { Kind = SessionOperationTypes.ToolCallRecorded,
                RunId = run, TurnId = frame.Fact.Run.TurnId, Message = accepted.Call, Sequence = frame.Position.Sequence });
            foreach (var operation in new[] { StartedOperation(db, chat, run, call), TerminalOperation(db, chat, run, call) as string })
            {
                if (operation == null) continue;
                var receipt = FindChatWrite(db, operation) ?? throw TextStoreScope();
                var tools = receipt.Fact.Tools;
                if (receipt.Fact.Card.ChatId != chat || receipt.Fact.Run?.RunId != run || tools?.CallId != call)
                    throw new InvalidDataException("Tool lifecycle locator mismatch.");
                var message = receipt.Fact.Messages.Single(item => item.Header.Id == tools.ActivityId).Header;
                facts.Add(new ConversationFinishEvidenceAdmission.Fact {
                    Kind = tools.Terminal == null ? SessionOperationTypes.ToolExecutionStarted : SessionOperationTypes.ToolExecutionFinished,
                    RunId = run, TurnId = receipt.Fact.Run.TurnId, Message = message, Sequence = receipt.Position.Sequence });
            }
        }

        IReadOnlyList<SessionEvent> IEventStore.Read(ConversationMetadata session, SessionEventReadMode mode)
        {
            if (mode != SessionEventReadMode.Validated && mode != SessionEventReadMode.RequireComplete && mode != SessionEventReadMode.RequireCompleteInputJournal) throw TextStoreScope();
            RequireTextRoot();
            using (RuntimeStorageActivity.Acquire(_paths))
            {
                var receipts = Access((db, cursor) => {
                    if (mode == SessionEventReadMode.RequireCompleteInputJournal) RequireInputRevision(db, session);
                    else RequireChatRevision(session, TextChat(db, session.Id));
                    var operations = new List<string>();
                    using (var command = Command(db, mode == SessionEventReadMode.RequireCompleteInputJournal
                        ? "SELECT operation FROM control_fact WHERE stream=$stream AND chat=$chat AND sequence<=$through ORDER BY sequence"
                        : "SELECT operation FROM chat_write WHERE stream=$stream AND chat=$chat ORDER BY sequence"))
                    {
                        command.Parameters.AddWithValue("$chat", session.Id);
                        command.Parameters.AddWithValue("$through", session.Revision);
                        using (var reader = command.ExecuteReader()) while (reader.Read()) operations.Add(reader.GetString(0));
                    }
                    return operations.Select(operation => FindChatWrite(db, operation) ?? throw TextStoreScope()).ToArray();
                }, CancellationToken.None);
                return receipts.Select(ProjectChatEvent).ToArray();
            }
        }

        ISessionEventReader IEventStore.OpenReader(ConversationMetadata session) { throw TextStoreScope(); }
        string IEventStore.ReadPayload(ConversationMetadata session, SessionEvent item)
        {
            if (item?.SessionId != session.Id) throw TextStoreScope();
            if (item.Type == SessionEventTypes.ConversationInputChanged && item.Payload != null)
                return Access((db, cursor) => {
                    RequireInputRevision(db, session);
                    if (item.Sequence > session.Revision) throw new ChatConcurrencyException("Input lies beyond the supplied chat revision.");
                    return ReadChatBody(RequireChatInputEvent(db, session.Id, item).Body);
                }, CancellationToken.None);
            RequireChatRevision(session, ReadChat(session.Id));
            if (item.Payload != null) throw TextStoreScope();
            return null; // raw model capture belongs to the existing optional RAM buffer
        }
    }

    internal sealed class ConversationModelEvent
    {
        public SessionEventKind Kind { get; private set; }
        public string RunId { get; private set; }
        public string TurnId { get; private set; }
        public string StepId { get; private set; }
        public PayloadRef Body { get; private set; }
        [JsonConstructor] internal ConversationModelEvent(SessionEventKind kind, string runId, string turnId, string stepId, PayloadRef body)
        {
            if (kind != SessionEventKind.ModelRequestPrepared && kind != SessionEventKind.ModelResponseReceived &&
                kind != SessionEventKind.ModelResponseAccepted && kind != SessionEventKind.ModelAttemptRejected && kind != SessionEventKind.ModelFailure &&
                kind != SessionEventKind.ToolPackExtensionAccepted && kind != SessionEventKind.ToolPackExtensionRejected ||
                string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(turnId) || body == null)
                throw new ArgumentException("An exact model manifest/diagnostic event is required.");
            Kind = kind; RunId = runId; TurnId = turnId; StepId = stepId; Body = body;
        }
    }
}
