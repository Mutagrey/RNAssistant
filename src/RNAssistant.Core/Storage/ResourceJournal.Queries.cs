using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ResourceJournal
    {
        public ResourceHeadState GetHead(ResourceAuthorityScopeId scope, ResourceIdentity identity)
        { return scope == null || identity == null ? null : Head(scope, identity); }
        public ResourceRevisionMetadata GetRevision(ResourceAuthorityScopeId scope, ResourceRef reference)
        { return scope == null || reference?.IsExact != true ? null : Revision(scope, reference); }

        public ResourceAuthoritySnapshot Capture(ResourceAuthorityScopeId scope)
        {
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            return Access((db, cursor) => Capture(db, scope), CancellationToken.None);
        }

        public ResourceAuthoritySnapshotSet CaptureMany(IReadOnlyList<ResourceAuthorityScopeId> scopes)
        {
            return Access((db, cursor) => new ResourceAuthoritySnapshotSet((scopes ?? new ResourceAuthorityScopeId[0])
                .Where(scope => scope != null).Distinct().OrderBy(scope => scope.ToString(), StringComparer.Ordinal)
                .Select(scope => Capture(db, scope))), CancellationToken.None);
        }

        // Heads are frozen now. Optional history is pinned to this generation and
        // loaded only when requested; mutation recovery has an exact index lookup.
        private ResourceAuthoritySnapshot Capture(SqliteConnection db, ResourceAuthorityScopeId scope)
        {
            var heads = new List<ResourceHeadState>();
            var locations = new List<KeyValuePair<string, string>>();
            using (var command = Command(db, "SELECT uri,operation FROM resource_head WHERE scope=$scope ORDER BY uri", scope))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) locations.Add(new KeyValuePair<string, string>(reader.GetString(0), reader.GetString(1)));
            foreach (var item in locations) heads.Add(HeadOf(Find(db, scope, item.Value)?.Fact, item.Key) ??
                throw new InvalidDataException("Resource head locator is unavailable."));
            long generation = 0, effects = 0; string operation = null;
            using (var command = Command(db, "SELECT generation,operation,effects FROM resource_scope WHERE scope=$scope", scope))
            using (var reader = command.ExecuteReader())
                if (reader.Read()) { generation = reader.GetInt64(0); operation = reader.GetString(1); effects = reader.GetInt64(2); }
            var highWater = operation == null ? null : Find(db, scope, operation);
            var hash = highWater?.Position.Hash;
            return new ResourceAuthoritySnapshot(scope, generation, AuthorityOf(highWater?.Fact)?.CommitId, effects, heads,
                () => ReadHistory(scope, generation, hash), attempt => ReadMutationCommit(scope, generation, hash, attempt));
        }

        private IReadOnlyList<ResourceAuthorityCommit> ReadHistory(ResourceAuthorityScopeId scope, long generation, string hash)
        {
            return Access((db, cursor) => {
                RequireGeneration(db, scope, generation, hash);
                var operations = new List<string>();
                using (var command = Command(db, "SELECT operation FROM resource_commit WHERE scope=$scope AND generation<=$generation ORDER BY generation", scope))
                {
                    command.Parameters.AddWithValue("$generation", generation);
                    using (var reader = command.ExecuteReader()) while (reader.Read()) operations.Add(reader.GetString(0));
                }
                return (IReadOnlyList<ResourceAuthorityCommit>)operations.Select(operation => AuthorityOf(Find(db, scope, operation)?.Fact) ??
                    throw new InvalidDataException("Authority locator is unavailable.")).ToArray();
            }, CancellationToken.None);
        }

        private ResourceAuthorityCommit ReadMutationCommit(ResourceAuthorityScopeId scope, long generation, string hash, string attempt)
        {
            return Access((db, cursor) => {
                RequireGeneration(db, scope, generation, hash);
                using (var command = Command(db, "SELECT operation FROM resource_commit WHERE scope=$scope AND attempt=$attempt AND generation<=$generation ORDER BY generation DESC LIMIT 1", scope))
                {
                    command.Parameters.AddWithValue("$attempt", attempt); command.Parameters.AddWithValue("$generation", generation);
                    var operation = command.ExecuteScalar() as string;
                    if (operation == null) return null;
                    var commit = AuthorityOf(Find(db, scope, operation)?.Fact);
                    if (commit?.MutationAttemptId != attempt || commit.NewGeneration > generation)
                        throw new InvalidDataException("Mutation commit locator mismatch.");
                    return commit;
                }
            }, CancellationToken.None);
        }

        private static void RequireGeneration(SqliteConnection db, ResourceAuthorityScopeId scope, long generation, string hash)
        {
            if (generation == 0) return;
            using (var command = Command(db, "SELECT f.hash FROM resource_commit c JOIN resource_fact f ON f.scope=c.scope AND f.operation=c.operation WHERE c.scope=$scope AND c.generation=$generation", scope))
            {
                command.Parameters.AddWithValue("$generation", generation);
                if (hash == null || command.ExecuteScalar() as string != hash)
                    throw new ResourceAuthorityConflictException("The captured authority generation is unavailable.");
            }
        }

        public ResourceHeadPage ReadHeads(ResourceAuthorityScopeId scope, IReadOnlyList<ResourceHeadRange> ranges, int offset, int limit)
        {
            if (scope == null || ranges == null || ranges.Count == 0 || ranges.Count > 16 || ranges.Any(range => range == null) ||
                offset < 0 || limit < 0 || limit > 100) throw new ArgumentException("A bounded head page is required.");
            var spans = ranges.OrderBy(range => range.Start, StringComparer.Ordinal).ToArray();
            for (var i = 1; i < spans.Length; i++)
                if (string.CompareOrdinal(spans[i - 1].End, spans[i].Start) > 0) throw new ArgumentException("Head ranges overlap.");
            return Access((db, cursor) => {
                var predicate = string.Join(" OR ", spans.Select((range, i) => "(uri >= $start" + i + " AND uri < $end" + i + ")"));
                using (var command = Command(db, "SELECT COUNT(*) FROM resource_head WHERE scope=$scope AND (" + predicate + ")", scope))
                {
                    for (var i = 0; i < spans.Length; i++)
                    { command.Parameters.AddWithValue("$start" + i, spans[i].Start); command.Parameters.AddWithValue("$end" + i, spans[i].End); }
                    var total = Convert.ToInt32(command.ExecuteScalar());
                    if (offset > total) throw new ArgumentException("Head page exceeds its collection.");
                    command.CommandText = "SELECT uri,operation FROM resource_head WHERE scope=$scope AND (" + predicate + ") ORDER BY uri LIMIT $limit OFFSET $offset";
                    command.Parameters.AddWithValue("$limit", limit); command.Parameters.AddWithValue("$offset", offset);
                    var locations = new List<KeyValuePair<string, string>>();
                    using (var reader = command.ExecuteReader())
                        while (reader.Read()) locations.Add(new KeyValuePair<string, string>(reader.GetString(0), reader.GetString(1)));
                    var heads = locations.Select(item => HeadOf(Find(db, scope, item.Value)?.Fact, item.Key) ??
                        throw new InvalidDataException("Head locator is unavailable.")).ToArray();
                    var next = offset + heads.Length;
                    return new ResourceHeadPage(heads, Generation(db, scope), total, next < total ? (int?)next : null);
                }
            }, CancellationToken.None);
        }

        public ResourceRevisionView GetView(ResourceAuthorityScopeId scope, ResourceRef reference, string view)
        {
            if (scope == null || reference?.IsExact != true || string.IsNullOrWhiteSpace(view)) throw new ArgumentException("Exact view coordinates required.");
            return Access((db, cursor) => {
                using (var command = ViewCommand(db, "SELECT operation FROM resource_view WHERE scope=$scope AND uri=$uri AND revision=$revision AND view=$view ORDER BY whole DESC,sequence LIMIT 1", scope, reference))
                {
                    command.Parameters.AddWithValue("$view", view);
                    var operation = command.ExecuteScalar() as string;
                    if (operation == null) return null;
                    var result = ViewOf(Find(db, scope, operation)?.Fact);
                    if (result?.Reference.Uri != reference.Uri || result.Reference.Revision != reference.Revision || result.View != view)
                        throw new InvalidDataException("View locator mismatch.");
                    return result;
                }
            }, CancellationToken.None);
        }

        public ResourceRevisionViewPage ReadViews(ResourceAuthorityScopeId scope, ResourceRef reference, int offset, int limit, int? snapshotCount = null)
        {
            if (scope == null || reference?.IsExact != true || offset < 0 || limit < 1 || limit > 50 || snapshotCount < 0)
                throw new ArgumentException("An exact revision and bounded view page are required.");
            return Access((db, cursor) => {
                using (var command = ViewCommand(db, "SELECT COUNT(*) FROM resource_view WHERE scope=$scope AND uri=$uri AND revision=$revision", scope, reference))
                {
                    var total = Convert.ToInt32(command.ExecuteScalar()); var count = snapshotCount ?? total;
                    if (count > total || offset > count) throw new ResourceAuthorityConflictException("View inventory snapshot is unavailable.");
                    command.CommandText = "SELECT operation FROM resource_view WHERE scope=$scope AND uri=$uri AND revision=$revision ORDER BY sequence LIMIT $limit OFFSET $offset";
                    command.Parameters.AddWithValue("$limit", Math.Min(limit, count - offset)); command.Parameters.AddWithValue("$offset", offset);
                    var operations = new List<string>();
                    using (var reader = command.ExecuteReader()) while (reader.Read()) operations.Add(reader.GetString(0));
                    var views = operations.Select(operation => ViewOf(Find(db, scope, operation)?.Fact) ?? throw new InvalidDataException("View locator is unavailable.")).ToArray();
                    var next = offset + views.Length;
                    return new ResourceRevisionViewPage(views, count, next < count ? (int?)next : null);
                }
            }, CancellationToken.None);
        }

        public IReadOnlyList<MutationAttempt> Unresolved()
        {
            return Access((db, cursor) => {
                var locations = new List<KeyValuePair<ResourceAuthorityScopeId, string>>();
                using (var command = db.CreateCommand())
                {
                    command.CommandText = "SELECT scope,operation FROM resource_attempt WHERE state IN (0,1) ORDER BY prepared_ticks,attempt";
                    using (var reader = command.ExecuteReader())
                        while (reader.Read()) locations.Add(new KeyValuePair<ResourceAuthorityScopeId, string>(ParseScope(reader.GetString(0)), reader.GetString(1)));
                }
                return (IReadOnlyList<MutationAttempt>)locations.Select(item => Find(db, item.Key, item.Value)?.Fact.Attempt ??
                    throw new InvalidDataException("Pending attempt locator is unavailable.")).ToArray();
            }, CancellationToken.None);
        }

        private ResourceJournalReceipt RequireAttempt(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("An exact attempt id is required.");
            return Access((db, cursor) => {
                var scope = AttemptScope(db, id);
                return scope == null ? throw new KeyNotFoundException("Resource attempt is unavailable.") : FindAttempt(db, ParseScope(scope), id);
            }, CancellationToken.None);
        }

        private static ResourceAuthorityScopeId ParseScope(string scope)
        { var split = scope.IndexOf(':'); return new ResourceAuthorityScopeId(scope.Substring(0, split), scope.Substring(split + 1)); }
        private static string AttemptScope(SqliteConnection db, string id)
        {
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT scope FROM resource_attempt WHERE attempt=$attempt";
                command.Parameters.AddWithValue("$attempt", id); return command.ExecuteScalar() as string;
            }
        }
        private static string AuthorityOperation(SqliteConnection db, ResourceAuthorityScopeId scope, string commit)
        {
            using (var command = Command(db, "SELECT operation FROM resource_commit WHERE scope=$scope AND commit_id=$commit", scope))
            { command.Parameters.AddWithValue("$commit", commit); return command.ExecuteScalar() as string; }
        }
        private static SqliteCommand ViewCommand(SqliteConnection db, string sql, ResourceAuthorityScopeId scope, ResourceRef reference)
        {
            var command = Command(db, sql, scope); command.Parameters.AddWithValue("$uri", reference.Uri);
            command.Parameters.AddWithValue("$revision", reference.Revision); return command;
        }
    }
}
