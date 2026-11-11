using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    // Logical scopes and the existing resource ports share one canonical stream
    // and index. Execution and resource leases stay with their domain owners.
    internal sealed partial class ResourceJournal
    {
        private readonly AppDataPaths _paths;
        private readonly StorageProtector _protector;
        private readonly ChatBlobStore _bodies;
        internal readonly StorageJournal Journal;
        internal Action<string> Fault;

        internal ResourceJournal(AppDataPaths paths, StorageProtector protector = null)
        {
            _paths = paths ?? throw new ArgumentNullException(nameof(paths)); _protector = protector ?? StorageProtector.None;
            if (Directory.Exists(paths.ResourceAuthorityDirectory) && Directory.EnumerateFiles(paths.ResourceAuthorityDirectory, "*.jsonl").Any())
                throw new InvalidOperationException("Resource journal requires a new root; legacy resources must remain separately preserved.");
            _bodies = new ChatBlobStore(paths, () => _protector);
            RegisterObserver(paths.ResourceAuthorityDirectory, this);
            Journal = new StorageJournal(Path.Combine(paths.Root, "resource-stream"), "resources", _protector);
        }

        internal ResourceJournalReceipt Prepare(string operation, MutationAttempt attempt, CancellationToken token = default(CancellationToken))
        {
            if (attempt == null || attempt.State != MutationAttemptState.Prepared || attempt.LinkedAuthorityCommitId != null)
                throw new ArgumentException("An exact prepared resource attempt is required.");
            return Commit(operation, new ResourceJournalFact { Kind = "resource.prepared", Scope = attempt.ScopeId, Attempt = attempt }, token);
        }

        internal ResourceJournalReceipt MarkDispatch(string operation, ResourceAuthorityScopeId scope, string attemptId,
            CancellationToken token = default(CancellationToken))
        {
            using (RuntimeStorageActivity.Acquire(_paths, token))
            {
                var prior = Attempt(scope, attemptId, token);
                if (prior == null) throw new InvalidOperationException("Exact prepared attempt is unavailable.");
                foreach (var reference in References(prior.Fact)) Verify(reference);
                return Commit(operation, new ResourceJournalFact { Kind = "resource.dispatch", Scope = scope,
                    AttemptId = attemptId }, token);
            }
        }

        internal ResourceJournalReceipt Publish(string operation, ResourcePublication publication,
            CancellationToken token = default(CancellationToken))
        {
            if (publication == null) throw new ArgumentNullException(nameof(publication));
            return Commit(operation, new ResourceJournalFact { Kind = "resource.published", Scope = publication.Scope,
                Publication = publication }, token);
        }

        internal ResourceJournalReceipt Receipt(ResourceAuthorityScopeId scope, string operation,
            CancellationToken token = default(CancellationToken))
        { return Access((db, cursor) => Find(db, scope, operation), token); }

        internal ResourceJournalReceipt Attempt(ResourceAuthorityScopeId scope, string attemptId,
            CancellationToken token = default(CancellationToken))
        { return Access((db, cursor) => FindAttempt(db, scope, attemptId), token); }

        internal ResourceHeadState Head(ResourceAuthorityScopeId scope, ResourceIdentity identity,
            CancellationToken token = default(CancellationToken))
        { return Access((db, cursor) => Head(db, scope, identity), token); }

        internal ResourceRevisionMetadata Revision(ResourceAuthorityScopeId scope, ResourceRef reference, CancellationToken token = default(CancellationToken))
        { return Access((db, cursor) => RevisionOf(FindRevision(db, scope, reference)?.Fact), token); }

        internal ResourceRevisionView View(ResourceAuthorityScopeId scope, ResourceRef reference, string view, ResourceCoverage coverage,
            CancellationToken token = default(CancellationToken))
        { return Access((db, cursor) => ViewOf(FindView(db, scope, reference, view, coverage)?.Fact), token); }

        internal ResourceJournalReceipt ReadReceipt(ResourceJournalReceipt receipt)
        {
            if (receipt == null || receipt.Root != _paths.Root) throw new ArgumentException("Foreign resource receipt root.");
            // Immutable direct locator: no resource catch-up or append lock is
            // needed while preparing a later conversation linkage.
            return Read(receipt.Fact.Scope, receipt.Operation, receipt.Fingerprint, receipt.Position);
        }

        internal ResourceJournalReceipt ReadOutcome(ResourceOutcomeLink link)
        {
            if (link == null) throw new ArgumentNullException(nameof(link));
            var receipt = Read(link.Scope, link.Operation, link.Fingerprint, link.Position);
            var actual = ResourceOutcomeLink.From(receipt);
            if (actual.AttemptId != link.AttemptId || actual.CommitId != link.CommitId || !actual.Target.Equals(link.Target))
                throw new InvalidDataException("Resource outcome link identifies a different attempt/publication.");
            return receipt;
        }

        private ResourceJournalReceipt Commit(string operation, ResourceJournalFact proposed, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(operation) || proposed.Scope == null) throw new ArgumentException("Exact resource operation/scope required.");
            using (RuntimeStorageActivity.Acquire(_paths, token))
            {
                var fact = JObject.FromObject(proposed).ToObject<ResourceJournalFact>();
                ValidateShape(fact);
                if (fact.Kind == "resource.prepared" && fact.Call != null) VerifyCall(fact.Call);
                foreach (var reference in References(fact)) Verify(reference);
                var fingerprint = Fingerprint(fact);
                return Access((db, cursor) => {
                    var prior = Find(db, fact.Scope, operation);
                    if (prior != null)
                    {
                        if (prior.Fingerprint != fingerprint) throw new InvalidOperationException("Resource operation identity was reused with different content.");
                        prior.Duplicate = true;
                        return prior;
                    }
                    PrepareTransition(db, fact);
                    var position = Journal.Append(cursor, operation, fingerprint, fact.Kind, JObject.FromObject(fact));
                    var receipt = new ResourceJournalReceipt(_paths.Root, operation, fingerprint, fact, position, true);
                    try { Fault?.Invoke("before_index"); Apply(db, operation, fingerprint, fact, position); }
                    catch (Exception error) when (error is SqliteException || error is IOException) { receipt.ProjectionAvailable = false; }
                    return receipt;
                }, token);
            }
        }

        private void PrepareTransition(SqliteConnection db, ResourceJournalFact fact)
        {
            if (PrepareStoreTransition(db, fact)) return;
            if (fact.Kind == "resource.prepared")
            {
                if (fact.Call != null && OriginAttempt(db, fact.Call.Origin) != null)
                    throw new InvalidOperationException("This runtime call already has a resource preparation.");
                if (AttemptScope(db, fact.Attempt.AttemptId) != null) throw new InvalidOperationException("Resource attempt is already prepared.");
                var head = Head(db, fact.Scope, fact.Attempt.Target);
                if (fact.Attempt.ExpectedRevision != null && head?.Revision?.Revision != fact.Attempt.ExpectedRevision ||
                    head != null && head.Knowledge == HeadKnowledge.Unknown)
                    throw new ResourceAuthorityConflictException("Prepared resource revision changed.");
                fact.PreparedHead = head;
                return;
            }
            var attemptId = fact.Kind == "resource.dispatch" ? fact.AttemptId : fact.Publication.AttemptId;
            var predecessor = attemptId == null ? null : FindAttempt(db, fact.Scope, attemptId);
            var attempt = predecessor?.Fact.Attempt;
            fact.Call = predecessor?.Fact.Call;
            fact.PreparedHead = predecessor?.Fact.PreparedHead;
            if (fact.Kind == "resource.dispatch")
            {
                if (attempt?.State != MutationAttemptState.Prepared) throw new InvalidOperationException("Only an exact prepared attempt may dispatch.");
                fact.Attempt = attempt.Transition(MutationAttemptState.DispatchMayHaveOccurred); return;
            }
            var publication = fact.Publication;
            if (AuthorityOperation(db, fact.Scope, publication.CommitId) != null) throw new ResourceAuthorityConflictException("Duplicate resource commit id.");
            if (attemptId != null && (attempt?.State != MutationAttemptState.DispatchMayHaveOccurred ||
                !attempt.Target.Equals(publication.Target))) throw new InvalidOperationException("Publication has no exact possibly-dispatched attempt.");
            var current = Head(db, fact.Scope, publication.Target);
            if (attempt == null && !SameHead(current, publication.ExpectedHead))
                throw new ResourceAuthorityConflictException("Resource publication expected head changed.");
            var revision = publication.Revision == null ? null : FindRevision(db, fact.Scope, publication.Revision.Reference);
            if (revision != null && !JToken.DeepEquals(JObject.FromObject(RevisionOf(revision.Fact)), JObject.FromObject(publication.Revision)))
                throw new ResourceAuthorityConflictException("Exact resource revision metadata is immutable.");
            var view = publication.View == null ? null : FindView(db, fact.Scope, publication.View.Reference, publication.View.View, publication.View.Coverage);
            if (view != null && !JToken.DeepEquals(JObject.FromObject(ViewOf(view.Fact)), JObject.FromObject(publication.View)))
                throw new ResourceAuthorityConflictException("Exact resource view metadata is immutable.");
            var generation = Generation(db, fact.Scope) + 1;
            // A late outcome is historical evidence. It must not replace a newer
            // head even when the earlier run was cancelled or lost its acknowledgement.
            var mayAdvance = attempt == null || SameHead(current, fact.PreparedHead) ||
                predecessor.Fact.Publication?.Unknown == true && predecessor.Fact.AdvancesHead && SameHead(current, predecessor.Fact.Head);
            fact.Generation = generation;
            fact.Head = mayAdvance ? publication.Unknown
                ? ResourceHeadState.Unknown(publication.Target, generation, "possible-dispatch")
                : ResourceHeadState.Known(publication.Revision.Reference, generation) : current;
            fact.AdvancesHead = mayAdvance;
            fact.Attempt = attempt == null ? null : publication.Unknown ? attempt :
                attempt.Transition(MutationAttemptState.Resolved, publication.CommitId);
        }

        private ResourceJournalReceipt FindAttempt(SqliteConnection db, ResourceAuthorityScopeId scope, string attempt)
        {
            using (var command = Command(db, "SELECT operation FROM resource_attempt WHERE scope=$scope AND attempt=$attempt", scope))
            {
                command.Parameters.AddWithValue("$attempt", attempt);
                var operation = command.ExecuteScalar() as string;
                if (operation == null) return null;
                var receipt = Find(db, scope, operation);
                if (receipt?.Fact.Attempt?.AttemptId != attempt || !receipt.Fact.Attempt.ScopeId.Equals(scope))
                    throw new InvalidDataException("Mutation attempt locator mismatch.");
                return receipt;
            }
        }

        private ResourceJournalReceipt FindRevision(SqliteConnection db, ResourceAuthorityScopeId scope, ResourceRef reference)
        {
            if (reference?.IsExact != true) throw new ArgumentException("An exact revision is required.");
            using (var command = Command(db, "SELECT operation FROM resource_revision WHERE scope=$scope AND uri=$uri AND revision=$revision", scope))
            {
                command.Parameters.AddWithValue("$uri", reference.Uri); command.Parameters.AddWithValue("$revision", reference.Revision);
                var operation = command.ExecuteScalar() as string;
                var result = operation == null ? null : Find(db, scope, operation);
                if (result != null && (RevisionOf(result.Fact)?.Reference.Uri != reference.Uri ||
                    RevisionOf(result.Fact).Reference.Revision != reference.Revision)) throw new InvalidDataException("Revision locator mismatch.");
                return result;
            }
        }

        private ResourceJournalReceipt FindView(SqliteConnection db, ResourceAuthorityScopeId scope, ResourceRef reference, string view, ResourceCoverage coverage)
        {
            if (reference?.IsExact != true || string.IsNullOrWhiteSpace(view) || coverage == null) throw new ArgumentException("Exact view coordinates are required.");
            using (var command = Command(db, "SELECT operation FROM resource_view WHERE scope=$scope AND uri=$uri AND revision=$revision AND view=$view AND coverage=$coverage", scope))
            {
                command.Parameters.AddWithValue("$uri", reference.Uri); command.Parameters.AddWithValue("$revision", reference.Revision);
                command.Parameters.AddWithValue("$view", view); command.Parameters.AddWithValue("$coverage", JsonConvert.SerializeObject(coverage));
                var operation = command.ExecuteScalar() as string;
                var result = operation == null ? null : Find(db, scope, operation);
                var retained = ViewOf(result?.Fact);
                if (result != null && (retained == null || retained.Reference.Uri != reference.Uri || retained.Reference.Revision != reference.Revision ||
                    retained.View != view || !JToken.DeepEquals(JObject.FromObject(retained.Coverage), JObject.FromObject(coverage))))
                    throw new InvalidDataException("Resource view locator mismatch.");
                return result;
            }
        }

        private ResourceHeadState Head(SqliteConnection db, ResourceAuthorityScopeId scope, ResourceIdentity identity)
        {
            using (var command = Command(db, "SELECT operation FROM resource_head WHERE scope=$scope AND uri=$uri", scope))
            {
                command.Parameters.AddWithValue("$uri", identity.Uri);
                var operation = command.ExecuteScalar() as string;
                if (operation == null) return null;
                var receipt = Find(db, scope, operation);
                if (HeadOf(receipt?.Fact, identity.Uri) == null)
                    throw new InvalidDataException("Resource head locator is inconsistent.");
                return HeadOf(receipt.Fact, identity.Uri);
            }
        }

        private static long Generation(SqliteConnection db, ResourceAuthorityScopeId scope)
        {
            using (var command = Command(db, "SELECT generation FROM resource_scope WHERE scope=$scope", scope))
            { var value = command.ExecuteScalar(); return value == null ? 0 : Convert.ToInt64(value); }
        }

        private ResourceJournalReceipt Find(SqliteConnection db, ResourceAuthorityScopeId scope, string operation)
        {
            using (var command = Command(db, "SELECT fingerprint,segment,start_offset,end_offset,sequence,hash,first_sequence FROM resource_fact WHERE scope=$scope AND operation=$operation", scope))
            {
                command.Parameters.AddWithValue("$operation", operation);
                string fingerprint; JournalCursor position;
                using (var reader = command.ExecuteReader())
                { if (!reader.Read()) return null; fingerprint = reader.GetString(0); position = JournalIndex.ReadCursor(reader, 1); }
                return Read(scope, operation, fingerprint, position);
            }
        }

        private ResourceJournalReceipt Read(ResourceAuthorityScopeId scope, string operation, string fingerprint, JournalCursor position)
        {
            var record = Journal.Read(position); var fact = Decode(record);
            if (!fact.Scope.Equals(scope) || record.Operation != operation || record.Fingerprint != fingerprint)
                throw new InvalidDataException("Resource receipt locator mismatch.");
            return new ResourceJournalReceipt(_paths.Root, operation, fingerprint, fact, position, true);
        }

        private void Apply(SqliteConnection db, string operation, string fingerprint, ResourceJournalFact fact, JournalCursor position)
        {
            ValidateTransition(db, fact);
            using (var tx = db.BeginTransaction())
            {
                using (var command = Command(db, "INSERT INTO resource_fact VALUES($scope,$operation,$fingerprint,$segment,$start,$end,$sequence,$hash,$first)", fact.Scope, tx))
                {
                    command.Parameters.AddWithValue("$operation", operation); command.Parameters.AddWithValue("$fingerprint", fingerprint);
                    JournalIndex.AddPosition(command, position); command.ExecuteNonQuery();
                }
                ApplyStoreRows(db, tx, operation, fact, position);
                JournalIndex.SaveCursor(db, "resources", position, tx); tx.Commit();
            }
        }

        private void ValidateTransition(SqliteConnection db, ResourceJournalFact fact)
        {
            var expected = JObject.FromObject(fact).ToObject<ResourceJournalFact>();
            PrepareTransition(db, expected);
            if (!JToken.DeepEquals(JObject.FromObject(expected), JObject.FromObject(fact)))
                throw new InvalidDataException("Resource transition does not match its canonical predecessor.");
        }

        private JournalCursor CatchUp(SqliteConnection db, HashSet<string> verified, CancellationToken token, bool recover = true, string verifiedCall = null)
        {
            var cursor = JournalIndex.LoadCursor(db, "resources"); var applied = cursor;
            var current = Journal.CatchUp(cursor, (record, position) => {
                Apply(db, record.Operation, record.Fingerprint, Decode(record), position); applied = position;
            }, record => {
                var fact = Decode(record); ValidateTransition(db, fact);
                if (fact.Kind == "resource.prepared" && fact.Call != null && CallKey(fact.Call) != verifiedCall)
                    throw new CallProofNeeded(fact.Call);
                foreach (var reference in References(fact))
                    if (!verified.Contains(ReferenceKey(reference))) throw new ReferenceNeeded(reference);
            }, token, recover);
            if (applied == null || applied.Hash != current.Hash || applied.End != current.End || applied.Segment != current.Segment)
                using (var tx = db.BeginTransaction()) { JournalIndex.SaveCursor(db, "resources", current, tx); tx.Commit(); }
            return current;
        }

        internal void ReplayInto(string path, CancellationToken token, bool recover = true)
        {
            var verified = new HashSet<string>(StringComparer.Ordinal); string verifiedCall = null;
            while (true)
            {
                PayloadRef required = null; ResourceCallLink call = null;
                using (Journal.AcquireAppendLock(token))
                using (var db = JournalIndex.Open(_paths, path))
                { try { CatchUp(db, verified, token, recover, verifiedCall); return; }
                    catch (ReferenceNeeded error) { required = error.Reference; } catch (CallProofNeeded error) { call = error.Call; } }
                if (call != null) { VerifyCall(call); verifiedCall = CallKey(call); }
                else { Verify(required); verified.Add(ReferenceKey(required)); }
            }
        }

        private T Access<T>(Func<SqliteConnection, JournalCursor, T> action, CancellationToken token)
        {
            using (RuntimeStorageActivity.Acquire(_paths, token))
            {
                var verified = new HashSet<string>(StringComparer.Ordinal); var repaired = false; string verifiedCall = null;
                while (true)
                {
                    PayloadRef required = null; ResourceCallLink call = null;
                    try
                    {
                        using (JournalIndexAdmission.Enter(_paths, token))
                        using (Journal.AcquireAppendLock(token))
                        using (var db = JournalIndex.Open(_paths))
                        { try { return action(db, CatchUp(db, verified, token, verifiedCall: verifiedCall)); }
                            catch (ReferenceNeeded error) { required = error.Reference; } catch (CallProofNeeded error) { call = error.Call; } }
                    }
                    catch (SqliteException error) when (!repaired && JournalIndex.IsDamaged(error))
                    { repaired = true; JournalIndex.Repair(_paths, _protector, false, token); continue; }
                    catch (JournalIndex.IndexVersionMismatch) when (!repaired)
                    { repaired = true; JournalIndex.Repair(_paths, _protector, false, token); continue; }
                    if (call != null) { VerifyCall(call); verifiedCall = CallKey(call); }
                    else { Verify(required); verified.Add(ReferenceKey(required)); }
                }
            }
        }

        private static ResourceJournalFact Decode(JournalRecord record)
        {
            var fact = record.Data.ToObject<ResourceJournalFact>(JsonSerializer.Create(new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error }));
            ValidateShape(fact);
            if (record.Kind != fact.Kind || record.Fingerprint != Fingerprint(fact)) throw new InvalidDataException("Resource fact fingerprint mismatch.");
            return fact;
        }

        private static void ValidateShape(ResourceJournalFact fact)
        {
            if (fact?.Call != null && (fact.Attempt == null || fact.Attempt.AttemptId != ConversationResourceBinding.ForCall(
                fact.Call.Origin.ConversationScope, fact.Call.Origin.ChatId, fact.Call.Origin.RunId, fact.Call.Origin.CallId,
                fact.Scope, fact.Attempt.Target).AttemptId)) throw new InvalidDataException("Foreign resource call binding.");
            if (ValidateStoreShape(fact)) return;
            if (fact?.Scope == null || fact.Kind != "resource.prepared" && fact.Kind != "resource.dispatch" && fact.Kind != "resource.published")
                throw new InvalidDataException("Unknown resource fact.");
            if (fact.Kind == "resource.prepared" && (fact.Attempt?.State != MutationAttemptState.Prepared || !fact.Attempt.ScopeId.Equals(fact.Scope)) ||
                fact.Kind == "resource.dispatch" && string.IsNullOrWhiteSpace(fact.AttemptId) ||
                fact.Kind == "resource.published" && (fact.Publication == null || !fact.Publication.Scope.Equals(fact.Scope)))
                throw new InvalidDataException("Incomplete resource command identity.");
            if (fact.Kind != "resource.published" && (fact.Publication != null || fact.Generation != 0 || fact.AdvancesHead || fact.Head != null) ||
                fact.Kind != "resource.dispatch" && fact.AttemptId != null)
                throw new InvalidDataException("Resource fact contains fields of a different transition.");
        }

        private static string Fingerprint(ResourceJournalFact fact)
        {
            if (IsStoreFact(fact)) return StoreFingerprint(fact);
            object value = fact.Kind == "resource.prepared" ? (object)fact.Attempt : fact.Kind == "resource.published" ? (object)fact.Publication :
                new { fact.Scope, fact.AttemptId };
            if (fact.Kind == "resource.prepared" && fact.Call != null) value = new { Command = value, fact.Call };
            return RNAssistant.Core.Tools.TextPatternEngine.Sha256(fact.Kind + "\n" + JsonConvert.SerializeObject(value));
        }
        internal static IEnumerable<PayloadRef> CanonicalReferences(JournalRecord record)
        { return References(Decode(record)); }

        private static IEnumerable<PayloadRef> References(ResourceJournalFact fact)
        { return new[] { fact.Attempt?.Payload, RevisionOf(fact)?.Payload, ViewOf(fact)?.Payload }
            .Concat(ViewOf(fact)?.Parts ?? new PayloadRef[0]).Where(reference => reference != null).GroupBy(ReferenceKey).Select(group => group.First()); }
        private void Verify(PayloadRef reference)
        { if (!_bodies.HasVerifiedReference(reference.ToBlobReference())) throw new InvalidDataException("Resource body is unavailable before acceptance."); }
        private void VerifyCall(ResourceCallLink call)
        { new ConversationJournal(_paths, call.Origin.ConversationScope, _protector).VerifyMutation(call); }
        private static string CallKey(ResourceCallLink call) { return JsonConvert.SerializeObject(call); }
        private sealed class CallProofNeeded : Exception
        { internal readonly ResourceCallLink Call; internal CallProofNeeded(ResourceCallLink call) { Call = call; } }
        private static string ReferenceKey(PayloadRef reference) { return reference.Sha256 + ":" + reference.ByteLength + ":" + reference.ProtectionKeyId; }
        private static bool SameHead(ResourceHeadState left, ResourceHeadState right)
        { return left == null ? right == null : right != null && left.SameAuthority(right); }
        private static SqliteCommand Command(SqliteConnection db, string sql, ResourceAuthorityScopeId scope, SqliteTransaction tx = null)
        { var command = db.CreateCommand(); command.CommandText = sql; command.Transaction = tx; command.Parameters.AddWithValue("$scope", scope.ToString()); return command; }
        private sealed class ReferenceNeeded : Exception
        { internal readonly PayloadRef Reference; internal ReferenceNeeded(PayloadRef reference) { Reference = reference; } }
    }

    internal sealed class ResourceJournalFact
    {
        public string Kind { get; set; }
        public ResourceAuthorityScopeId Scope { get; set; }
        public string AttemptId { get; set; }
        public MutationAttempt Attempt { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public ResourceCallLink Call { get; set; }
        public ResourcePublication Publication { get; set; }
        public long Generation { get; set; }
        public bool AdvancesHead { get; set; }
        public ResourceHeadState Head { get; set; }
        public ResourceHeadState PreparedHead { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public ResourceAuthorityCommit Authority { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public ResourceRevisionMetadata Revision { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public ResourceRevisionView View { get; set; }
    }

    internal sealed class ResourcePublication
    {
        public ResourceAuthorityScopeId Scope { get; private set; }
        public ResourceIdentity Target { get; private set; }
        public ResourceHeadState ExpectedHead { get; private set; }
        public ResourceRevisionMetadata Revision { get; private set; }
        public ResourceRevisionView View { get; private set; }
        public ResourceEffect Effect { get; private set; }
        public string AttemptId { get; private set; }
        public string CommitId { get; private set; }
        [JsonIgnore] public bool Unknown { get { return Effect?.Outcome == ResourceEffectOutcome.UnknownAfterDispatch; } }
        [JsonConstructor]
        internal ResourcePublication(ResourceAuthorityScopeId scope, ResourceIdentity target, ResourceHeadState expectedHead,
            ResourceRevisionMetadata revision, ResourceRevisionView view, ResourceEffect effect, string attemptId, string commitId)
        {
            if (scope == null || target == null || string.IsNullOrWhiteSpace(commitId) ||
                (revision == null && effect?.Outcome != ResourceEffectOutcome.UnknownAfterDispatch) ||
                revision != null && revision.Reference.Uri != target.Uri || view != null && (view.Reference.Uri != target.Uri || view.Reference.Revision != revision?.Reference.Revision) ||
                expectedHead != null && !expectedHead.Identity.Equals(target) || attemptId != null && (string.IsNullOrWhiteSpace(attemptId) || effect == null) ||
                effect?.Outcome == ResourceEffectOutcome.UnknownAfterDispatch && attemptId == null ||
                effect != null && (effect.Targets?.Count > 0 || effect.Impacts.Any(impact => impact == null || !impact.Identity.Equals(target) ||
                    impact.Before != null && (!impact.Before.IsExact || impact.Before.Uri != target.Uri) ||
                    impact.After != null && (!impact.After.IsExact || impact.After.Uri != target.Uri ||
                        revision == null || impact.After.Revision != revision.Reference.Revision))))
                throw new ArgumentException("Invalid resource publication contract.");
            Scope = scope; Target = target; ExpectedHead = expectedHead; Revision = revision; View = view;
            Effect = effect; AttemptId = attemptId; CommitId = commitId;
        }
    }

    internal sealed class ResourceJournalReceipt
    {
        internal readonly string Root, Operation, Fingerprint;
        internal readonly ResourceJournalFact Fact;
        internal readonly JournalCursor Position;
        internal bool ProjectionAvailable;
        internal bool Duplicate;
        internal string CommitId { get { return Fact.Authority?.CommitId ?? Fact.Publication?.CommitId; } }
        internal ResourceEffect Effect { get { return Fact.Authority?.Effect ?? Fact.Publication?.Effect; } }
        internal bool Unknown { get { return Effect?.Outcome == ResourceEffectOutcome.UnknownAfterDispatch; } }
        internal ResourceJournalReceipt(string root, string operation, string fingerprint, ResourceJournalFact fact, JournalCursor position, bool projectionAvailable)
        { Root = root; Operation = operation; Fingerprint = fingerprint; Fact = fact; Position = position; ProjectionAvailable = projectionAvailable; }
    }
}
