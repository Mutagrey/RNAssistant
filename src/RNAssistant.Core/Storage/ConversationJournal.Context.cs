using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using RNAssistant.Core.Agent;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ConversationJournal
    {
        private const int ContextCandidateLimit = 64;

        // Tool facts only. User input/instructions remain owned by their producers.
        // The checked stream cursor and all selections belong to one index admission.
        internal ConversationContextWindow RestoreContext(string chat, string run, int recentResults, int workingBudget,
            CancellationToken token = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(chat) || string.IsNullOrWhiteSpace(run) || recentResults < 0 || recentResults > ContextCandidateLimit)
                throw new ArgumentException("An exact context scope and bounded recent window are required.");
            return Access((db, cursor) => {
                var recent = recentResults == 0 ? new ConversationResultReceipt[0]
                    : ReadWindow(db, chat, long.MaxValue, recentResults).ToArray();
                var active = new HashSet<string>(recent.Select(item => item.OperationId), StringComparer.Ordinal);
                var summary = new ContextRequirementSummary(
                    ContextOperations(db, chat, "SELECT operation FROM context_required WHERE stream=$stream AND chat=$chat AND unknown_effect=1 LIMIT $limit", 1).Count != 0,
                    ContextOperations(db, chat, "SELECT operation FROM context_required WHERE stream=$stream AND chat=$chat AND run=$run AND retry<>0 LIMIT $limit", 1, run).Count != 0);
                // Only the latest terminal is pinned. Unbounded obligations remain
                // addressable in the index, independently of prompt membership.
                var required = new HashSet<string>(ContextOperations(db, chat,
                    "SELECT operation FROM result_fact WHERE stream=$stream AND chat=$chat AND run=$run ORDER BY sequence DESC LIMIT $limit", 1, run), StringComparer.Ordinal);
                required.ExceptWith(active);

                var operations = new HashSet<string>(required, StringComparer.Ordinal);
                var bodies = new HashSet<string>(StringComparer.Ordinal);
                if (workingBudget > 0)
                {
                    operations.UnionWith(ContextOperations(db, chat,
                        "SELECT operation FROM result_fact WHERE stream=$stream AND chat=$chat AND context_optional=1 AND sequence<$before ORDER BY sequence DESC LIMIT $limit",
                        ContextCandidateLimit, before: recent.Length == 0 ? long.MaxValue : recent[0].Position.Sequence));
                    bodies.UnionWith(ContextOperations(db, chat,
                        "SELECT DISTINCT operation,sequence FROM context_body WHERE stream=$stream AND chat=$chat ORDER BY sequence DESC LIMIT $limit",
                        ContextCandidateLimit + active.Count).Where(operation => !active.Contains(operation)).Take(ContextCandidateLimit));
                }
                var loaded = recent.ToDictionary(item => item.OperationId, StringComparer.Ordinal);
                foreach (var operation in operations.Concat(bodies).Distinct(StringComparer.Ordinal))
                {
                    if (loaded.ContainsKey(operation)) continue;
                    var receipt = Find(db, operation);
                    if (receipt == null || receipt.Fact.ChatId != chat)
                        throw new InvalidDataException("Context locator belongs to another conversation or has no result.");
                    loaded.Add(operation, receipt);
                }
                var calls = new Dictionary<string, ChatMessage>(StringComparer.Ordinal);
                foreach (var item in loaded.Values)
                {
                    var result = item.Fact.Result;
                    var call = FindCall(db, chat, result.RunId, result.ToolCallId);
                    if (call == null) throw new InvalidDataException("Context result has no accepted call.");
                    calls.Add(item.OperationId, call.Call);
                }
                var working = new ContextWorkingSet { Budget = Math.Max(0, workingBudget), Requirements = summary };
                foreach (var item in loaded.Values.OrderBy(item => item.Position.Sequence))
                {
                    if (active.Contains(item.OperationId)) continue;
                    var candidate = new ContextWorkingSet.Candidate(calls[item.OperationId], item.Fact.Result, required.Contains(item.OperationId));
                    if (operations.Contains(item.OperationId)) working.Operations.Add(candidate);
                    if (bodies.Contains(item.OperationId)) working.Bodies.Add(candidate);
                }
                var messages = recent.SelectMany(item => new[] { calls[item.OperationId], item.Fact.Result }).ToArray();
                return new ConversationContextWindow(messages, working, cursor);
            }, token);
        }

        // Runtime-only addresses. Model-facing discovery/read will use the shared
        // semantic query surface at cutover, never raw operation IDs or cursors.
        internal IReadOnlyList<ConversationContextOperation> ReadContextRequirements(string chat, string run,
            long beforeSequence, int limit, CancellationToken token = default(CancellationToken))
        {
            ValidateWindow(chat, beforeSequence, limit);
            if (limit > ContextCandidateLimit || string.IsNullOrWhiteSpace(run)) throw new ArgumentException("A bounded requirement page and exact run are required.");
            return Access((db, cursor) => {
                var unknown = ContextOperations(db, chat,
                    "SELECT operation FROM context_required WHERE stream=$stream AND chat=$chat AND unknown_effect=1 AND sequence<$before ORDER BY sequence DESC LIMIT $limit", limit, before: beforeSequence);
                var recovery = ContextOperations(db, chat,
                    "SELECT operation FROM context_required WHERE stream=$stream AND chat=$chat AND run=$run AND retry<>0 AND sequence<$before ORDER BY sequence DESC LIMIT $limit", limit, run, beforeSequence);
                return (IReadOnlyList<ConversationContextOperation>)unknown.Concat(recovery).Distinct(StringComparer.Ordinal)
                    .Select(operation => RequireContextOperation(db, chat, operation)).OrderByDescending(item => item.Result.Position.Sequence).Take(limit).ToArray();
            }, token);
        }

        internal ConversationContextOperation ReadContextOperation(string chat, string operation,
            CancellationToken token = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(chat) || string.IsNullOrWhiteSpace(operation)) throw new ArgumentException("An exact context address is required.");
            return Access((db, cursor) => ReadContextOperation(db, chat, operation), token);
        }

        // This is a guard lookup, not a dispatch decision. The existing tracker
        // still applies policy (including fresh explicit opaque confirmation).
        internal ConversationContextOperation FindContextReplayGuard(string chat, ToolCall call,
            CancellationToken token = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(chat) || call == null) throw new ArgumentException("An exact chat and proposed call are required.");
            return Access((db, cursor) => FindContextReplayGuard(db, chat, call), token);
        }

        bool IEventStore.RetainedReplayGuards { get { return true; } }
        ToolExecutionProgress IEventStore.ReadReplayGuard(ConversationMetadata session, ToolCall call)
        {
            if (call == null) throw new ArgumentNullException(nameof(call));
            return Access((db, cursor) => {
                RequireChatRevision(session, TextChat(db, session.Id));
                return FindContextReplayGuard(db, session.Id, call)?.Result.Fact.Result.ExecutionProgress;
            }, CancellationToken.None);
        }

        private ConversationContextOperation FindContextReplayGuard(SqliteConnection db, string chat, ToolCall call)
        {
            using (var command = Command(db, "SELECT operation FROM context_required WHERE stream=$stream AND chat=$chat AND signature=$signature AND repeat_guard=1 ORDER BY sequence DESC LIMIT 1"))
            {
                command.Parameters.AddWithValue("$chat", chat); command.Parameters.AddWithValue("$signature", AgentProgressTracker.Signature(call));
                var operation = command.ExecuteScalar() as string;
                if (operation == null) return null;
                var item = RequireContextOperation(db, chat, operation);
                if (item.Result.Fact.Result.ExecutionProgress.Outcome != ToolExecutionOutcome.Unknown ||
                    item.Result.Fact.Result.ExecutionProgress.OpaqueActionCompleted || item.Call.ToolName != call.Name)
                    throw new InvalidDataException("Repeat guard disagrees with its canonical terminal.");
                return item;
            }
        }

        private ConversationContextOperation RequireContextOperation(SqliteConnection db, string chat, string operation)
        {
            return ReadContextOperation(db, chat, operation) ??
                throw new InvalidDataException("Context locator belongs to another conversation or has no result.");
        }

        private ConversationContextOperation ReadContextOperation(SqliteConnection db, string chat, string operation)
        {
            var receipt = Find(db, operation);
            if (receipt == null || receipt.Fact.ChatId != chat) return null;
            var result = receipt.Fact.Result;
            var call = FindCall(db, chat, result.RunId, result.ToolCallId);
            if (call == null) throw new InvalidDataException("Context result has no accepted call.");
            using (var command = Command(db, "SELECT unknown_effect,retry FROM context_required WHERE stream=$stream AND chat=$chat AND operation=$operation"))
            {
                command.Parameters.AddWithValue("$chat", chat); command.Parameters.AddWithValue("$operation", operation);
                using (var reader = command.ExecuteReader())
                    return reader.Read() ? new ConversationContextOperation(call.Call, receipt, reader.GetInt32(0) != 0, reader.GetInt32(1) != 0)
                        : new ConversationContextOperation(call.Call, receipt);
            }
        }

        private sealed class ContextCallKeyNeeded : Exception
        {
            internal readonly ChatMessage Call;
            internal readonly string Fingerprint;
            internal ContextCallKeyNeeded(ChatMessage call, string fingerprint) { Call = call; Fingerprint = fingerprint; }
        }

        private sealed class ContextCallKey
        {
            internal readonly string Fingerprint, Signature;
            internal ContextCallKey(string fingerprint, string signature) { Fingerprint = fingerprint; Signature = signature; }
        }

        private ContextCallKey PrepareContextCallKey(ChatMessage call, string fingerprint)
        {
            var parsed = ConversationResponseHistoryReader.Read(AcceptedCallPayloadService.Hydrate(call, _bodies));
            if (!parsed.Success || parsed.Response.ToolCalls.Count != 1) throw new InvalidDataException("Context call protocol is invalid.");
            return new ContextCallKey(fingerprint, AgentProgressTracker.Signature(parsed.Response.ToolCalls[0]));
        }

        private IReadOnlyList<string> ContextOperations(SqliteConnection db, string chat, string sql, int limit,
            string run = null, long before = long.MaxValue)
        {
            using (var command = Command(db, sql))
            {
                command.Parameters.AddWithValue("$chat", chat); command.Parameters.AddWithValue("$limit", limit);
                if (run != null) command.Parameters.AddWithValue("$run", run);
                if (sql.Contains("$before")) command.Parameters.AddWithValue("$before", before);
                var result = new List<string>();
                using (var reader = command.ExecuteReader()) while (reader.Read()) result.Add(reader.GetString(0));
                return result;
            }
        }

        private void ApplyContextIndex(SqliteConnection db, SqliteTransaction tx, ConversationResultFact fact,
            string operation, JournalCursor position)
        {
            var result = fact.Result;
            var progress = result.ExecutionProgress;
            // Resolve only from later successful facts, including excluded prompt bodies.
            if (progress.Outcome == ToolExecutionOutcome.Ok) ResolveContextRecovery(db, tx, fact.ChatId, result);
            ToolResultWireReadResult wire; string error;
            if (!ToolResultHistoryReader.TryRead(result, out wire, out error)) throw new InvalidDataException(error);
            if (wire.Result.Status != ToolResultStatus.Ok || result.ResourceEffect != null || result.EvidenceReference != null)
                using (var command = Command(db, "UPDATE result_fact SET context_optional=1 WHERE stream=$stream AND operation=$operation", tx))
                { command.Parameters.AddWithValue("$operation", operation); command.ExecuteNonQuery(); }
            var unknown = ContextWorkingSet.RequiresUnknownRetention(result, wire.Result.Status);
            var recovery = progress.Outcome == ToolExecutionOutcome.Error ? progress.Recovery : null;
            var retry = recovery?.RetryPolicy ?? ToolRetryPolicy.None;
            if (unknown || retry != ToolRetryPolicy.None)
            {
                using (var command = Command(db, "INSERT INTO context_required SELECT $stream,$chat,$operation,$run,$sequence,$unknown,$retry,$uri,$view,$read_scope,signature,$repeat_guard FROM call_fact WHERE stream=$stream AND chat=$chat AND run=$run AND call=$call", tx))
                {
                    command.Parameters.AddWithValue("$chat", fact.ChatId); command.Parameters.AddWithValue("$operation", operation);
                    command.Parameters.AddWithValue("$run", result.RunId); command.Parameters.AddWithValue("$sequence", position.Sequence);
                    command.Parameters.AddWithValue("$unknown", unknown ? 1 : 0); command.Parameters.AddWithValue("$retry", (int)retry);
                    command.Parameters.AddWithValue("$call", result.ToolCallId);
                    command.Parameters.AddWithValue("$repeat_guard", progress.Outcome == ToolExecutionOutcome.Unknown && !progress.OpaqueActionCompleted ? 1 : 0);
                    command.Parameters.AddWithValue("$uri", (object)recovery?.ResourceIdentity?.Uri ?? DBNull.Value);
                    command.Parameters.AddWithValue("$view", (object)recovery?.View ?? DBNull.Value);
                    command.Parameters.AddWithValue("$read_scope", (int)(recovery?.ReadScope ?? ToolRecoveryReadScope.None));
                    command.ExecuteNonQuery();
                }
                foreach (var dependency in recovery?.ChangeDependencies ?? new ResourceRef[0])
                    using (var command = Command(db, "INSERT INTO context_dependency VALUES($stream,$chat,$operation,$uri,$revision)", tx))
                    {
                        command.Parameters.AddWithValue("$chat", fact.ChatId); command.Parameters.AddWithValue("$operation", operation);
                        command.Parameters.AddWithValue("$uri", dependency.Uri); command.Parameters.AddWithValue("$revision", dependency.Revision);
                        command.ExecuteNonQuery();
                    }
            }
            if (result.ResourceEvidence.Count == 0 || !ModelContextCompiler.ExposesBody(result)) return;
            var body = result.ResultPayload?.Sha256 ?? TextPatternEngine.Sha256(result.Content);
            foreach (var evidence in result.ResourceEvidence)
            {
                var key = TextPatternEngine.Sha256(JsonConvert.SerializeObject(new { evidence.ScopeId, evidence.Resource.Uri,
                    evidence.Resource.Revision, evidence.View, evidence.Coverage, evidence.Complete, body }));
                using (var command = Command(db, "INSERT OR REPLACE INTO context_body VALUES($stream,$chat,$key,$operation,$sequence)", tx))
                {
                    command.Parameters.AddWithValue("$chat", fact.ChatId); command.Parameters.AddWithValue("$key", key);
                    command.Parameters.AddWithValue("$operation", operation); command.Parameters.AddWithValue("$sequence", position.Sequence);
                    command.ExecuteNonQuery();
                }
            }
        }

        private void ResolveContextRecovery(SqliteConnection db, SqliteTransaction tx, string chat, ChatMessage result)
        {
            foreach (var evidence in result.ResourceEvidence ?? new List<ResourceEvidence>())
            {
                var whole = evidence.Complete && evidence.Coverage.Kind == ResourceCoverageKinds.Whole;
                var lines = evidence.Coverage.Kind == ResourceCoverageKinds.CharacterRange;
                if (!whole && !lines) continue;
                ClearContextRecovery(db, tx, chat, "retry=$refresh AND uri=$uri AND view=$view" +
                    (whole ? "" : " AND read_scope=$lines"), command => {
                        command.Parameters.AddWithValue("$refresh", (int)ToolRetryPolicy.RefreshRequired);
                        command.Parameters.AddWithValue("$uri", evidence.Resource.Uri); command.Parameters.AddWithValue("$view", evidence.View);
                        if (!whole) command.Parameters.AddWithValue("$lines", (int)ToolRecoveryReadScope.RelevantLines);
                    });
            }
            var effect = result.ResourceEffect;
            if (effect == null) return;
            var changed = effect.Outcome == ResourceEffectOutcome.VerifiedChanged || effect.Outcome == ResourceEffectOutcome.Restored;
            if (!changed && effect.Outcome != ResourceEffectOutcome.ExternalDriftObserved) return;
            foreach (var impact in effect.Impacts)
            {
                ClearContextRecovery(db, tx, chat, "uri=$uri AND (retry=$refresh" + (changed ? " OR retry=$replan)" : ")"), command => {
                    command.Parameters.AddWithValue("$uri", impact.Identity.Uri); command.Parameters.AddWithValue("$refresh", (int)ToolRetryPolicy.RefreshRequired);
                    if (changed) command.Parameters.AddWithValue("$replan", (int)ToolRetryPolicy.Replan);
                });
                if (!changed || impact.Relation != ResourceImpactRelation.Exact || impact.Coverage?.Kind != ResourceCoverageKinds.Whole ||
                    impact.Before?.IsExact != true || impact.After?.IsExact != true ||
                    !impact.Identity.Equals(impact.Before.Identity) || !impact.Identity.Equals(impact.After.Identity) ||
                    impact.Before.Revision == impact.After.Revision) continue;
                ClearContextRecovery(db, tx, chat,
                    "retry=$change AND operation IN (SELECT operation FROM context_dependency WHERE stream=$stream AND chat=$chat AND uri=$uri AND revision<>$revision)", command => {
                        command.Parameters.AddWithValue("$change", (int)ToolRetryPolicy.ChangeRequired);
                        command.Parameters.AddWithValue("$uri", impact.Identity.Uri); command.Parameters.AddWithValue("$revision", impact.After.Revision);
                    });
            }
        }

        private void ClearContextRecovery(SqliteConnection db, SqliteTransaction tx, string chat, string predicate,
            Action<SqliteCommand> parameters)
        {
            // Keep unknown effect authority even if the separate recovery is satisfied.
            var match = "stream=$stream AND chat=$chat AND " + predicate;
            using (var command = Command(db,
                "DELETE FROM context_required WHERE " + match + " AND unknown_effect=0; " +
                "UPDATE context_required SET retry=0 WHERE " + match + " AND unknown_effect=1", tx))
            {
                command.Parameters.AddWithValue("$chat", chat); parameters(command); command.ExecuteNonQuery();
            }
        }
    }

    internal sealed class ConversationContextOperation
    {
        internal readonly ChatMessage Call;
        internal readonly ConversationResultReceipt Result;
        internal readonly bool UnknownEffect, RecoveryRequired;
        internal ConversationContextOperation(ChatMessage call, ConversationResultReceipt result, bool unknownEffect = false, bool recoveryRequired = false)
        { Call = call; Result = result; UnknownEffect = unknownEffect; RecoveryRequired = recoveryRequired; }
    }

    internal sealed class ConversationContextWindow
    {
        internal readonly IReadOnlyList<ChatMessage> Recent;
        internal readonly ContextWorkingSet WorkingSet;
        internal readonly JournalCursor Through;
        internal ConversationContextWindow(IReadOnlyList<ChatMessage> recent, ContextWorkingSet workingSet, JournalCursor through)
        { Recent = recent; WorkingSet = workingSet; Through = through; }
    }
}
