using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    // First new-generation slice: accepted calls and terminal facts, bounded locator queries,
    // one shared index. Only isolated callers until the P2/P6/P7 integration gates.
    internal sealed partial class ConversationJournal
    {
        private readonly AppDataPaths _paths;
        private readonly string _scope;
        private readonly ChatBlobStore _bodies;
        private readonly StorageProtector _protector;
        internal readonly StorageJournal Journal;
        internal Action<string> Fault;

        internal ConversationJournal(AppDataPaths paths, string scope, StorageProtector protector = null)
        {
            if (paths == null || string.IsNullOrWhiteSpace(scope) ||
                !(scope.StartsWith("document:", StringComparison.Ordinal) || scope.StartsWith("workspace:", StringComparison.Ordinal)) ||
                scope.EndsWith(":", StringComparison.Ordinal)) throw new ArgumentException("An exact document/workspace scope is required.");
            _paths = paths; _scope = scope;
            _protector = protector ?? StorageProtector.None;
            _bodies = new ChatBlobStore(paths, () => protector ?? StorageProtector.None);
            Journal = new StorageJournal(Path.Combine(paths.Root, "conversation-streams",
                RNAssistant.Core.Tools.TextPatternEngine.Sha256(scope)), scope, protector);
        }

        internal ConversationResultReceipt CommitResult(string chatId, string operationId, string callId, string toolName,
            string stepId, string runId, ToolResultMaterialization result, CancellationToken token = default(CancellationToken),
            ResourceJournalReceipt resourceOutcome = null)
        {
            if (string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(operationId) || string.IsNullOrWhiteSpace(runId))
                throw new ArgumentException("Exact chat, operation and run identities are required.");
            using (RuntimeStorageActivity.Acquire(_paths, token))
            {
                // Publication and hashing precede the metadata/append section.
                var message = ToolResultPersistence.Create(callId, toolName, stepId, result, _bodies, runId);
                foreach (var reference in ResultReferences(message).GroupBy(ReferenceKey).Select(group => group.First()))
                    if (!_bodies.HasVerifiedReference(reference.ToBlobReference())) throw new InvalidDataException("Result body is unavailable before acceptance.");
                ResourceOutcomeLink outcomeLink = null;
                if (resourceOutcome != null)
                {
                    var verified = new ResourceJournal(_paths, _protector).ReadReceipt(resourceOutcome);
                    outcomeLink = ResourceOutcomeLink.From(verified);
                    if (message.AuthorityCommitId != verified.Fact.Publication.CommitId || message.ResourceEffect == null ||
                        !JToken.DeepEquals(JObject.FromObject(message.ResourceEffect), JObject.FromObject(verified.Fact.Publication.Effect)))
                        throw new InvalidOperationException("Terminal effect is not the exact resource outcome.");
                    if (verified.Fact.Publication.Unknown && (message.ExecutionProgress.Outcome != RNAssistant.Core.Tools.ToolExecutionOutcome.Unknown ||
                        message.ExecutionProgress.EvidenceEligibility != RNAssistant.Core.Tools.EvidenceEligibility.None))
                        throw new InvalidOperationException("Unknown resource effect cannot become successful/eligible.");
                }
                var fact = new ConversationResultFact(chatId, message, outcomeLink);
                var fingerprint = Fingerprint(fact);
                return Access((db, cursor) => {
                    var prior = Find(db, operationId);
                    if (prior != null)
                    {
                        if (prior.Fingerprint != fingerprint || prior.Fact.ChatId != chatId)
                            throw new InvalidOperationException("Operation identity was already accepted with different content.");
                        return prior;
                    }
                    var accepted = FindCall(db, chatId, runId, callId);
                    if (accepted == null || accepted.Call.ToolName != toolName || accepted.Call.AcceptedCallOrigin.StepId != stepId)
                        throw new InvalidOperationException("Terminal result has no exact accepted call.");
                    ValidateResourceBinding(accepted.ResourceBinding, fact);
                    if (OperationExists(db, operationId) || TerminalOperation(db, chatId, runId, callId) != null)
                        throw new InvalidOperationException("Call or operation already has a terminal fact.");
                    message.ToolCallReference = accepted.Call.ToolCallReference;
                    var next = Scalar(db, "SELECT next_e FROM chat_sequence WHERE stream=$stream AND chat=$chat", chatId);
                    message.EvidenceReference = message.ExecutionProgress.EvidenceEligibility == RNAssistant.Core.Tools.EvidenceEligibility.Eligible
                        ? ModelReference.Create(ModelReferenceKind.Evidence, Convert.ToInt64(next)) : null;
                    var position = Journal.Append(cursor, operationId, fingerprint, "tool.result", JObject.FromObject(fact));
                    var receipt = new ConversationResultReceipt(fact, operationId, fingerprint, position, true);
                    try
                    {
                        Fault?.Invoke("before_index");
                        Apply(db, fact, operationId, fingerprint, position);
                    }
                    catch (Exception error) when (error is SqliteException || error is IOException)
                    {
                        // The durable fact exists. This is never a not-dispatched outcome.
                        receipt.ProjectionAvailable = false;
                    }
                    return receipt;
                }, token);
            }
        }

        internal ConversationResultReceipt Receipt(string operationId, CancellationToken token = default(CancellationToken))
        { return Access((db, cursor) => Find(db, operationId), token); }

        internal ConversationCallReceipt AcceptedCall(string chat, string run, string call, CancellationToken token = default(CancellationToken))
        { return Access((db, cursor) => FindCall(db, chat, run, call), token); }

        internal ResourceOutcomeLink LinkResourceOutcome(string chat, string run, string call, ResourceJournalReceipt resourceReceipt,
            CancellationToken token = default(CancellationToken))
        {
            using (RuntimeStorageActivity.Acquire(_paths, token))
            {
                // Foreign immutable proof is prepared before this conversation's
                // append lock. Neither index catch-up nor effect dispatch occurs here.
                var proof = new ResourceJournal(_paths, _protector).ReadReceipt(resourceReceipt);
                var fact = new ConversationResourceOutcomeFact(chat, run, call, ResourceOutcomeLink.From(proof));
                var fingerprint = RNAssistant.Core.Tools.TextPatternEngine.Sha256(JsonConvert.SerializeObject(fact));
                var operation = "resource-link:" + fingerprint;
                return Access((db, cursor) => {
                    var existing = FindResourceLink(db, operation);
                    if (existing != null) return existing.Outcome;
                    ValidateLink(db, operation, fact);
                    var position = Journal.Append(cursor, operation, fingerprint, "resource.linked", JObject.FromObject(fact));
                    try { Fault?.Invoke("before_index"); ApplyLink(db, operation, fingerprint, fact, position); }
                    catch (Exception error) when (error is SqliteException || error is IOException) { /* canonical link accepted */ }
                    return fact.Outcome;
                }, token);
            }
        }

        internal ResourceJournalReceipt LinkedResourceOutcome(string chat, string run, string call,
            CancellationToken token = default(CancellationToken))
        {
            var link = Access((db, cursor) => {
                string operation = null; long sequence = -1;
                using (var command = Command(db, "SELECT operation,sequence FROM resource_link WHERE stream=$stream AND chat=$chat AND run=$run AND call=$call ORDER BY sequence DESC LIMIT 1"))
                {
                    AddCallKey(command, chat, run, call);
                    using (var reader = command.ExecuteReader()) if (reader.Read()) { operation = reader.GetString(0); sequence = reader.GetInt64(1); }
                }
                var terminal = TerminalOperation(db, chat, run, call) as string;
                var result = terminal == null ? null : Find(db, terminal);
                if (result?.Fact.ResourceOutcome != null && result.Position.Sequence > sequence) return result.Fact.ResourceOutcome;
                if (operation == null) return null;
                var linked = FindResourceLink(db, operation);
                if (linked.ChatId != chat || linked.RunId != run || linked.CallId != call) throw new InvalidDataException("Foreign resource linkage row.");
                return linked.Outcome;
            }, token);
            return link == null ? null : new ResourceJournal(_paths, _protector).ReadOutcome(link);
        }

        internal JournalCursor CommitCall(string chatId, string operationId, ChatMessage call,
            CancellationToken token = default(CancellationToken), ConversationResourceBinding resourceBinding = null)
        {
            if (string.IsNullOrWhiteSpace(operationId)) throw new ArgumentException("Exact operation identity is required.");
            using (RuntimeStorageActivity.Acquire(_paths, token))
            {
                // Freeze caller-owned metadata; body publication/verification is outside append.
                var fact = new ConversationCallFact(chatId, JsonConvert.DeserializeObject<ChatMessage>(JsonConvert.SerializeObject(call)), resourceBinding);
                if (resourceBinding != null && !resourceBinding.BelongsTo(_scope, chatId, call.RunId, call.ToolCallId))
                    throw new InvalidOperationException("Resource attempt is not allocated to this exact conversation call.");
                foreach (var reference in CallReferences(fact.Call))
                    if (!_bodies.HasVerifiedReference(reference.ToBlobReference())) throw new InvalidDataException("Accepted call body is unavailable.");
                var hydrated = AcceptedCallPayloadService.Hydrate(fact.Call, _bodies);
                if (!ConversationResponseHistoryReader.Read(hydrated).Success) throw new ArgumentException("Accepted call protocol is invalid.");
                var fingerprint = CallFingerprint(fact);
                return Access((db, cursor) => {
                    var prior = FindCall(db, chatId, call.RunId, call.ToolCallId);
                    if (prior != null)
                    {
                        if (prior.Operation != operationId || prior.Fingerprint != fingerprint)
                            throw new InvalidOperationException("Accepted call identity was reused with different content.");
                        return prior.Position;
                    }
                    if (OperationExists(db, operationId))
                        throw new InvalidOperationException("Operation identity was already accepted.");
                    var next = Scalar(db, "SELECT next_t FROM chat_sequence WHERE stream=$stream AND chat=$chat", chatId);
                    fact.Call.ToolCallReference = ModelReference.Create(ModelReferenceKind.ToolCall, next == null ? 1 : Convert.ToInt64(next));
                    var position = Journal.Append(cursor, operationId, fingerprint, "tool.accepted", JObject.FromObject(fact));
                    try { Fault?.Invoke("before_index"); ApplyCall(db, fact, operationId, fingerprint, position); }
                    catch (Exception error) when (error is SqliteException || error is IOException) { /* accepted; next access catches up */ }
                    return position;
                }, token);
            }
        }

        internal IReadOnlyList<ChatMessage> OpenContext(string chatId, long beforeSequence, int resultLimit,
            CancellationToken token = default(CancellationToken))
        {
            ValidateWindow(chatId, beforeSequence, resultLimit);
            return Access((db, cursor) => {
                var messages = new List<ChatMessage>();
                foreach (var receipt in ReadWindow(db, chatId, beforeSequence, resultLimit))
                {
                    var result = receipt.Fact.Result;
                    var accepted = FindCall(db, chatId, result.RunId, result.ToolCallId);
                    if (accepted == null) throw new InvalidDataException("Terminal fact has no accepted call.");
                    messages.Add(accepted.Call); messages.Add(result);
                }
                return (IReadOnlyList<ChatMessage>)messages;
            }, token);
        }

        internal IReadOnlyList<ConversationResultReceipt> ReadWindow(string chatId, long beforeSequence, int limit,
            CancellationToken token = default(CancellationToken))
        {
            ValidateWindow(chatId, beforeSequence, limit);
            return Access((db, cursor) => ReadWindow(db, chatId, beforeSequence, limit), token);
        }

        private static void ValidateWindow(string chatId, long beforeSequence, int limit)
        { if (string.IsNullOrWhiteSpace(chatId) || beforeSequence < 1 || limit < 1 || limit > 128) throw new ArgumentException("Invalid bounded result window."); }

        private IReadOnlyList<ConversationResultReceipt> ReadWindow(SqliteConnection db, string chatId, long beforeSequence, int limit)
        {
                var rows = new List<Tuple<string, string, JournalCursor>>();
                using (var command = Command(db, "SELECT operation,fingerprint,segment,start_offset,end_offset,sequence,hash,first_sequence FROM result_fact WHERE stream=$stream AND chat=$chat AND sequence<$before ORDER BY sequence DESC LIMIT $limit"))
                {
                    command.Parameters.AddWithValue("$chat", chatId); command.Parameters.AddWithValue("$before", beforeSequence);
                    command.Parameters.AddWithValue("$limit", limit);
                    using (var reader = command.ExecuteReader()) while (reader.Read())
                        rows.Add(Tuple.Create(reader.GetString(0), reader.GetString(1), JournalIndex.ReadCursor(reader, 2)));
                }
                return (IReadOnlyList<ConversationResultReceipt>)rows.Select(row => ReadReceipt(row.Item1, row.Item2, row.Item3, chatId)).Reverse().ToArray();
        }

        // No full session or source body is loaded. The caller explicitly selects
        // which retained bodies will be included in its next actual request.
        internal ChatMessage Hydrate(ConversationResultReceipt receipt)
        { return ToolResultPersistence.Hydrate(receipt.Fact.Result, _bodies); }

        internal void Rebuild(CancellationToken token = default(CancellationToken))
        {
            RepairIndex(true, token);
        }

        private void RepairIndex(bool force, CancellationToken token)
        { JournalIndex.Repair(_paths, _protector, force, token); }

        internal void ReplayInto(string indexPath, CancellationToken token, bool recover = true)
        {
            var verified = new HashSet<string>(StringComparer.Ordinal);
            ResourceJournalReceipt outcome = null;
            while (true)
            {
                PayloadRef required = null; ResourceOutcomeLink requiredOutcome = null;
                using (Journal.AcquireAppendLock(token))
                using (var db = OpenIndex(indexPath))
                {
                    try { CatchUp(db, verified, outcome, token, recover); return; }
                    catch (TailReferenceNeeded error) { required = error.Reference; }
                    catch (OutcomeNeeded error) { requiredOutcome = error.Link; }
                }
                if (requiredOutcome != null) outcome = new ResourceJournal(_paths, _protector).ReadOutcome(requiredOutcome);
                else VerifyReference(required, verified);
            }
        }

        private T Access<T>(Func<SqliteConnection, JournalCursor, T> action, CancellationToken token)
        {
            using (RuntimeStorageActivity.Acquire(_paths, token))
            {
                var verified = new HashSet<string>(StringComparer.Ordinal);
                ResourceJournalReceipt outcome = null;
                var repairAttempted = false;
                while (true)
                {
                    PayloadRef required = null; ResourceOutcomeLink requiredOutcome = null;
                    try
                    {
                        using (JournalIndexAdmission.Enter(_paths, token))
                        using (Journal.AcquireAppendLock(token))
                        using (var db = OpenIndex())
                        {
                            try { return action(db, CatchUp(db, verified, outcome, token)); }
                            catch (TailReferenceNeeded error) { required = error.Reference; }
                            catch (OutcomeNeeded error) { requiredOutcome = error.Link; }
                        }
                    }
                    catch (SqliteException error) when (!repairAttempted && JournalIndex.IsDamaged(error))
                    { repairAttempted = true; RepairIndex(false, token); continue; }
                    catch (JournalIndex.IndexVersionMismatch) when (!repairAttempted)
                    { repairAttempted = true; RepairIndex(false, token); continue; }
                    // Recovery verifies the one unresolved tail body outside all
                    // append/SQL locks, while the existing GC activity remains held.
                    if (requiredOutcome != null) outcome = new ResourceJournal(_paths, _protector).ReadOutcome(requiredOutcome);
                    else VerifyReference(required, verified);
                }
            }
        }

        private void VerifyReference(PayloadRef required, HashSet<string> verified)
        {
            if (!_bodies.HasVerifiedReference(required.ToBlobReference()))
                throw new InvalidDataException("An unaccepted journal tail references an unavailable CAS body.");
            verified.Add(ReferenceKey(required));
        }


        private JournalCursor CatchUp(SqliteConnection db, HashSet<string> verified, ResourceJournalReceipt outcome, CancellationToken token, bool recover = true)
        {
            var cursor = JournalIndex.LoadCursor(db, _scope);
            var applied = cursor;
            var current = Journal.CatchUp(cursor, (record, position) => {
                if (record.Kind == "queue.control") ApplyControl(db, record, position);
                else if (record.Kind == "run.checkpoint") ApplyRun(db, record, position);
                else if (IsDeliveryFact(record.Kind)) ApplyDeliveryRecord(db, record, position);
                else if (record.Kind == "tool.accepted") ApplyCall(db, DecodeCall(record), record.Operation, record.Fingerprint, position);
                else if (record.Kind == "resource.linked")
                {
                    var fact = DecodeLink(record); RequireOutcome(fact.Outcome, outcome);
                    ApplyLink(db, record.Operation, record.Fingerprint, fact, position);
                }
                else
                {
                    var fact = Decode(record); ValidateOutcome(fact, outcome);
                    Apply(db, fact, record.Operation, record.Fingerprint, position);
                }
                applied = position;
            }, record => {
                IEnumerable<PayloadRef> references;
                if (record.Kind == "queue.control")
                { ValidateControl(db, record.Operation, DecodeControl(record)); references = new PayloadRef[0]; }
                else if (record.Kind == "run.checkpoint")
                {
                    var fact = DecodeRun(record); ValidateRun(db, record.Operation, fact);
                    references = fact.References.Concat(new[] { fact.Body });
                }
                else if (IsDeliveryFact(record.Kind)) references = ValidateDeliveryRecord(db, record);
                else if (record.Kind == "tool.accepted")
                {
                    var fact = DecodeCall(record); ValidateCall(db, fact, record.Operation); references = CallReferences(fact.Call);
                }
                else if (record.Kind == "resource.linked")
                {
                    var fact = DecodeLink(record); RequireOutcome(fact.Outcome, outcome); ValidateLink(db, record.Operation, fact);
                    references = new PayloadRef[0];
                }
                else
                {
                    var fact = Decode(record); ValidateOutcome(fact, outcome); ValidateResult(db, fact, record.Operation); references = ResultReferences(fact.Result);
                }
                foreach (var reference in references)
                    if (!verified.Contains(ReferenceKey(reference))) throw new TailReferenceNeeded(reference);
            }, token, recover);
            if (applied == null || applied.Segment != current.Segment || applied.Hash != current.Hash || applied.End != current.End)
                using (var transaction = db.BeginTransaction()) { SaveCursor(db, current, transaction); transaction.Commit(); }
            return current;
        }

        private static IEnumerable<PayloadRef> CallReferences(ChatMessage call)
        { return new[] { call.ArgumentPayload, call.AcceptedCallPayload }.Where(reference => reference != null); }
        private static IEnumerable<PayloadRef> ResultReferences(ChatMessage result)
        { return result.ResourceEvidence.Select(evidence => evidence.Payload).Concat(new[] { result.ResultPayload }).Where(reference => reference != null); }

        internal IEnumerable<PayloadRef> CanonicalReferences(JournalRecord record)
        {
            if (record.Kind == "tool.accepted") return CallReferences(DecodeCall(record).Call);
            if (record.Kind == "run.checkpoint") { var fact = DecodeRun(record); return fact.References.Concat(new[] { fact.Body }); }
            if (record.Kind == "agent.accepted") return new[] { DecodeDelivery(record).Body };
            if (record.Kind == "agent.spawned") { var fact = DecodeSpawn(record); return fact.Input == null ? new PayloadRef[0] : new[] { fact.Input.Body }; }
            if (record.Kind == "chat.created") DecodeCard(record);
            else if (record.Kind == "input.changed") DecodeInput(record);
            else if (record.Kind == "queue.control") DecodeControl(record);
            else if (record.Kind == "resource.linked") DecodeLink(record);
            else return ResultReferences(Decode(record).Result);
            return new PayloadRef[0];
        }

        private static ConversationCallFact DecodeCall(JournalRecord record)
        {
            if (record.Kind != "tool.accepted") throw new InvalidDataException("Unsupported accepted call fact.");
            var fact = record.Data.ToObject<ConversationCallFact>(JsonSerializer.Create(new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error }));
            if (fact?.Call.ToolCallReference?.Kind != ModelReferenceKind.ToolCall || CallFingerprint(fact) != record.Fingerprint)
                throw new InvalidDataException("Accepted call fingerprint/reference mismatch.");
            return fact;
        }

        private static string CallFingerprint(ConversationCallFact fact)
        {
            var value = JObject.FromObject(fact); var call = (JObject)value["Call"];
            call.Remove("Id"); call.Remove("CreatedUtc"); call.Remove("ToolCallReference");
            return RNAssistant.Core.Tools.TextPatternEngine.Sha256(value.ToString(Formatting.None));
        }

        private bool OperationExists(SqliteConnection db, string operation)
        {
            using (var command = Command(db, "SELECT 1 FROM conversation_operation WHERE stream=$stream AND operation=$operation"))
            { command.Parameters.AddWithValue("$operation", operation); return command.ExecuteScalar() != null; }
        }

        private object TerminalOperation(SqliteConnection db, string chat, string run, string call)
        {
            using (var command = Command(db, "SELECT terminal FROM call_fact WHERE stream=$stream AND chat=$chat AND run=$run AND call=$call"))
            { AddCallKey(command, chat, run, call); var value = command.ExecuteScalar(); return value is DBNull ? null : value; }
        }

        private ConversationCallReceipt FindCall(SqliteConnection db, string chat, string run, string call)
        {
            using (var command = Command(db, "SELECT operation,fingerprint,segment,start_offset,end_offset,sequence,hash,first_sequence FROM call_fact WHERE stream=$stream AND chat=$chat AND run=$run AND call=$call"))
            {
                AddCallKey(command, chat, run, call);
                string operation, fingerprint; JournalCursor position;
                using (var reader = command.ExecuteReader())
                { if (!reader.Read()) return null; operation = reader.GetString(0); fingerprint = reader.GetString(1); position = JournalIndex.ReadCursor(reader, 2); }
                var record = Journal.Read(position); var fact = DecodeCall(record);
                if (record.Operation != operation || record.Fingerprint != fingerprint || fact.ChatId != chat || fact.Call.RunId != run || fact.Call.ToolCallId != call)
                    throw new InvalidDataException("Derived accepted-call locator mismatch.");
                return new ConversationCallReceipt(fact.Call, operation, fingerprint, position, fact.ResourceBinding);
            }
        }

        private void ValidateCall(SqliteConnection db, ConversationCallFact fact, string operation)
        {
            if (OperationExists(db, operation) || FindCall(db, fact.ChatId, fact.Call.RunId, fact.Call.ToolCallId) != null)
                throw new InvalidDataException("Operation/call identity was already accepted.");
            if (fact.ResourceBinding != null && !fact.ResourceBinding.BelongsTo(_scope, fact.ChatId, fact.Call.RunId, fact.Call.ToolCallId))
                throw new InvalidDataException("Foreign resource attempt binding.");
            var next = Scalar(db, "SELECT next_t FROM chat_sequence WHERE stream=$stream AND chat=$chat", fact.ChatId);
            if (fact.Call.ToolCallReference?.Kind != ModelReferenceKind.ToolCall ||
                fact.Call.ToolCallReference.Ordinal != (next == null ? 1 : Convert.ToInt64(next))) throw new InvalidDataException("Accepted call ordinal mismatch.");
        }

        private void ApplyCall(SqliteConnection db, ConversationCallFact fact, string operation, string fingerprint, JournalCursor position)
        {
            ValidateCall(db, fact, operation);
            using (var tx = db.BeginTransaction())
            using (var command = Command(db, "INSERT INTO call_fact VALUES($stream,$chat,$run,$call,$operation,$fingerprint,$segment,$start,$end,$sequence,$hash,$first,NULL)", tx))
            {
                AddCallKey(command, fact.ChatId, fact.Call.RunId, fact.Call.ToolCallId); JournalIndex.AddPosition(command, position);
                command.Parameters.AddWithValue("$operation", operation); command.Parameters.AddWithValue("$fingerprint", fingerprint);
                RegisterOperation(db, tx, operation, "tool.accepted");
                command.ExecuteNonQuery();
                using (var counter = Command(db, "INSERT INTO chat_sequence(stream,chat,next_e,next_t) VALUES($stream,$chat,1,$next) ON CONFLICT(stream,chat) DO UPDATE SET next_t=$next", tx))
                {
                    counter.Parameters.AddWithValue("$chat", fact.ChatId); counter.Parameters.AddWithValue("$next", checked(fact.Call.ToolCallReference.Ordinal + 1));
                    counter.ExecuteNonQuery();
                }
                SaveCursor(db, position, tx); tx.Commit();
            }
        }

        private static void AddCallKey(SqliteCommand command, string chat, string run, string call)
        { command.Parameters.AddWithValue("$chat", chat); command.Parameters.AddWithValue("$run", run); command.Parameters.AddWithValue("$call", call); }

        private ConversationResultFact Decode(JournalRecord record)
        {
            if (record.Kind != "tool.result") throw new InvalidDataException("Unsupported conversation fact.");
            var fact = record.Data.ToObject<ConversationResultFact>(JsonSerializer.Create(new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error }));
            if (fact == null || fact.Result.ToolCallReference?.Kind != ModelReferenceKind.ToolCall ||
                (fact.Result.ExecutionProgress.EvidenceEligibility == RNAssistant.Core.Tools.EvidenceEligibility.Eligible
                    ? fact.Result.EvidenceReference?.Kind != ModelReferenceKind.Evidence : fact.Result.EvidenceReference != null) || Fingerprint(fact) != record.Fingerprint)
                throw new InvalidDataException("Conversation result fingerprint/alias mismatch.");
            return fact;
        }

        private static string Fingerprint(ConversationResultFact fact)
        {
            var value = JObject.FromObject(fact);
            var result = (JObject)value["Result"];
            result.Remove("Id"); result.Remove("CreatedUtc"); result.Remove("EvidenceReference"); result.Remove("ToolCallReference");
            return RNAssistant.Core.Tools.TextPatternEngine.Sha256(value.ToString(Formatting.None));
        }

        private ConversationResultReceipt Find(SqliteConnection db, string operation)
        {
            using (var command = Command(db, "SELECT fingerprint,segment,start_offset,end_offset,sequence,hash,first_sequence FROM result_fact WHERE stream=$stream AND operation=$operation"))
            {
                command.Parameters.AddWithValue("$operation", operation);
                string fingerprint; JournalCursor position;
                using (var reader = command.ExecuteReader())
                { if (!reader.Read()) return null; fingerprint = reader.GetString(0); position = JournalIndex.ReadCursor(reader, 1); }
                return ReadReceipt(operation, fingerprint, position, null);
            }
        }

        private ConversationResultReceipt ReadReceipt(string operation, string fingerprint, JournalCursor position, string chat)
        {
            var record = Journal.Read(position); var fact = Decode(record);
            if (record.Operation != operation || record.Fingerprint != fingerprint || chat != null && fact.ChatId != chat)
                throw new InvalidDataException("Derived result locator does not identify the requested fact.");
            return new ConversationResultReceipt(fact, operation, fingerprint, position, true);
        }

        private long ValidateResult(SqliteConnection db, ConversationResultFact fact, string operation)
        {
            var accepted = FindCall(db, fact.ChatId, fact.Result.RunId, fact.Result.ToolCallId);
            if (accepted == null || accepted.Call.ToolName != fact.Result.ToolName ||
                accepted.Call.AcceptedCallOrigin.StepId != fact.Result.ResultStepId ||
                accepted.Call.ToolCallReference.Alias != fact.Result.ToolCallReference?.Alias || OperationExists(db, operation) ||
                TerminalOperation(db, fact.ChatId, fact.Result.RunId, fact.Result.ToolCallId) != null)
                throw new InvalidDataException("Result does not match its exact accepted call.");
            ValidateResourceBinding(accepted.ResourceBinding, fact);
            var next = Convert.ToInt64(Scalar(db, "SELECT next_e FROM chat_sequence WHERE stream=$stream AND chat=$chat", fact.ChatId));
            if (fact.Result.EvidenceReference != null && fact.Result.EvidenceReference.Ordinal != next)
                throw new InvalidDataException("Terminal evidence ordinal mismatch.");
            return next;
        }

        private void Apply(SqliteConnection db, ConversationResultFact fact, string operation, string fingerprint, JournalCursor position)
        {
            var next = ValidateResult(db, fact, operation);
            using (var transaction = db.BeginTransaction())
            using (var command = Command(db, "INSERT INTO result_fact VALUES ($stream,$chat,$operation,$fingerprint,$sequence,$ordinal,$segment,$start,$end,$hash,$first); UPDATE chat_sequence SET next_e=$next WHERE stream=$stream AND chat=$chat", transaction))
            {
                JournalIndex.AddPosition(command, position);
                command.Parameters.AddWithValue("$chat", fact.ChatId); command.Parameters.AddWithValue("$operation", operation);
                command.Parameters.AddWithValue("$fingerprint", fingerprint);
                command.Parameters.AddWithValue("$ordinal", (object)fact.Result.EvidenceReference?.Ordinal ?? DBNull.Value);
                command.Parameters.AddWithValue("$next", fact.Result.EvidenceReference == null ? next : checked(next + 1));
                RegisterOperation(db, transaction, operation, "tool.result");
                command.ExecuteNonQuery();
                using (var terminal = Command(db, "UPDATE call_fact SET terminal=$operation WHERE stream=$stream AND chat=$chat AND run=$run AND call=$call AND terminal IS NULL", transaction))
                {
                    AddCallKey(terminal, fact.ChatId, fact.Result.RunId, fact.Result.ToolCallId);
                    terminal.Parameters.AddWithValue("$operation", operation);
                    if (terminal.ExecuteNonQuery() != 1) throw new InvalidDataException("Missing or already terminal accepted call.");
                }
                SaveCursor(db, position, transaction); transaction.Commit();
            }
        }

        private void SaveCursor(SqliteConnection db, JournalCursor cursor, SqliteTransaction transaction)
        { JournalIndex.SaveCursor(db, _scope, cursor, transaction); }

        private void RegisterOperation(SqliteConnection db, SqliteTransaction tx, string operation, string kind)
        {
            using (var command = Command(db, "INSERT INTO conversation_operation VALUES($stream,$operation,$kind)", tx))
            { command.Parameters.AddWithValue("$operation", operation); command.Parameters.AddWithValue("$kind", kind); command.ExecuteNonQuery(); }
        }

        private static void ValidateResourceBinding(ConversationResourceBinding binding, ConversationResultFact fact)
        {
            if (fact.ResourceOutcome != null && (binding == null || !binding.Matches(fact.ResourceOutcome)))
                throw new InvalidDataException("Result refers to another resource attempt.");
            if (binding != null && fact.ResourceOutcome == null &&
                fact.Result.ExecutionProgress.Outcome != RNAssistant.Core.Tools.ToolExecutionOutcome.Unknown &&
                fact.Result.ExecutionProgress.Outcome != RNAssistant.Core.Tools.ToolExecutionOutcome.NotDispatched)
                throw new InvalidDataException("An effectful terminal requires its exact resource receipt.");
            if (binding != null && fact.ResourceOutcome == null && fact.Result.ExecutionProgress.Outcome == RNAssistant.Core.Tools.ToolExecutionOutcome.Unknown &&
                fact.Result.ExecutionProgress.EvidenceEligibility != RNAssistant.Core.Tools.EvidenceEligibility.None)
                throw new InvalidDataException("Unresolved resource effect has no eligible success evidence.");
        }

        private static void ValidateOutcome(ConversationResultFact fact, ResourceJournalReceipt outcome)
        {
            var link = fact.ResourceOutcome;
            if (link == null) return;
            RequireOutcome(link, outcome);
            if (fact.Result.AuthorityCommitId != outcome.Fact.Publication.CommitId || fact.Result.ResourceEffect == null ||
                !JToken.DeepEquals(JObject.FromObject(fact.Result.ResourceEffect), JObject.FromObject(outcome.Fact.Publication.Effect)))
                throw new InvalidDataException("Conversation effect disagrees with its exact resource receipt.");
            if (outcome.Fact.Publication.Unknown && (fact.Result.ExecutionProgress.Outcome != RNAssistant.Core.Tools.ToolExecutionOutcome.Unknown ||
                fact.Result.EvidenceReference != null)) throw new InvalidDataException("Unknown resource outcome has no success evidence.");
        }

        private static void RequireOutcome(ResourceOutcomeLink link, ResourceJournalReceipt outcome)
        {
            if (outcome == null || outcome.Position.Hash != link.Position.Hash || outcome.Operation != link.Operation ||
                !outcome.Fact.Scope.Equals(link.Scope) || outcome.Fact.Publication?.AttemptId != link.AttemptId)
                throw new OutcomeNeeded(link);
        }

        private static ConversationResourceOutcomeFact DecodeLink(JournalRecord record)
        {
            var fact = record.Data.ToObject<ConversationResourceOutcomeFact>(JsonSerializer.Create(new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error }));
            if (record.Kind != "resource.linked" || fact == null ||
                RNAssistant.Core.Tools.TextPatternEngine.Sha256(JsonConvert.SerializeObject(fact)) != record.Fingerprint)
                throw new InvalidDataException("Resource linkage fingerprint mismatch.");
            return fact;
        }

        private ConversationResourceOutcomeFact FindResourceLink(SqliteConnection db, string operation)
        {
            using (var command = Command(db, "SELECT fingerprint,segment,start_offset,end_offset,sequence,hash,first_sequence FROM resource_link WHERE stream=$stream AND operation=$operation"))
            {
                command.Parameters.AddWithValue("$operation", operation);
                string fingerprint; JournalCursor position;
                using (var reader = command.ExecuteReader())
                { if (!reader.Read()) return null; fingerprint = reader.GetString(0); position = JournalIndex.ReadCursor(reader, 1); }
                var record = Journal.Read(position); var fact = DecodeLink(record);
                if (record.Operation != operation || record.Fingerprint != fingerprint) throw new InvalidDataException("Resource link locator mismatch.");
                return fact;
            }
        }

        private void ValidateLink(SqliteConnection db, string operation, ConversationResourceOutcomeFact fact)
        {
            var accepted = FindCall(db, fact.ChatId, fact.RunId, fact.CallId);
            if (OperationExists(db, operation) || accepted?.ResourceBinding?.Matches(fact.Outcome) != true)
                throw new InvalidDataException("Resource linkage has no exact accepted call/attempt.");
        }

        private void ApplyLink(SqliteConnection db, string operation, string fingerprint, ConversationResourceOutcomeFact fact, JournalCursor position)
        {
            ValidateLink(db, operation, fact);
            using (var tx = db.BeginTransaction())
            using (var command = Command(db, "INSERT INTO resource_link VALUES($stream,$chat,$run,$call,$operation,$fingerprint,$segment,$start,$end,$sequence,$hash,$first)", tx))
            {
                AddCallKey(command, fact.ChatId, fact.RunId, fact.CallId); JournalIndex.AddPosition(command, position);
                command.Parameters.AddWithValue("$operation", operation); command.Parameters.AddWithValue("$fingerprint", fingerprint);
                RegisterOperation(db, tx, operation, "resource.linked"); command.ExecuteNonQuery(); SaveCursor(db, position, tx); tx.Commit();
            }
        }

        private SqliteConnection OpenIndex(string path = null)
        { return JournalIndex.Open(_paths, path); }

        private SqliteCommand Command(SqliteConnection db, string sql, SqliteTransaction transaction = null)
        { var command = db.CreateCommand(); command.CommandText = sql; command.Transaction = transaction; command.Parameters.AddWithValue("$stream", _scope); return command; }
        private object Scalar(SqliteConnection db, string sql, string chat)
        { using (var command = Command(db, sql)) { command.Parameters.AddWithValue("$chat", chat); return command.ExecuteScalar(); } }
        private static string ReferenceKey(PayloadRef reference)
        { return reference.Sha256 + ":" + reference.ByteLength + ":" + reference.ProtectionKeyId; }
        private sealed class TailReferenceNeeded : Exception
        { internal readonly PayloadRef Reference; internal TailReferenceNeeded(PayloadRef reference) { Reference = reference; } }
        private sealed class OutcomeNeeded : Exception
        { internal readonly ResourceOutcomeLink Link; internal OutcomeNeeded(ResourceOutcomeLink link) { Link = link; } }
    }

    internal sealed class ConversationResourceOutcomeFact
    {
        public string ChatId { get; private set; }
        public string RunId { get; private set; }
        public string CallId { get; private set; }
        public ResourceOutcomeLink Outcome { get; private set; }
        [JsonConstructor]
        internal ConversationResourceOutcomeFact(string chatId, string runId, string callId, ResourceOutcomeLink outcome)
        {
            if (string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(callId) || outcome == null)
                throw new ArgumentException("An exact conversation call and resource outcome are required.");
            ChatId = chatId; RunId = runId; CallId = callId; Outcome = outcome;
        }
    }

    internal sealed class ConversationCallFact
    {
        public string ChatId { get; private set; }
        public ChatMessage Call { get; private set; }
        public ConversationResourceBinding ResourceBinding { get; private set; }
        [JsonConstructor]
        internal ConversationCallFact(string chatId, ChatMessage call, ConversationResourceBinding resourceBinding = null)
        {
            if (string.IsNullOrWhiteSpace(chatId) || call == null || !call.ProtocolMessage || call.Role != "assistant" ||
                call.Activity != null || call.AcceptedCallOrigin == null || string.IsNullOrWhiteSpace(call.ToolCallId) ||
                string.IsNullOrWhiteSpace(call.ToolName) || string.IsNullOrWhiteSpace(call.RunId) ||
                call.EvidenceReference != null || call.ResultPayload != null || call.ResultBody != null)
                throw new ArgumentException("Invalid accepted call fact.");
            if (call.AcceptedCallPayload != null ? !AcceptedCallPayloadService.IsExternalizedCall(call) :
                !ConversationResponseHistoryReader.Read(call).Success) throw new ArgumentException("Invalid accepted protocol.");
            ChatId = chatId; Call = call; ResourceBinding = resourceBinding;
        }
    }

    internal sealed class ConversationCallReceipt
    {
        internal readonly ChatMessage Call;
        internal readonly string Operation, Fingerprint;
        internal readonly JournalCursor Position;
        internal readonly ConversationResourceBinding ResourceBinding;
        internal ConversationCallReceipt(ChatMessage call, string operation, string fingerprint, JournalCursor position, ConversationResourceBinding resourceBinding)
        { Call = call; Operation = operation; Fingerprint = fingerprint; Position = position; ResourceBinding = resourceBinding; }
    }

    internal sealed class ConversationResultFact
    {
        public string ChatId { get; private set; }
        public ChatMessage Result { get; private set; }
        public ResourceOutcomeLink ResourceOutcome { get; private set; }
        [JsonConstructor]
        internal ConversationResultFact(string chatId, ChatMessage result, ResourceOutcomeLink resourceOutcome = null)
        {
            if (string.IsNullOrWhiteSpace(chatId) || result == null || !result.ProtocolMessage || result.Activity != null ||
                result.Role != ToolResultRoles.User || result.ToolResultProtocolVersion != ToolResultWire.CurrentVersion ||
                result.ExecutionProgress == null ||
                result.AcceptedCallOrigin != null || string.IsNullOrWhiteSpace(result.ToolName) || string.IsNullOrWhiteSpace(result.ResultStepId) ||
                string.IsNullOrWhiteSpace(result.ToolCallId) || string.IsNullOrWhiteSpace(result.RunId) ||
                result.ResultBody != null && result.ResultPayload == null) throw new ArgumentException("Invalid typed terminal fact.");
            ChatId = chatId; Result = result; ResourceOutcome = resourceOutcome;
        }
    }

    internal sealed class ConversationResultReceipt
    {
        internal readonly ConversationResultFact Fact;
        internal readonly string OperationId, Fingerprint;
        internal readonly JournalCursor Position;
        internal bool ProjectionAvailable;
        internal ConversationResultReceipt(ConversationResultFact fact, string operation, string fingerprint, JournalCursor position, bool available)
        { Fact = fact; OperationId = operation; Fingerprint = fingerprint; Position = position; ProjectionAvailable = available; }
    }
}
