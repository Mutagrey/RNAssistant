using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ConversationJournal
    {
        // Kernel-authored bounded state, without accepted messages or call-ID lists.
        // A persisted invocation is correlation, never a replacement for a live lease.
        internal RunCheckpointReceipt CommitRun(string operation, string chat, RunCheckpointReceipt expected, string invocation,
            AgentRunState state, ConversationRunPolicy policy, bool resume = false, ModelRunMetadata model = null,
            CancellationToken token = default(CancellationToken))
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            using (RuntimeStorageActivity.Acquire(_paths, token))
            {
                var frozen = JsonConvert.DeserializeObject<AgentRunState>(JsonConvert.SerializeObject(state));
                var references = RunReferences(frozen);
                foreach (var reference in references)
                    if (!_bodies.HasVerifiedReference(reference.ToBlobReference())) throw new InvalidDataException("Run checkpoint references an unavailable body.");
                var body = PayloadRef.FromBlob(_bodies.StoreText(JsonConvert.SerializeObject(frozen), "application/vnd.rnassistant.run-state+json"));
                var fact = new ConversationRunCheckpoint(chat, frozen.Summary.RunId, frozen.Summary.TurnId, invocation,
                    (expected?.Fact.Revision ?? 0) + 1, expected?.Position.Hash, resume, frozen.Summary.Lifecycle,
                    frozen.Summary.PauseId, frozen.Summary.IterationsUsed, frozen.Summary.ToolStepsUsed, frozen.Summary.ToolCounts,
                    frozen.Limits, policy, model, body, references);
                var fingerprint = TextPatternEngine.Sha256(JsonConvert.SerializeObject(fact));
                return Access((db, cursor) => {
                    var prior = FindRun(db, operation);
                    if (prior != null)
                    {
                        if (prior.Fingerprint != fingerprint) throw new InvalidOperationException("Run operation was reused with different content.");
                        return prior;
                    }
                    ValidateRun(db, operation, fact);
                    var record = DeliveryRecord(operation, "run.checkpoint", fact, fingerprint);
                    var position = Journal.Append(cursor, operation, fingerprint, record.Kind, record.Data);
                    var projected = true;
                    try { Fault?.Invoke("before_index"); ApplyRun(db, record, position); }
                    catch (Exception error) when (error is IOException || error is SqliteException) { projected = false; }
                    return new RunCheckpointReceipt(operation, fingerprint, fact, position, projected);
                }, token);
            }
        }

        internal RunCheckpointReceipt CurrentRun(string chat, CancellationToken token = default(CancellationToken))
        { return Access((db, cursor) => CurrentRun(db, chat), token); }

        internal RunCheckpointReceipt RunReceipt(string operation, CancellationToken token = default(CancellationToken))
        { return Access((db, cursor) => FindRun(db, operation), token); }

        internal AgentRunState ReadRunState(RunCheckpointReceipt receipt)
        {
            using (RuntimeStorageActivity.Acquire(_paths))
            {
                if (receipt == null) throw new ArgumentNullException(nameof(receipt));
                var record = Journal.Read(receipt.Position); var fact = DecodeRun(record);
                if (record.Operation != receipt.Operation || record.Fingerprint != receipt.Fingerprint)
                    throw new InvalidDataException("Foreign run checkpoint receipt.");
                return ReadRunBody(fact);
            }
        }

        internal void ValidateRunClosure(JournalRecord record)
        { ReadRunBody(DecodeRun(record)); }

        private AgentRunState ReadRunBody(ConversationRunCheckpoint fact)
        {
            var state = JsonConvert.DeserializeObject<AgentRunState>(_bodies.ReadText(fact.Body.ToBlobReference()),
                new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error });
            var summary = state?.Summary;
            if (summary == null || summary.RunId != fact.RunId || summary.TurnId != fact.TurnId || summary.Lifecycle != fact.Lifecycle ||
                summary.PauseId != fact.PauseId || summary.IterationsUsed != fact.IterationsUsed || summary.ToolStepsUsed != fact.ToolStepsUsed ||
                !SameRunValue(summary.ToolCounts, fact.Counts) || !SameRunValue(state.Limits, fact.Limits) || !SameRunValue(RunReferences(state), fact.References))
                throw new InvalidDataException("Checkpoint body differs from its accepted metadata/closure.");
            return state;
        }

        private RunCheckpointReceipt CurrentRun(SqliteConnection db, string chat)
        {
            var operation = Scalar(db, "SELECT operation FROM run_current WHERE stream=$stream AND chat=$chat", chat) as string;
            var receipt = operation == null ? null : FindRun(db, operation);
            if (receipt != null && receipt.Fact.ChatId != chat) throw new InvalidDataException("Foreign current run locator.");
            return receipt;
        }

        private RunCheckpointReceipt FindRun(SqliteConnection db, string operation)
        {
            using (var command = Command(db, "SELECT operation,fingerprint,segment,start_offset,end_offset,sequence,hash,first_sequence FROM run_fact WHERE stream=$stream AND operation=$operation"))
            {
                command.Parameters.AddWithValue("$operation", operation);
                JournalCursor position; var record = LocatedRecord(command, out position);
                if (record == null) return null;
                if (record.Operation != operation) throw new InvalidDataException("Foreign run operation locator.");
                return new RunCheckpointReceipt(operation, record.Fingerprint, DecodeRun(record), position, true);
            }
        }

        private void ValidateRun(SqliteConnection db, string operation, ConversationRunCheckpoint fact)
        {
            if (string.IsNullOrWhiteSpace(operation) || OperationExists(db, operation) || Card(db, fact.ChatId) == null)
                throw new InvalidOperationException("Run operation/chat identity conflicts.");
            var prior = CurrentRun(db, fact.ChatId);
            if (fact.PreviousHash != prior?.Position.Hash || fact.Revision != (prior?.Fact.Revision ?? 0) + 1)
                throw new InvalidOperationException("Run checkpoint guard is stale.");
            if (fact.Lifecycle == RunLifecycle.Running && (prior == null || prior.Fact.RunId != fact.RunId ||
                prior.Fact.Lifecycle != RunLifecycle.Running) && QueueControl(db, fact.ChatId)?.Fact.Paused == true)
                throw new InvalidOperationException("The paused queue requires an explicit matching release before continuation.");
            if (prior != null && prior.Fact.RunId == fact.RunId)
            {
                var old = prior.Fact;
                if (TerminalRun(old.Lifecycle) || fact.TurnId != old.TurnId || !SameRunValue(fact.Limits, old.Limits) ||
                    !SameRunValue(fact.Policy, old.Policy) || !SameRunValue(fact.Model, old.Model) ||
                    fact.IterationsUsed < old.IterationsUsed || fact.ToolStepsUsed < old.ToolStepsUsed ||
                    fact.Counts.ReadOk < old.Counts.ReadOk || fact.Counts.ReadError < old.Counts.ReadError ||
                    fact.Counts.WriteOk < old.Counts.WriteOk || fact.Counts.WriteError < old.Counts.WriteError || fact.Counts.WriteUnknown < old.Counts.WriteUnknown)
                    throw new InvalidOperationException("Run checkpoint cannot reset terminal state, budget, policy or effect counts.");
                if (fact.Resume ? fact.InvocationId == old.InvocationId || fact.Lifecycle != RunLifecycle.Running ||
                    old.Lifecycle != RunLifecycle.Waiting && old.Lifecycle != RunLifecycle.AwaitingConfirmation : fact.InvocationId != old.InvocationId)
                    throw new InvalidOperationException("Run invocation changed without an explicit saved continuation.");
            }
            else
            {
                if (prior != null && !TerminalRun(prior.Fact.Lifecycle) || fact.Resume || fact.Lifecycle != RunLifecycle.Running ||
                    fact.IterationsUsed != 0 || fact.ToolStepsUsed != 0 || !SameRunValue(fact.Counts, new ToolCounts()))
                    throw new InvalidOperationException("A new run requires an idle predecessor and empty counters.");
                using (var command = Command(db, "SELECT 1 FROM run_identity WHERE stream=$stream AND chat=$chat AND run=$run"))
                {
                    command.Parameters.AddWithValue("$chat", fact.ChatId); command.Parameters.AddWithValue("$run", fact.RunId);
                    if (command.ExecuteScalar() != null) throw new InvalidOperationException("A historical run ID cannot be reused.");
                }
            }
        }

        private void ApplyRun(SqliteConnection db, JournalRecord record, JournalCursor position)
        {
            var fact = DecodeRun(record); ValidateRun(db, record.Operation, fact);
            using (var tx = db.BeginTransaction())
            {
                RegisterOperation(db, tx, record.Operation, record.Kind);
                ApplyRunRows(db, tx, record, position, fact);
                SaveCursor(db, position, tx); tx.Commit();
            }
        }

        private void ApplyRunRows(SqliteConnection db, SqliteTransaction tx, JournalRecord record,
            JournalCursor position, ConversationRunCheckpoint fact)
        {
            using (var command = Command(db, "INSERT INTO run_fact VALUES($stream,$operation,$chat,$run,$revision,$fingerprint,$segment,$start,$end,$sequence,$hash,$first); INSERT INTO run_current VALUES($stream,$chat,$operation) ON CONFLICT(stream,chat) DO UPDATE SET operation=$operation; INSERT OR IGNORE INTO run_identity VALUES($stream,$chat,$run)", tx))
            {
                command.Parameters.AddWithValue("$chat", fact.ChatId); command.Parameters.AddWithValue("$run", fact.RunId);
                command.Parameters.AddWithValue("$revision", fact.Revision); AddDeliveryPosition(command, record, position); command.ExecuteNonQuery();
            }
        }

        private static ConversationRunCheckpoint DecodeRun(JournalRecord record)
        {
            if (record.Kind == "chat.committed")
                return DecodeChatCommit(record).Checkpoint ?? throw new InvalidDataException("Missing atomic run checkpoint.");
            var fact = DeliveryData<ConversationRunCheckpoint>(record);
            if (record.Kind != "run.checkpoint" || fact == null || record.Fingerprint != TextPatternEngine.Sha256(JsonConvert.SerializeObject(fact)))
                throw new InvalidDataException("Invalid run checkpoint.");
            return fact;
        }

        private static PayloadRef[] RunReferences(AgentRunState state)
        {
            var scan = new CasReachabilityScan();
            scan.AddTokenReferences(JObject.FromObject(state), "run-checkpoint", state.Summary.RunId, "state");
            // Prepared-state JSON is an explicit nested document, not a model body.
            foreach (var prepared in new[] { state.InFlightTool?.PreparedStateJson, state.Summary.PendingConfirmation?.PreparedStateJson })
                if (prepared != null) scan.AddTokenReferences(JToken.Parse(prepared), "run-checkpoint", state.Summary.RunId, "prepared");
            if (scan.Issues.Count != 0) throw new InvalidDataException("Run checkpoint contains invalid CAS references.");
            return scan.References.Select(value => PayloadRef.FromBlob(value.Reference)).GroupBy(ReferenceKey).Select(group => group.First())
                .OrderBy(ReferenceKey, StringComparer.Ordinal).ToArray();
        }
        private static bool SameRunValue(object left, object right)
        { return JToken.DeepEquals(left == null ? null : JToken.FromObject(left), right == null ? null : JToken.FromObject(right)); }
        private static bool TerminalRun(RunLifecycle lifecycle)
        { return lifecycle == RunLifecycle.Completed || lifecycle == RunLifecycle.Failed || lifecycle == RunLifecycle.Cancelled; }
    }

    internal sealed class ConversationRunCheckpoint
    {
        public string ChatId { get; private set; }
        public string RunId { get; private set; }
        public string TurnId { get; private set; }
        public string InvocationId { get; private set; }
        public long Revision { get; private set; }
        public string PreviousHash { get; private set; }
        public bool Resume { get; private set; }
        public RunLifecycle Lifecycle { get; private set; }
        public string PauseId { get; private set; }
        public int IterationsUsed { get; private set; }
        public int ToolStepsUsed { get; private set; }
        public ToolCounts Counts { get; private set; }
        public AgentRunLimits Limits { get; private set; }
        public ConversationRunPolicy Policy { get; private set; }
        public ModelRunMetadata Model { get; private set; }
        public PayloadRef Body { get; private set; }
        public IReadOnlyList<PayloadRef> References { get; private set; }
        [JsonConstructor]
        internal ConversationRunCheckpoint(string chatId, string runId, string turnId, string invocationId, long revision,
            string previousHash, bool resume, RunLifecycle lifecycle, string pauseId, int iterationsUsed, int toolStepsUsed,
            ToolCounts counts, AgentRunLimits limits, ConversationRunPolicy policy, ModelRunMetadata model, PayloadRef body, IEnumerable<PayloadRef> references)
        {
            if (new[] { chatId, runId, turnId, invocationId }.Any(string.IsNullOrWhiteSpace) || revision < 1 ||
                (revision == 1 ? previousHash != null : string.IsNullOrWhiteSpace(previousHash)) || !Enum.IsDefined(typeof(RunLifecycle), lifecycle) ||
                pauseId != null && (string.IsNullOrWhiteSpace(pauseId) || lifecycle != RunLifecycle.Waiting) ||
                iterationsUsed < 0 || toolStepsUsed < 0 || counts == null || limits == null || policy == null || body == null || references == null)
                throw new ArgumentException("Invalid run checkpoint metadata.");
            ChatId = chatId; RunId = runId; TurnId = turnId; InvocationId = invocationId; Revision = revision; PreviousHash = previousHash;
            Resume = resume; Lifecycle = lifecycle; PauseId = pauseId; IterationsUsed = iterationsUsed; ToolStepsUsed = toolStepsUsed;
            Counts = counts; Limits = limits; Policy = policy; Body = body; References = Array.AsReadOnly(references.ToArray());
            if (References.Any(reference => reference == null)) throw new ArgumentException("Invalid run checkpoint closure.");
            Model = model == null ? null : JsonConvert.DeserializeObject<ModelRunMetadata>(JsonConvert.SerializeObject(model));
        }
    }

    internal sealed class RunCheckpointReceipt
    {
        internal readonly string Operation, Fingerprint;
        internal readonly ConversationRunCheckpoint Fact;
        internal readonly JournalCursor Position;
        internal readonly bool ProjectionAvailable;
        internal RunCheckpointReceipt(string operation, string fingerprint, ConversationRunCheckpoint fact, JournalCursor position, bool projected)
        { Operation = operation; Fingerprint = fingerprint; Fact = fact; Position = position; ProjectionAvailable = projected; }
    }
}
