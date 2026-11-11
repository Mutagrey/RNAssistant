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
    // Existing resource ports use the same canonical stream and locator tables as
    // conversation outcome links. No import, dual write or legacy-file fallback.
    internal sealed partial class ResourceJournal : IResourceAuthorityStore, IResourceRevisionStore, IResourceMutationJournal
    {
        public event EventHandler<ResourceAuthorityChangedEventArgs> Changed;
        // The pre-existing in-process notification list moved from the retiring
        // authority backend. Both generations share this one advisory owner.
        private static readonly object ObserversSync = new object();
        private static readonly List<KeyValuePair<string, WeakReference<IResourceAuthorityStore>>> Observers =
            new List<KeyValuePair<string, WeakReference<IResourceAuthorityStore>>>();

        internal static void RegisterObserver(string root, IResourceAuthorityStore store)
        {
            lock (ObserversSync)
            {
                Observers.RemoveAll(item => { IResourceAuthorityStore target; return !item.Value.TryGetTarget(out target); });
                Observers.Add(new KeyValuePair<string, WeakReference<IResourceAuthorityStore>>(root, new WeakReference<IResourceAuthorityStore>(store)));
            }
        }

        internal static IResourceAuthorityStore[] AuthorityObservers(string root)
        {
            lock (ObserversSync)
                return Observers.Where(item => item.Key == root).Select(item => {
                    IResourceAuthorityStore target; return item.Value.TryGetTarget(out target) ? target : null;
                }).Where(item => item != null).ToArray();
        }

        internal static void NotifyAuthority(string root, ResourceAuthorityCommit commit)
        {
            foreach (var store in AuthorityObservers(root))
            {
                if (store is ResourceAuthorityStore legacy) legacy.NotifyChanged(commit);
                else if (store is ResourceJournal journal)
                {
                    var handler = journal.Changed;
                    if (handler == null) continue;
                    foreach (EventHandler<ResourceAuthorityChangedEventArgs> observer in handler.GetInvocationList())
                        try { observer(journal, new ResourceAuthorityChangedEventArgs(commit)); }
                        catch { /* Advisory notification follows the durable commit. */ }
                }
            }
        }

        public void RegisterRevision(ResourceAuthorityScopeId scope, ResourceRevisionMetadata revision)
        {
            if (scope == null || revision == null) throw new ArgumentNullException();
            Commit("revision:" + Key(revision.Reference.Uri + "\n" + revision.Reference.Revision),
                new ResourceJournalFact { Kind = "resource.revision", Scope = scope, Revision = revision }, CancellationToken.None);
        }

        public void RegisterView(ResourceAuthorityScopeId scope, ResourceRevisionView view)
        {
            if (scope == null || view == null) throw new ArgumentNullException();
            Commit("view:" + Key(view.Reference.Uri + "\n" + view.Reference.Revision + "\n" + view.View + "\n" + JsonConvert.SerializeObject(view.Coverage)),
                new ResourceJournalFact { Kind = "resource.view", Scope = scope, View = view }, CancellationToken.None);
        }

        public AuthorityCommitResult Publish(ResourceAuthorityCommit commit)
        {
            if (commit == null) throw new ArgumentNullException(nameof(commit));
            using (RuntimeStorageActivity.Acquire(_paths, CancellationToken.None))
            {
                // Validate bytes before entering the append/index section. Revisions
                // are immutable and registered before the authoritative head change.
                foreach (var change in commit.HeadChanges.Where(item => item.After.Knowledge == HeadKnowledge.Known))
                {
                    var revision = Revision(commit.ScopeId, change.After.Revision);
                    if (revision == null) throw new ResourceAuthorityConflictException("Known heads require registered revision metadata.");
                    if (revision.Payload != null) Verify(revision.Payload);
                }
                var operation = "authority:" + commit.CommitId;
                var receipt = Commit(operation, new ResourceJournalFact { Kind = "resource.authority", Scope = commit.ScopeId, Authority = commit }, CancellationToken.None);
                var duplicate = receipt.Duplicate;
                if (!duplicate) NotifyAuthority(_paths.ResourceAuthorityDirectory, commit);
                return new AuthorityCommitResult(!duplicate, duplicate, Capture(commit.ScopeId));
            }
        }

        public MutationAttempt Prepare(ResourceAuthorityScopeId scope, string operation, ResourceIdentity target,
            string expectedRevision = null, PayloadRef payload = null, string semanticHash = null,
            IEnumerable<ResourceImpact> intendedImpacts = null, ResourceMutationOrigin origin = null)
        {
            var attempt = MutationAttempt.Prepare(scope, operation, target, expectedRevision, payload, semanticHash, intendedImpacts);
            if (origin == null) return Prepare("prepare:" + attempt.AttemptId, attempt).Fact.Attempt;
            var conversation = new ConversationJournal(_paths, origin.ConversationScope, _protector);
            var proof = conversation.BindMutation(origin);
            var binding = ConversationResourceBinding.ForCall(origin.ConversationScope, origin.ChatId, origin.RunId, origin.CallId, scope, target);
            attempt = new MutationAttempt(binding.AttemptId, scope, operation, target, expectedRevision, payload,
                semanticHash, MutationAttemptState.Prepared, attempt.PreparedAt, intendedImpacts: intendedImpacts);
            // Re-entering this API is not a safe retry of an invocation. A lost
            // acknowledgement remains addressable by its deterministic attempt.
            if (Attempt(scope, attempt.AttemptId) != null) throw new InvalidOperationException("This call already prepared this resource attempt.");
            return Commit("prepare:" + attempt.AttemptId, new ResourceJournalFact {
                Kind = "resource.prepared", Scope = scope, Attempt = attempt, Call = proof }, CancellationToken.None).Fact.Attempt;
        }

        public MutationAttempt MarkDispatchMayHaveOccurred(string attemptId)
        {
            var current = RequireAttempt(attemptId);
            if (current.Fact.Attempt.State == MutationAttemptState.DispatchMayHaveOccurred) return current.Fact.Attempt;
            if (current.Fact.Attempt.State != MutationAttemptState.Prepared) throw new InvalidOperationException("A terminal attempt cannot dispatch again.");
            return MarkDispatch("dispatch:" + attemptId, current.Fact.Scope, attemptId).Fact.Attempt;
        }

        public MutationAttempt Resolve(string attemptId, string authorityCommitId)
        {
            var current = RequireAttempt(attemptId).Fact.Attempt;
            // Authority publication and resolution are one durable fact. A caller
            // cannot resolve an uncertain effect by merely naming a commit.
            if (current.State != MutationAttemptState.Resolved || current.LinkedAuthorityCommitId != authorityCommitId)
                throw new InvalidOperationException("The attempt has no matching committed authority outcome.");
            return current;
        }

        public MutationAttempt AbandonBeforeDispatch(string attemptId)
        {
            var current = RequireAttempt(attemptId);
            return Commit("abandon:" + attemptId, new ResourceJournalFact {
                Kind = "resource.abandoned", Scope = current.Fact.Scope, AttemptId = attemptId }, CancellationToken.None).Fact.Attempt;
        }

        public IDisposable AcquireScope(ResourceAuthorityScopeId scope, bool waitForOwner = false,
            CancellationToken token = default(CancellationToken))
        {
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            token.ThrowIfCancellationRequested();
            // Keep the existing domain lease identity. Only the canonical metadata
            // backend changes; the lease still spans guard, dispatch and read-back.
            StorageFileSystem.EnsureRegularDirectory(_paths.ResourceAuthorityDirectory);
            var path = Path.Combine(_paths.ResourceAuthorityDirectory, "mutation-" + Key(scope.ToString()) + ".lck");
            return waitForOwner ? StorageFileSystem.AcquireWriteLock(path, token) :
                new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        private static string Key(string value) { return RNAssistant.Core.Tools.TextPatternEngine.Sha256(value); }
        private static ResourceRevisionMetadata RevisionOf(ResourceJournalFact fact) { return fact?.Revision ?? fact?.Publication?.Revision; }
        private static ResourceRevisionView ViewOf(ResourceJournalFact fact) { return fact?.View ?? fact?.Publication?.View; }
        private static ResourceHeadState HeadOf(ResourceJournalFact fact, string uri)
        { return fact?.Authority?.HeadChanges.SingleOrDefault(item => item.Identity.Uri == uri)?.After ??
            (fact?.AdvancesHead == true && fact.Head?.Identity.Uri == uri ? fact.Head : null); }

        private static ResourceAuthorityCommit AuthorityOf(ResourceJournalFact fact)
        {
            if (fact?.Authority != null) return fact.Authority;
            var publication = fact?.Publication;
            if (publication == null) return null;
            return new ResourceAuthorityCommit(publication.CommitId, fact.Scope, fact.Generation - 1, fact.Generation,
                publication.Effect, fact.AdvancesHead ? new[] { new ResourceHeadChange(publication.Target,
                    publication.ExpectedHead, fact.Head) } : new ResourceHeadChange[0],
                publication.AttemptId == null ? AuthorityCommitReason.InitialObservation : AuthorityCommitReason.MutationEffect,
                publication.AttemptId, DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc));
        }

        private bool PrepareStoreTransition(SqliteConnection db, ResourceJournalFact fact)
        {
            if (!IsStoreFact(fact)) return false;
            if (fact.Kind == "resource.revision")
            {
                var existing = RevisionOf(FindRevision(db, fact.Scope, fact.Revision.Reference)?.Fact);
                if (existing != null && !Same(existing, fact.Revision)) throw new ResourceAuthorityConflictException("Immutable revision changed.");
            }
            else if (fact.Kind == "resource.view")
            {
                if (FindRevision(db, fact.Scope, fact.View.Reference) == null) throw new ResourceAuthorityConflictException("A view requires a registered revision.");
                var existing = ViewOf(FindView(db, fact.Scope, fact.View.Reference, fact.View.View, fact.View.Coverage)?.Fact);
                if (existing != null && !Same(existing, fact.View)) throw new ResourceAuthorityConflictException("Immutable view changed.");
            }
            else if (fact.Kind == "resource.abandoned")
            {
                var predecessor = FindAttempt(db, fact.Scope, fact.AttemptId)?.Fact;
                var previous = predecessor?.Attempt; fact.Call = predecessor?.Call;
                if (previous?.State != MutationAttemptState.Prepared) throw new InvalidOperationException("Only a prepared attempt may be abandoned.");
                fact.Attempt = previous.Transition(MutationAttemptState.AbandonedBeforeDispatch);
            }
            else
            {
                var commit = fact.Authority;
                if (Generation(db, fact.Scope) != commit.PreviousGeneration ||
                    commit.HeadChanges.Select(item => item.Identity.Uri).Distinct(StringComparer.Ordinal).Count() != commit.HeadChanges.Count ||
                    AuthorityOperation(db, fact.Scope, commit.CommitId) != null)
                    throw new ResourceAuthorityConflictException("Authority generation or commit identity changed.");
                foreach (var change in commit.HeadChanges)
                {
                    if (!SameHead(Head(db, fact.Scope, change.Identity), change.Before) || change.After.AuthorityGeneration != commit.NewGeneration ||
                        change.After.Knowledge == HeadKnowledge.Known && FindRevision(db, fact.Scope, change.After.Revision) == null)
                        throw new ResourceAuthorityConflictException("Expected head changed or exact revision is unavailable.");
                }
                if (commit.MutationAttemptId != null)
                {
                    var predecessor = FindAttempt(db, fact.Scope, commit.MutationAttemptId)?.Fact;
                    var previous = predecessor?.Attempt; fact.Call = predecessor?.Call;
                    if (previous?.State != MutationAttemptState.DispatchMayHaveOccurred || commit.Effect == null)
                        throw new InvalidOperationException("Authority outcome requires an exact possibly-dispatched attempt.");
                    fact.Attempt = previous.Transition(MutationAttemptState.Resolved, commit.CommitId);
                }
                fact.Generation = commit.NewGeneration;
            }
            return true;
        }

        private void ApplyStoreRows(SqliteConnection db, SqliteTransaction tx, string operation, ResourceJournalFact fact, JournalCursor position)
        {
            if (fact.Attempt != null)
                using (var command = Command(db, "INSERT INTO resource_attempt VALUES($scope,$attempt,$operation,$state,$ticks,$origin) ON CONFLICT(scope,attempt) DO UPDATE SET operation=$operation,state=$state", fact.Scope, tx))
                {
                    command.Parameters.AddWithValue("$attempt", fact.Attempt.AttemptId); command.Parameters.AddWithValue("$operation", operation);
                    command.Parameters.AddWithValue("$origin", fact.Call == null ? (object)DBNull.Value : JsonConvert.SerializeObject(fact.Call.Origin));
                    command.Parameters.AddWithValue("$state", (int)fact.Attempt.State); command.Parameters.AddWithValue("$ticks", fact.Attempt.PreparedAt.Ticks); command.ExecuteNonQuery();
                }
            var authority = AuthorityOf(fact);
            if (authority != null)
            {
                using (var command = Command(db, "INSERT INTO resource_scope VALUES($scope,$generation,$operation,$effects) ON CONFLICT(scope) DO UPDATE SET generation=$generation,operation=$operation,effects=effects+$effects", fact.Scope, tx))
                {
                    command.Parameters.AddWithValue("$generation", fact.Generation); command.Parameters.AddWithValue("$operation", operation);
                    command.Parameters.AddWithValue("$effects", authority.Effect == null ? 0 : 1); command.ExecuteNonQuery();
                }
                using (var command = Command(db, "INSERT INTO resource_commit VALUES($scope,$commit,$attempt,$operation,$generation)", fact.Scope, tx))
                {
                    command.Parameters.AddWithValue("$commit", authority.CommitId); command.Parameters.AddWithValue("$operation", operation);
                    command.Parameters.AddWithValue("$attempt", (object)authority.MutationAttemptId ?? DBNull.Value);
                    command.Parameters.AddWithValue("$generation", fact.Generation); command.ExecuteNonQuery();
                }
                foreach (var change in authority.HeadChanges)
                    using (var command = Command(db, "INSERT INTO resource_head VALUES($scope,$uri,$operation) ON CONFLICT(scope,uri) DO UPDATE SET operation=$operation", fact.Scope, tx))
                    { command.Parameters.AddWithValue("$uri", change.Identity.Uri); command.Parameters.AddWithValue("$operation", operation); command.ExecuteNonQuery(); }
            }
            var revision = RevisionOf(fact);
            if (revision != null)
                using (var command = Command(db, "INSERT OR IGNORE INTO resource_revision VALUES($scope,$uri,$revision,$operation)", fact.Scope, tx))
                {
                    command.Parameters.AddWithValue("$uri", revision.Reference.Uri); command.Parameters.AddWithValue("$revision", revision.Reference.Revision);
                    command.Parameters.AddWithValue("$operation", operation); command.ExecuteNonQuery();
                }
            var view = ViewOf(fact);
            if (view != null)
                using (var command = Command(db, "INSERT OR IGNORE INTO resource_view VALUES($scope,$uri,$revision,$view,$coverage,$operation,$sequence,$whole)", fact.Scope, tx))
                {
                    command.Parameters.AddWithValue("$uri", view.Reference.Uri); command.Parameters.AddWithValue("$revision", view.Reference.Revision);
                    command.Parameters.AddWithValue("$view", view.View); command.Parameters.AddWithValue("$coverage", JsonConvert.SerializeObject(view.Coverage));
                    command.Parameters.AddWithValue("$operation", operation); command.Parameters.AddWithValue("$sequence", position.Sequence);
                    command.Parameters.AddWithValue("$whole", view.Coverage.Kind == ResourceCoverageKinds.Whole ? 1 : 0); command.ExecuteNonQuery();
                }
        }

        private static bool Same(object left, object right) { return JToken.DeepEquals(JToken.FromObject(left), JToken.FromObject(right)); }
        private static bool IsStoreFact(ResourceJournalFact fact)
        { return fact?.Kind == "resource.authority" || fact?.Kind == "resource.revision" || fact?.Kind == "resource.view" || fact?.Kind == "resource.abandoned"; }
        private static string StoreFingerprint(ResourceJournalFact fact)
        { return Key(fact.Kind + "\n" + JsonConvert.SerializeObject((object)fact.Authority ?? fact.Revision ?? (object)fact.View ?? new { fact.Scope, fact.AttemptId })); }
        private static bool ValidateStoreShape(ResourceJournalFact fact)
        {
            if (!IsStoreFact(fact))
            {
                if (fact?.Authority != null || fact?.Revision != null || fact?.View != null) throw new InvalidDataException("Foreign resource command fields.");
                return false;
            }
            if (fact.Scope == null || fact.Publication != null || fact.AdvancesHead || fact.Head != null || fact.PreparedHead != null ||
                (fact.Kind == "resource.authority") != (fact.Authority != null) ||
                (fact.Kind == "resource.revision") != (fact.Revision != null) ||
                (fact.Kind == "resource.view") != (fact.View != null) ||
                (fact.Kind == "resource.abandoned") != (fact.AttemptId != null) ||
                fact.Authority != null && !fact.Authority.ScopeId.Equals(fact.Scope) ||
                fact.Kind != "resource.authority" && fact.Generation != 0 ||
                (fact.Kind == "resource.revision" || fact.Kind == "resource.view") && fact.Attempt != null ||
                fact.Attempt != null && (!fact.Attempt.ScopeId.Equals(fact.Scope) ||
                    fact.Kind == "resource.abandoned" && (fact.Attempt.AttemptId != fact.AttemptId || fact.Attempt.State != MutationAttemptState.AbandonedBeforeDispatch) ||
                    fact.Kind == "resource.authority" && (fact.Attempt.AttemptId != fact.Authority.MutationAttemptId ||
                        fact.Attempt.State != MutationAttemptState.Resolved || fact.Attempt.LinkedAuthorityCommitId != fact.Authority.CommitId)))
                throw new InvalidDataException("Invalid resource store fact.");
            return true;
        }
    }
}
