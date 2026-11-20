using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.Data.Sqlite;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ConversationJournal : IEventStore, IIndexedModelEventReader
    {
        SessionEvent IEventStore.Append(ChatSession session, SessionEventWrite write)
        {
            if (write == null) throw new ArgumentNullException(nameof(write));
            SessionEventDescriptors.EnsureEventPortWritable(write.Descriptor);
            if (write.Descriptor.Kind == SessionEventKind.ConversationInputChanged)
            {
                var change = write.Data as ConversationInputEvent;
                if (change == null || (change.Input == null ? write.Payload != null || write.Correlation != null :
                    write.Payload?.Text == null || write.Payload.Bytes != null || change.Input.Text != null ||
                    write.Correlation?.RunId != change.Input.AcceptedRunId || write.Correlation?.TurnId != change.Input.AcceptedRunId))
                    throw TextStoreScope();
                RequireTextRoot();
                if (change.Input == null) return CommitChatControl(session, change);
                var body = change.Input == null ? null : PayloadRef.FromBlob(_bodies.StoreText(write.Payload.Text, "text/plain"));
                var receipt = CommitChat(session, new ConversationCommit(), queueControl: change, inputBody: body);
                ApplyChatReceipt(session, receipt);
                return ProjectChatEvent(receipt);
            }
            var causal = write.Data as CausalTraceRecord;
            var diagnosticKind = write.Descriptor.Lane == SessionEventLane.DomainDiagnostic;
            var trace = write.Data as LlmTraceRecord;
            var admission = write.Data as ToolPackExtensionEventData;
            var admissionKind = write.Descriptor.Kind == SessionEventKind.ToolPackExtensionAccepted ||
                write.Descriptor.Kind == SessionEventKind.ToolPackExtensionRejected;
            if (write.Payload != null || (diagnosticKind ? causal?.Kind != write.Descriptor.Kind : admissionKind ? admission == null : trace == null) ||
                trace?.PayloadUtf8Bytes != null || trace?.PayloadJson != null)
                throw TextStoreScope();
            if (session?.LastRun == null || trace != null && (trace.SessionId != session.Id || trace.RunId != session.LastRun.RunId) ||
                write.Correlation?.RunId != session.LastRun.RunId || write.Correlation.TurnId != session.LastRun.TurnId ||
                causal != null && (causal.SessionId != session.Id || causal.RunId != write.Correlation.RunId ||
                    causal.TurnId != write.Correlation.TurnId || causal.StepId != write.Correlation.StepId ||
                    causal.DocumentRuntimeId != null && causal.DocumentRuntimeId != session.LastRun.DocumentRuntimeKey))
                throw new InvalidOperationException("Model event belongs to another run.");
            RequireTextRoot();
            using (RuntimeStorageActivity.Acquire(_paths))
            {
                var item = new ConversationModelEvent(write.Descriptor.Kind, write.Correlation.RunId,
                    write.Correlation.TurnId, write.Correlation.StepId, StoreChatJson(write.Data), trace?.StepId);
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
                Data = JObject.FromObject(ProjectQueueControl(receipt.Fact.Control, receipt.Fact.Input?.Header)) };
            return new SessionEvent { SessionId = receipt.Fact.Card.ChatId, Sequence = receipt.Position.Sequence,
                EventId = receipt.Operation, CreatedUtc = receipt.Fact.CreatedUtc, Hash = receipt.Position.Hash,
                NativeCommitOperations = item == null ? new Lazy<JArray>(() => DiagnosticOperations(receipt.Fact)) : null,
                ReadCommittedMessage = item == null ? new Func<string, ChatMessage>(id => ReadText(receipt.Fact.Messages.Single(entry => entry.Header.Id == id))) : null,
                Type = item == null ? SessionEventTypes.ChatCommitted : SessionEventDescriptors.For(item.Kind).Type,
                RunId = item?.RunId ?? receipt.Fact.Run?.RunId, TurnId = item?.TurnId ?? receipt.Fact.Run?.TurnId, StepId = item?.StepId ?? receipt.Fact.Tools?.StepId,
                Data = item == null ? JObject.FromObject(receipt.Fact) : JObject.Parse(ReadChatBody(item.Body)) };
        }

        // A disposable semantic view of one verified frame. Keep raw export and the
        // journal unchanged. Only a selected accepted response needs its CAS text.
        private static JArray DiagnosticOperations(ConversationChatCommit fact)
        {
            var operations = new JArray();
            var tools = fact.Tools;
            for (var i = 0; i < fact.Messages.Count; i++)
            {
                var message = fact.Messages[i].Header;
                var type = SessionOperationTypes.MessageUpdated;
                if (tools?.Kind == AgentRunEventKind.ResponseAccepted && message.AcceptedCallOrigin != null)
                    type = SessionOperationTypes.ToolCallRecorded;
                else if (tools?.Kind == AgentRunEventKind.ResponseAccepted && message.ProtocolMessage && !message.RuntimeProjection)
                    type = SessionOperationTypes.AssistantMessageAppended;
                else if (tools?.Kind == AgentRunEventKind.ModelAttemptRetained)
                    type = SessionOperationTypes.ModelAttemptRetained;
                else if (tools != null && message.Id == tools.ActivityId && !tools.ResultProjection)
                {
                    if (tools.Kind == AgentRunEventKind.ToolStarted) type = SessionOperationTypes.ToolExecutionStarted;
                    else if (tools.Kind == AgentRunEventKind.ToolCompleted) type = SessionOperationTypes.ToolExecutionFinished;
                }
                else if (tools?.Kind == AgentRunEventKind.ToolCompleted && message.ToolCallId == tools.CallId && message.Activity == null)
                    type = SessionOperationTypes.ToolResultRecorded;
                else if (tools == null && message.Role == "user" && !message.ProtocolMessage)
                    type = SessionOperationTypes.UserMessageAppended;
                operations.Add(DiagnosticOperation(type, message, "Messages[" + i + "].Header"));
            }
            if (fact.Checkpoint != null)
            {
                var type = TerminalRun(fact.Checkpoint.Lifecycle) ? SessionOperationTypes.RunEnded :
                    fact.Checkpoint.Revision == 1 ? SessionOperationTypes.RunStarted : SessionOperationTypes.RunUpdated;
                operations.Add(DiagnosticOperation(type, fact.Run, "Run"));
            }
            if (fact.Snapshots != null)
                for (var i = 0; i < fact.Snapshots.Count; i++)
                    operations.Add(DiagnosticOperation(SessionOperationTypes.ArtifactRevisionCreated, fact.Snapshots[i], "TaskLists[" + i + "]"));
            if (operations.Count == 0)
                operations.Add(DiagnosticOperation(SessionOperationTypes.SessionMetadataSet, fact.Card, "Card"));
            return operations;
        }

        private static JObject DiagnosticOperation(string type, object value, string path)
        {
            return new JObject { ["Type"] = type, ["SourcePath"] = path,
                ["Data"] = new JObject { ["Value"] = JObject.FromObject(value) } };
        }

        public SessionEvent ReadModelEvent(ConversationMetadata chat, string eventId, CancellationToken token)
        {
            RequireTextRoot();
            using (RuntimeStorageActivity.Acquire(_paths, token))
            {
                var receipt = Access((db, cursor) => {
                    RequireChatRevision(chat, TextChat(db, chat.Id));
                    return FindChatWrite(db, eventId, chat.Id);
                }, token);
                token.ThrowIfCancellationRequested();
                return receipt?.Fact.Card.ChatId == chat.Id && receipt.Position.Sequence <= chat.Revision &&
                    receipt.Fact.ModelEvent != null ? ProjectChatEvent(receipt) : null;
            }
        }

        public IReadOnlyList<KeyValuePair<int, SessionEvent>> ReadRequestPage(ConversationMetadata chat, string run,
            long? before, CancellationToken token, string step = null)
        {
            RequireTextRoot();
            using (RuntimeStorageActivity.Acquire(_paths, token))
            {
                var receipts = Access((db, cursor) => {
                    RequireChatRevision(chat, TextChat(db, chat.Id));
                    var keys = new List<KeyValuePair<int, string>>();
                    using (var command = Command(db, "SELECT request_ordinal,operation FROM chat_event WHERE stream=$stream AND chat=$chat AND kind=$kind AND sequence<=$through" +
                        (run == null ? "" : " AND run=$run") + (step == null ? "" : " AND step=$step") + " ORDER BY sequence DESC LIMIT 51"))
                    {
                        command.Parameters.AddWithValue("$chat", chat.Id);
                        command.Parameters.AddWithValue("$kind", (int)SessionEventKind.ModelRequestPrepared);
                        command.Parameters.AddWithValue("$through", before.HasValue && before.Value <= chat.Revision
                            ? (before.Value > 0 ? before.Value - 1 : 0) : chat.Revision);
                        if (run != null) command.Parameters.AddWithValue("$run", run);
                        if (step != null) command.Parameters.AddWithValue("$step", step);
                        using (var reader = command.ExecuteReader()) while (reader.Read())
                            keys.Add(new KeyValuePair<int, string>(reader.GetInt32(0), reader.GetString(1)));
                    }
                    return keys.Select(key => {
                        token.ThrowIfCancellationRequested();
                        var fact = FindChatWrite(db, key.Value, chat.Id);
                        if (key.Key < 1 || fact?.Fact.Card.ChatId != chat.Id || fact.Fact.ModelEvent?.Kind != SessionEventKind.ModelRequestPrepared ||
                            fact.Position.Sequence > chat.Revision || run != null && fact.Fact.ModelEvent.RunId != run)
                            throw new InvalidDataException("Invalid diagnostic request locator.");
                        return new KeyValuePair<int, ConversationChatReceipt>(key.Key, fact);
                    }).ToArray();
                }, token);
                return receipts.Select(entry => {
                    token.ThrowIfCancellationRequested();
                    var item = ProjectChatEvent(entry.Value);
                    if (step != null && (string)item.Data["StepId"] != step) throw new InvalidDataException("Invalid diagnostic step locator.");
                    return new KeyValuePair<int, SessionEvent>(entry.Key, item);
                }).ToArray();
            }
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
                var calls = new HashSet<Tuple<string, string, string>>();
                using (var command = Command(db, "SELECT call FROM call_fact WHERE stream=$stream AND chat=$chat AND run=$run"))
                {
                    command.Parameters.AddWithValue("$chat", session.Id); command.Parameters.AddWithValue("$run", runId);
                    using (var reader = command.ExecuteReader()) while (reader.Read()) calls.Add(Tuple.Create(session.Id, runId, reader.GetString(0)));
                }
                foreach (var alias in references)
                {
                    string normalized;
                    if (!ModelReference.TryNormalize(alias, out normalized) || normalized[0] != 'E') continue;
                    var result = FindFinishResult(db, current, normalized);
                    if (result != null) calls.Add(Tuple.Create(result.ChatId, result.Result.RunId, result.Result.ToolCallId));
                }
                var facts = new List<ConversationFinishEvidenceAdmission.Fact>();
                foreach (var call in calls) AddFinishFacts(db, call.Item1, call.Item2, call.Item3, facts);
                var retained = facts.Select(item => item.Message.Id).Distinct(StringComparer.Ordinal)
                    .Where(id => ChatMessageOperation(db, session.Id, id) != null).ToArray();
                return new FinishEvidenceSnapshot(new SessionEventPosition(session.Id, current.Position.Sequence, current.Position.Hash),
                    facts, retained);
            }, CancellationToken.None);
        }

        private ConversationResultFact FindFinishResult(SqliteConnection db, ConversationChatReceipt chat, string alias)
        {
            var ordinal = new ModelReference(ModelReferenceKind.Evidence, alias).Ordinal;
            var through = chat.Position.Sequence;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                if (!seen.Add(chat.Fact.Card.ChatId)) throw new InvalidDataException("Cyclic fork provenance.");
                using (var command = Command(db, "SELECT operation FROM result_fact WHERE stream=$stream AND chat=$chat AND ordinal=$ordinal AND sequence<=$through"))
                {
                    command.Parameters.AddWithValue("$chat", chat.Fact.Card.ChatId);
                    command.Parameters.AddWithValue("$ordinal", ordinal);
                    command.Parameters.AddWithValue("$through", through);
                    var receipt = Find(db, command.ExecuteScalar() as string);
                    if (receipt != null)
                    {
                        if (receipt.Fact.ChatId != chat.Fact.Card.ChatId || receipt.Position.Sequence > through ||
                            receipt.Fact.Result.EvidenceReference?.Alias != alias)
                            throw new InvalidDataException("Evidence alias locator mismatch.");
                        return receipt.Fact;
                    }
                }
                // Forks retain original execution owners. Follow only the frozen
                // ancestry; child membership below still fences the copied prefix
                // and later removals. No execution facts become child-owned.
                var fork = chat.Fact.Fork;
                if (fork == null || ordinal >= fork.NextEvidence) return null;
                chat = FindChatWrite(db, fork.ParentOperation, fork.Parent.Id);
                if (chat == null || chat.Position.Sequence > fork.Parent.Revision)
                    throw new InvalidDataException("Fork lost its retained source provenance.");
                through = fork.Parent.Revision;
            }
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
        { return ReadEvents(session, mode, CancellationToken.None); }

        public IReadOnlyList<SessionEvent> ReadEvents(ConversationMetadata session, SessionEventReadMode mode, CancellationToken token)
        { return ReadEventRange(session, mode, null, token); }

        internal TrajectoryViewPage ReadRunTrajectory(ConversationMetadata session, TrajectoryViewQueryRequest request, CancellationToken token)
        {
            if (request.View != TrajectoryViews.RunCausal || string.IsNullOrWhiteSpace(request.RunId))
                throw new ArgumentException("An exact run is required for a scoped run journal.");
            var cursor = request.Cursor;
            var prefix = "run:" + TextPatternEngine.Sha256(_scope + "/" + session.Id + "/" + request.RunId) + ":";
            if (!string.IsNullOrEmpty(cursor) && !cursor.StartsWith(prefix, StringComparison.Ordinal))
                throw new ArgumentException("The trajectory cursor belongs to another run.");
            try
            {
                request.Cursor = string.IsNullOrEmpty(cursor) ? null : cursor.Substring(prefix.Length);
                var events = ReadEventRange(session, SessionEventReadMode.Validated, request.RunId, token);
                var result = new RNAssistant.Core.Services.EventStreamTrajectoryQuery().QueryView(events, request);
                result.Cursor = cursor;
                if (result.NextCursor != null) result.NextCursor = prefix + result.NextCursor;
                return result;
            }
            finally { request.Cursor = cursor; }
        }

        private void SaveDiagnosticRunEvent(SqliteConnection db, SqliteTransaction tx, JournalRecord record, JournalCursor position, string chat, string run)
        {
            if (string.IsNullOrWhiteSpace(chat) || string.IsNullOrWhiteSpace(run)) return;
            using (var command = Command(db, "INSERT OR IGNORE INTO chat_diagnostic_run VALUES($stream,$chat,$run,$seq,$operation)", tx))
            {
                command.Parameters.AddWithValue("$chat", chat); command.Parameters.AddWithValue("$run", run);
                command.Parameters.AddWithValue("$seq", position.Sequence); command.Parameters.AddWithValue("$operation", record.Operation);
                command.ExecuteNonQuery();
            }
        }

        private IReadOnlyList<SessionEvent> ReadEventRange(ConversationMetadata session, SessionEventReadMode mode, string run, CancellationToken token)
        {
            if (mode != SessionEventReadMode.Validated && mode != SessionEventReadMode.RequireComplete && mode != SessionEventReadMode.RequireCompleteInputJournal) throw TextStoreScope();
            RequireTextRoot();
            using (RuntimeStorageActivity.Acquire(_paths, token))
            {
                var receipts = Access((db, cursor) => {
                    if (mode == SessionEventReadMode.RequireCompleteInputJournal) RequireInputRevision(db, session, allowTransient: true);
                    else RequireChatRevision(session, TextChat(db, session.Id));
                    var operations = new List<string>();
                    using (var command = Command(db, run != null
                        ? "SELECT operation FROM chat_diagnostic_run WHERE stream=$stream AND chat=$chat AND run=$run AND sequence<=$through ORDER BY sequence"
                        : mode == SessionEventReadMode.RequireCompleteInputJournal
                        ? "SELECT operation FROM control_fact WHERE stream=$stream AND chat=$chat AND sequence<=$through ORDER BY sequence"
                        : ChatEventQuery + " ORDER BY sequence"))
                    {
                        command.Parameters.AddWithValue("$chat", session.Id);
                        command.Parameters.AddWithValue("$through", session.Revision);
                        command.Parameters.AddWithValue("$after", 0);
                        if (run != null) command.Parameters.AddWithValue("$run", run);
                        using (var reader = command.ExecuteReader()) while (reader.Read())
                        { token.ThrowIfCancellationRequested(); operations.Add(reader.GetString(0)); }
                    }
                    return operations.Select(operation => { token.ThrowIfCancellationRequested(); return ReadEventReceipt(db, session.Id, operation); }).ToArray();
                }, token);
                return receipts.Select(item => { token.ThrowIfCancellationRequested(); return ProjectReceipt(item, session.Id); }).ToArray();
            }
        }

        private Tuple<ConversationChatReceipt, AgentMessageReceipt, AgentSpawnJournalReceipt, QueueControlReceipt> ReadEventReceipt(
            SqliteConnection db, string chat, string operation)
        {
            var write = FindChatWrite(db, operation, chat);
            if (write?.Fact.Card.ChatId != chat) write = null;
            var spawn = write == null ? FindSpawn(db, operation) : null;
            if (spawn != null && spawn.Fact.Sender != chat) throw new InvalidDataException("Foreign spawn event.");
            var peer = write == null && spawn == null ? FindDelivery(db, "operation", operation) : null;
            return Tuple.Create(write, peer, spawn, write == null && peer == null && spawn == null ? FindControl(db, operation) : null);
        }

        private SessionEvent ProjectReceipt(Tuple<ConversationChatReceipt, AgentMessageReceipt, AgentSpawnJournalReceipt, QueueControlReceipt> item, string chat)
        {
            return item.Item1 != null ? ProjectChatEvent(item.Item1) : item.Item3 != null ? ProjectSpawnEvent(item.Item3) :
                item.Item4 != null ? ProjectControlEvent(item.Item4, chat) : ProjectPeerEvent(item.Item2, chat);
        }

        internal TrajectoryQueryPage ReadRawPage(ConversationMetadata session, TrajectoryQueryRequest request, CancellationToken token)
        {
            var before = RNAssistant.Core.Services.EventStreamTrajectoryQuery.ParseCursor(request.Cursor);
            var limit = request.PageSize <= 0 ? 100 : Math.Min(200, request.PageSize);
            RequireTextRoot();
            using (RuntimeStorageActivity.Acquire(_paths, token))
            {
                var page = Access((db, cursor) => {
                    RequireChatRevision(session, TextChat(db, session.Id));
                    int total;
                    using (var command = Command(db, "SELECT COUNT(*) FROM (" + ChatEventQuery + ")"))
                    {
                        command.Parameters.AddWithValue("$chat", session.Id); command.Parameters.AddWithValue("$after", 0);
                        command.Parameters.AddWithValue("$through", session.Revision);
                        total = Convert.ToInt32(command.ExecuteScalar());
                    }
                    var operations = new List<KeyValuePair<string, bool>>();
                    using (var command = Command(db, "SELECT e.operation,EXISTS(SELECT 1 FROM chat_diagnostic_current c WHERE c.stream=$stream AND c.chat=$chat AND c.sequence=e.sequence) FROM (" +
                        ChatEventQuery + " ORDER BY sequence DESC LIMIT $limit) e ORDER BY e.sequence DESC"))
                    {
                        command.Parameters.AddWithValue("$chat", session.Id); command.Parameters.AddWithValue("$after", 0);
                        command.Parameters.AddWithValue("$through", Math.Min(session.Revision, before.HasValue ? before.Value - 1 : session.Revision));
                        command.Parameters.AddWithValue("$limit", limit + 1);
                        using (var reader = command.ExecuteReader()) while (reader.Read())
                            operations.Add(new KeyValuePair<string, bool>(reader.GetString(0), reader.GetBoolean(1)));
                    }
                    return Tuple.Create(total, operations.Count > limit, operations.Take(limit).Select(row => {
                        token.ThrowIfCancellationRequested();
                        return Tuple.Create(ReadEventReceipt(db, session.Id, row.Key), row.Value);
                    }).ToArray());
                }, token);
                var records = page.Item3.Select(row => {
                    token.ThrowIfCancellationRequested();
                    return RNAssistant.Core.Services.EventStreamTrajectoryQuery.Project(ProjectReceipt(row.Item1, session.Id), null, row.Item2);
                }).ToList();
                return new TrajectoryQueryPage { Records = records, TotalEvents = page.Item1, TotalMatches = page.Item1,
                    HasMore = page.Item2, Cursor = request.Cursor, NextCursor = page.Item2 ? "seq:" +
                        records[records.Count - 1].Event.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) : null };
            }
        }

        ISessionEventReader IEventStore.OpenReader(ConversationMetadata session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            RequireTextRoot();
            return new JournalEventReader(this, session);
        }
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
            return null; // Optional model captures are read through the diagnostic store.
        }
    }

    internal sealed class ConversationModelEvent
    {
        public SessionEventKind Kind { get; private set; }
        public string RunId { get; private set; }
        public string TurnId { get; private set; }
        public string StepId { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string LogicalStepId { get; private set; }
        public PayloadRef Body { get; private set; }
        [JsonConstructor] internal ConversationModelEvent(SessionEventKind kind, string runId, string turnId, string stepId, PayloadRef body, string logicalStepId = null)
        {
            if (kind != SessionEventKind.ModelRequestPrepared && kind != SessionEventKind.ModelResponseReceived &&
                kind != SessionEventKind.ModelResponseAccepted && kind != SessionEventKind.ModelAttemptRejected && kind != SessionEventKind.ModelAttemptRetained && kind != SessionEventKind.ModelFailure &&
                kind != SessionEventKind.ToolPackExtensionAccepted && kind != SessionEventKind.ToolPackExtensionRejected &&
                SessionEventDescriptors.For(kind).Lane != SessionEventLane.DomainDiagnostic ||
                string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(turnId) || body == null)
                throw new ArgumentException("An exact model manifest/diagnostic event is required.");
            Kind = kind; RunId = runId; TurnId = turnId; StepId = stepId; Body = body; LogicalStepId = logicalStepId;
        }
    }
}
