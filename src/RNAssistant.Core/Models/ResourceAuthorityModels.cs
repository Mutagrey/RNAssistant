using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RNAssistant.Core.Models
{
    public sealed class DocumentAuthorityId : IEquatable<DocumentAuthorityId>
    {
        [JsonProperty("id", Required = Required.Always)]
        public string Id { get; private set; }

        [JsonConstructor]
        public DocumentAuthorityId(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A document authority id is required.", nameof(id));
            Id = id.Trim();
        }

        public static DocumentAuthorityId Create() { return new DocumentAuthorityId("doc_" + Guid.NewGuid().ToString("N")); }
        public bool Equals(DocumentAuthorityId other) { return other != null && string.Equals(Id, other.Id, StringComparison.Ordinal); }
        public override bool Equals(object obj) { return Equals(obj as DocumentAuthorityId); }
        public override int GetHashCode() { return StringComparer.Ordinal.GetHashCode(Id); }
        public override string ToString() { return Id; }
    }

    public sealed class ResourceAuthorityScopeId : IEquatable<ResourceAuthorityScopeId>
    {
        [JsonProperty("kind", Required = Required.Always)]
        public string Kind { get; private set; }
        [JsonProperty("id", Required = Required.Always)]
        public string Id { get; private set; }

        [JsonConstructor]
        public ResourceAuthorityScopeId(string kind, string id)
        {
            if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Authority scope kind and id are required.");
            Kind = kind.Trim().ToLowerInvariant();
            Id = id.Trim();
        }

        public static ResourceAuthorityScopeId Document(DocumentAuthorityId id)
        {
            return new ResourceAuthorityScopeId("document", (id ?? throw new ArgumentNullException(nameof(id))).Id);
        }

        public bool Equals(ResourceAuthorityScopeId other)
        {
            return other != null && string.Equals(Kind, other.Kind, StringComparison.Ordinal) &&
                string.Equals(Id, other.Id, StringComparison.Ordinal);
        }

        public override bool Equals(object obj) { return Equals(obj as ResourceAuthorityScopeId); }
        public override int GetHashCode()
        {
            unchecked { return StringComparer.Ordinal.GetHashCode(Kind) * 397 ^ StringComparer.Ordinal.GetHashCode(Id); }
        }
        public override string ToString() { return Kind + ":" + Id; }
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum HeadKnowledge { Known, Unknown, Unavailable }

    public sealed class ResourceHeadState
    {
        [JsonProperty("identity", Required = Required.Always)]
        public ResourceIdentity Identity { get; private set; }
        [JsonProperty("knowledge", Required = Required.Always)]
        public HeadKnowledge Knowledge { get; private set; }
        [JsonProperty("revision", NullValueHandling = NullValueHandling.Ignore)]
        public ResourceRef Revision { get; private set; }
        [JsonProperty("cause", NullValueHandling = NullValueHandling.Ignore)]
        public string Cause { get; private set; }
        [JsonProperty("authorityGeneration")]
        public long AuthorityGeneration { get; private set; }

        [JsonConstructor]
        public ResourceHeadState(ResourceIdentity identity, HeadKnowledge knowledge,
            ResourceRef revision, string cause, long authorityGeneration)
        {
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            if (!Enum.IsDefined(typeof(HeadKnowledge), knowledge)) throw new ArgumentOutOfRangeException(nameof(knowledge));
            if (authorityGeneration < 0) throw new ArgumentOutOfRangeException(nameof(authorityGeneration));
            if (knowledge == HeadKnowledge.Known && (revision == null || !revision.IsExact ||
                !identity.Equals(revision.Identity)))
                throw new ArgumentException("A known head requires an exact revision of the same logical resource.", nameof(revision));
            if (knowledge != HeadKnowledge.Known && revision != null)
                throw new ArgumentException("Unknown/unavailable heads cannot retain a current revision.", nameof(revision));
            Identity = new ResourceIdentity(identity.Uri);
            Knowledge = knowledge;
            Revision = revision == null ? null : revision.Copy();
            Cause = string.IsNullOrWhiteSpace(cause) ? null : cause.Trim();
            AuthorityGeneration = authorityGeneration;
        }

        public static ResourceHeadState Known(ResourceRef revision, long generation, string cause = null)
        {
            if (revision == null) throw new ArgumentNullException(nameof(revision));
            return new ResourceHeadState(revision.Identity, HeadKnowledge.Known, revision, cause, generation);
        }

        public static ResourceHeadState Unknown(ResourceIdentity identity, long generation, string cause)
        {
            return new ResourceHeadState(identity, HeadKnowledge.Unknown, null, cause, generation);
        }

        public static ResourceHeadState Unavailable(ResourceIdentity identity, long generation, string cause)
        {
            return new ResourceHeadState(identity, HeadKnowledge.Unavailable, null, cause, generation);
        }

        public ResourceHeadState AtGeneration(long generation)
        {
            return new ResourceHeadState(Identity, Knowledge, Revision, Cause, generation);
        }

        public bool SameAuthority(ResourceHeadState other)
        {
            return other != null && Identity.Equals(other.Identity) && Knowledge == other.Knowledge &&
                string.Equals(Cause ?? string.Empty, other.Cause ?? string.Empty, StringComparison.Ordinal) &&
                string.Equals(Revision == null ? string.Empty : Revision.Uri,
                    other.Revision == null ? string.Empty : other.Revision.Uri, StringComparison.Ordinal) &&
                string.Equals(Revision == null ? string.Empty : Revision.Revision,
                    other.Revision == null ? string.Empty : other.Revision.Revision, StringComparison.Ordinal);
        }
    }

    public sealed class ResourceHeadChange
    {
        [JsonProperty("identity", Required = Required.Always)]
        public ResourceIdentity Identity { get; private set; }
        [JsonProperty("before", NullValueHandling = NullValueHandling.Ignore)]
        public ResourceHeadState Before { get; private set; }
        [JsonProperty("after", Required = Required.Always)]
        public ResourceHeadState After { get; private set; }

        [JsonConstructor]
        public ResourceHeadChange(ResourceIdentity identity, ResourceHeadState before, ResourceHeadState after)
        {
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            if (after == null || !identity.Equals(after.Identity) || before != null && !identity.Equals(before.Identity))
                throw new ArgumentException("Head change identity is inconsistent.");
            Before = before;
            After = after;
        }
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum AuthorityCommitReason
    {
        InitialObservation,
        MutationEffect,
        Restore,
        ExternalDrift,
        Reconciliation,
        DerivedPublication,
        CatalogResourcePublication,
        MetadataTransition
    }

    public sealed class ResourceAuthorityCommit
    {
        [JsonProperty("commitId", Required = Required.Always)]
        public string CommitId { get; private set; }
        [JsonProperty("scopeId", Required = Required.Always)]
        public ResourceAuthorityScopeId ScopeId { get; private set; }
        [JsonProperty("previousGeneration")]
        public long PreviousGeneration { get; private set; }
        [JsonProperty("newGeneration")]
        public long NewGeneration { get; private set; }
        [JsonProperty("effect", NullValueHandling = NullValueHandling.Ignore)]
        public ResourceEffect Effect { get; private set; }
        [JsonProperty("headChanges")]
        public IReadOnlyList<ResourceHeadChange> HeadChanges { get; private set; }
        [JsonProperty("reason")]
        public AuthorityCommitReason Reason { get; private set; }
        [JsonProperty("mutationAttemptId", NullValueHandling = NullValueHandling.Ignore)]
        public string MutationAttemptId { get; private set; }
        [JsonProperty("recordedAt")]
        public DateTime RecordedAt { get; private set; }

        [JsonConstructor]
        public ResourceAuthorityCommit(string commitId, ResourceAuthorityScopeId scopeId,
            long previousGeneration, long newGeneration, ResourceEffect effect,
            IEnumerable<ResourceHeadChange> headChanges, AuthorityCommitReason reason,
            string mutationAttemptId = null, DateTime? recordedAt = null)
        {
            if (string.IsNullOrWhiteSpace(commitId)) throw new ArgumentException("An authority commit id is required.", nameof(commitId));
            if (scopeId == null) throw new ArgumentNullException(nameof(scopeId));
            if (previousGeneration < 0 || newGeneration != previousGeneration + 1)
                throw new ArgumentException("An authority commit must advance its scope generation exactly once.");
            if (!Enum.IsDefined(typeof(AuthorityCommitReason), reason)) throw new ArgumentOutOfRangeException(nameof(reason));
            CommitId = commitId.Trim();
            ScopeId = scopeId;
            PreviousGeneration = previousGeneration;
            NewGeneration = newGeneration;
            Effect = effect;
            HeadChanges = Array.AsReadOnly((headChanges ?? new ResourceHeadChange[0]).ToArray());
            Reason = reason;
            MutationAttemptId = string.IsNullOrWhiteSpace(mutationAttemptId) ? null : mutationAttemptId.Trim();
            RecordedAt = (recordedAt ?? DateTime.UtcNow).ToUniversalTime();
        }

        public static ResourceAuthorityCommit Create(ResourceAuthorityScopeId scopeId, long previousGeneration,
            ResourceEffect effect, IEnumerable<ResourceHeadChange> headChanges, AuthorityCommitReason reason,
            string mutationAttemptId = null)
        {
            return new ResourceAuthorityCommit("ac_" + Guid.NewGuid().ToString("N"), scopeId,
                previousGeneration, previousGeneration + 1, effect, headChanges, reason, mutationAttemptId);
        }
    }

    public sealed class ResourceAuthoritySnapshot
    {
        private readonly IReadOnlyDictionary<string, ResourceHeadState> _heads;
        public ResourceAuthorityScopeId ScopeId { get; private set; }
        public long Generation { get; private set; }
        public string HighWaterCommitId { get; private set; }
        public long EffectHighWaterMark { get; private set; }
        public IReadOnlyDictionary<string, ResourceHeadState> Heads { get { return _heads; } }
        public IReadOnlyList<ResourceAuthorityCommit> Commits { get; private set; }

        public ResourceAuthoritySnapshot(ResourceAuthorityScopeId scopeId, long generation,
            string highWaterCommitId, long effectHighWaterMark,
            IEnumerable<ResourceHeadState> heads, IEnumerable<ResourceAuthorityCommit> commits = null)
        {
            ScopeId = scopeId ?? throw new ArgumentNullException(nameof(scopeId));
            if (generation < 0 || effectHighWaterMark < 0) throw new ArgumentOutOfRangeException();
            Generation = generation;
            HighWaterCommitId = highWaterCommitId;
            EffectHighWaterMark = effectHighWaterMark;
            Commits = Array.AsReadOnly((commits ?? new ResourceAuthorityCommit[0]).ToArray());
            var values = (heads ?? new ResourceHeadState[0]).ToDictionary(
                item => item.Identity.Uri,
                item => item.AtGeneration(item.AuthorityGeneration),
                StringComparer.Ordinal);
            _heads = new ReadOnlyDictionary<string, ResourceHeadState>(values);
        }

        public ResourceHeadState GetHead(ResourceIdentity identity)
        {
            if (identity == null) return null;
            ResourceHeadState value;
            return _heads.TryGetValue(identity.Uri, out value) ? value : null;
        }
    }

    public sealed class ResourceAuthoritySnapshotSet
    {
        private readonly IReadOnlyDictionary<string, ResourceAuthoritySnapshot> _snapshots;
        public IReadOnlyDictionary<string, ResourceAuthoritySnapshot> Snapshots { get { return _snapshots; } }

        public ResourceAuthoritySnapshotSet(IEnumerable<ResourceAuthoritySnapshot> snapshots)
        {
            var values = (snapshots ?? new ResourceAuthoritySnapshot[0]).ToDictionary(
                item => item.ScopeId.ToString(), item => item, StringComparer.Ordinal);
            _snapshots = new ReadOnlyDictionary<string, ResourceAuthoritySnapshot>(values);
        }

        public ResourceAuthoritySnapshot Get(ResourceAuthorityScopeId scope)
        {
            if (scope == null) return null;
            ResourceAuthoritySnapshot value;
            return _snapshots.TryGetValue(scope.ToString(), out value) ? value : null;
        }
    }

    public sealed class AuthorityCommitResult
    {
        public bool Published { get; private set; }
        public bool Duplicate { get; private set; }
        public ResourceAuthoritySnapshot Snapshot { get; private set; }

        public AuthorityCommitResult(bool published, bool duplicate, ResourceAuthoritySnapshot snapshot)
        {
            Published = published;
            Duplicate = duplicate;
            Snapshot = snapshot;
        }
    }

    public sealed class ResourceAuthorityChangedEventArgs : EventArgs
    {
        public ResourceAuthorityScopeId ScopeId { get; private set; }
        public long Generation { get; private set; }
        public string CommitId { get; private set; }
        public IReadOnlyList<ResourceIdentity> AffectedResources { get; private set; }

        public ResourceAuthorityChangedEventArgs(ResourceAuthorityCommit commit)
        {
            if (commit == null) throw new ArgumentNullException(nameof(commit));
            ScopeId = commit.ScopeId;
            Generation = commit.NewGeneration;
            CommitId = commit.CommitId;
            AffectedResources = Array.AsReadOnly(commit.HeadChanges.Select(item => item.Identity).ToArray());
        }
    }

    public interface IResourceAuthorityStore
    {
        event EventHandler<ResourceAuthorityChangedEventArgs> Changed;
        ResourceAuthoritySnapshot Capture(ResourceAuthorityScopeId scope);
        ResourceAuthoritySnapshotSet CaptureMany(IReadOnlyList<ResourceAuthorityScopeId> scopes);
        ResourceHeadState GetHead(ResourceAuthorityScopeId scope, ResourceIdentity identity);
        AuthorityCommitResult Publish(ResourceAuthorityCommit commit);
    }

    public interface IResourceRevisionStore
    {
        void RegisterRevision(ResourceAuthorityScopeId scope, ResourceRevisionMetadata revision);
        ResourceRevisionMetadata GetRevision(ResourceAuthorityScopeId scope, ResourceRef reference);
        void RegisterView(ResourceAuthorityScopeId scope, ResourceRevisionView view);
        ResourceRevisionView GetView(ResourceAuthorityScopeId scope, ResourceRef reference, string view);
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum MutationAttemptState { Prepared, DispatchMayHaveOccurred, Resolved, AbandonedBeforeDispatch }

    public sealed class MutationAttempt
    {
        public string AttemptId { get; private set; }
        public ResourceAuthorityScopeId ScopeId { get; private set; }
        public string Operation { get; private set; }
        public ResourceIdentity Target { get; private set; }
        public IReadOnlyList<ResourceImpact> IntendedImpacts { get; private set; }
        public string ExpectedRevision { get; private set; }
        public PayloadRef Payload { get; private set; }
        public string IntendedSemanticHash { get; private set; }
        public MutationAttemptState State { get; private set; }
        public DateTime PreparedAt { get; private set; }
        public string LinkedAuthorityCommitId { get; private set; }

        [JsonConstructor]
        public MutationAttempt(string attemptId, ResourceAuthorityScopeId scopeId, string operation,
            ResourceIdentity target, string expectedRevision, PayloadRef payload,
            string intendedSemanticHash, MutationAttemptState state, DateTime preparedAt,
            string linkedAuthorityCommitId = null, IEnumerable<ResourceImpact> intendedImpacts = null)
        {
            if (string.IsNullOrWhiteSpace(attemptId) || scopeId == null ||
                string.IsNullOrWhiteSpace(operation) || target == null)
                throw new ArgumentException("A complete mutation attempt is required.");
            AttemptId = attemptId.Trim();
            ScopeId = scopeId;
            Operation = operation.Trim();
            Target = target;
            IntendedImpacts = Array.AsReadOnly((intendedImpacts ?? new[] {
                new ResourceImpact(target, ResourceImpactRelation.Exact) }).ToArray());
            ExpectedRevision = expectedRevision;
            Payload = payload;
            IntendedSemanticHash = intendedSemanticHash;
            State = state;
            PreparedAt = preparedAt.ToUniversalTime();
            LinkedAuthorityCommitId = linkedAuthorityCommitId;
        }

        public static MutationAttempt Prepare(ResourceAuthorityScopeId scope, string operation,
            ResourceIdentity target, string expectedRevision = null, PayloadRef payload = null,
            string semanticHash = null, IEnumerable<ResourceImpact> intendedImpacts = null)
        {
            return new MutationAttempt("ma_" + Guid.NewGuid().ToString("N"), scope, operation,
                target, expectedRevision, payload, semanticHash, MutationAttemptState.Prepared, DateTime.UtcNow,
                intendedImpacts: intendedImpacts);
        }

        public MutationAttempt Transition(MutationAttemptState state, string authorityCommitId = null)
        {
            if (State == MutationAttemptState.Resolved || State == MutationAttemptState.AbandonedBeforeDispatch)
                throw new InvalidOperationException("A terminal mutation attempt cannot transition.");
            if (State == MutationAttemptState.Prepared && state != MutationAttemptState.DispatchMayHaveOccurred &&
                state != MutationAttemptState.AbandonedBeforeDispatch)
                throw new InvalidOperationException("Prepared attempts must mark dispatch or be abandoned.");
            if (State == MutationAttemptState.DispatchMayHaveOccurred && state != MutationAttemptState.Resolved)
                throw new InvalidOperationException("A dispatched attempt can only resolve.");
            if (state == MutationAttemptState.Resolved && string.IsNullOrWhiteSpace(authorityCommitId))
                throw new ArgumentException("A resolved attempt requires its authority commit id.", nameof(authorityCommitId));
            return new MutationAttempt(AttemptId, ScopeId, Operation, Target, ExpectedRevision,
                Payload, IntendedSemanticHash, state, PreparedAt, authorityCommitId, IntendedImpacts);
        }
    }
}
