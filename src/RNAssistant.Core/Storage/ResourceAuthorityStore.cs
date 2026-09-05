using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    public sealed class ResourceAuthorityConflictException : InvalidOperationException
    {
        public ResourceAuthorityConflictException(string message) : base(message) { }
    }

    public sealed class ResourceAuthorityStore : IResourceAuthorityStore, IResourceRevisionStore
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private static readonly object ObserversSync = new object();
        private static readonly List<WeakReference<ResourceAuthorityStore>> Observers = new List<WeakReference<ResourceAuthorityStore>>();
        private readonly string _directory;
        private readonly object _sync = new object();
        private readonly Dictionary<string, ScopeProjection> _scopes =
            new Dictionary<string, ScopeProjection>(StringComparer.Ordinal);

        public event EventHandler<ResourceAuthorityChangedEventArgs> Changed;

        public ResourceAuthorityStore(AppDataPaths paths)
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            _directory = paths.ResourceAuthorityDirectory;
            StorageFileSystem.EnsureRegularDirectory(_directory);
            lock (ObserversSync)
            {
                Observers.RemoveAll(item => { ResourceAuthorityStore target; return !item.TryGetTarget(out target); });
                Observers.Add(new WeakReference<ResourceAuthorityStore>(this));
            }
        }

        public ResourceAuthoritySnapshot Capture(ResourceAuthorityScopeId scope)
        {
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            lock (_sync)
            using (AcquireLock()) return Snapshot(Load(scope));
        }

        public ResourceAuthoritySnapshotSet CaptureMany(IReadOnlyList<ResourceAuthorityScopeId> scopes)
        {
            lock (_sync)
            using (AcquireLock())
            {
                return new ResourceAuthoritySnapshotSet((scopes ?? new ResourceAuthorityScopeId[0])
                    .Where(scope => scope != null)
                    .Distinct()
                    .OrderBy(scope => scope.ToString(), StringComparer.Ordinal)
                    .Select(scope => Snapshot(Load(scope)))
                    .ToArray());
            }
        }

        public ResourceHeadState GetHead(ResourceAuthorityScopeId scope, ResourceIdentity identity)
        {
            if (scope == null || identity == null) return null;
            lock (_sync)
            using (AcquireLock())
            {
                ResourceHeadState value;
                return Load(scope).Heads.TryGetValue(identity.Uri, out value)
                    ? value.AtGeneration(value.AuthorityGeneration)
                    : null;
            }
        }

        public AuthorityCommitResult Publish(ResourceAuthorityCommit commit)
        {
            if (commit == null) throw new ArgumentNullException(nameof(commit));
            ResourceAuthoritySnapshot snapshot;
            var published = false;
            lock (_sync)
            using (AcquireLock())
            {
                var state = Load(commit.ScopeId);
                if (state.CommitIds.Contains(commit.CommitId))
                {
                    var original = state.Commits.Single(item => item.CommitId == commit.CommitId);
                    if (JsonConvert.SerializeObject(original) != JsonConvert.SerializeObject(commit))
                        throw new ResourceAuthorityConflictException("A commit id was reused with different content.");
                    return new AuthorityCommitResult(false, true, Snapshot(state));
                }
                if (!string.IsNullOrWhiteSpace(commit.MutationAttemptId) &&
                    state.MutationAttemptCommits.ContainsKey(commit.MutationAttemptId))
                    throw new ResourceAuthorityConflictException("A mutation attempt already has a terminal authority commit.");
                Validate(state, commit);
                Append(commit);
                Apply(state, commit);
                state.AuthorityLength = new FileInfo(PathFor(commit.ScopeId)).Length;
                snapshot = Snapshot(state);
                published = true;
            }
            if (published)
            {
                ResourceAuthorityStore[] stores;
                lock (ObserversSync)
                    stores = Observers.Select(item => { ResourceAuthorityStore target; return item.TryGetTarget(out target) ? target : null; })
                        .Where(item => item != null && item._directory == _directory).ToArray();
                foreach (var store in stores)
                {
                    var handler = store.Changed;
                    if (handler == null) continue;
                    foreach (EventHandler<ResourceAuthorityChangedEventArgs> observer in handler.GetInvocationList())
                        try { observer(store, new ResourceAuthorityChangedEventArgs(commit)); }
                        catch { /* Advisory notifications cannot undo or obscure a durable commit. */ }
                }
            }
            return new AuthorityCommitResult(true, false, snapshot);
        }

        public void RegisterRevision(ResourceAuthorityScopeId scope, ResourceRevisionMetadata revision)
        {
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            if (revision == null) throw new ArgumentNullException(nameof(revision));
            lock (_sync)
            using (AcquireLock())
            {
                var state = Load(scope);
                ResourceRevisionMetadata existing;
                var key = RevisionKey(revision.Reference);
                if (state.Revisions.TryGetValue(key, out existing))
                {
                    if (!SameRevision(existing, revision))
                        throw new InvalidDataException("A resource revision id was reused with different metadata.");
                    return;
                }
                AppendRevision(scope, revision);
                state.Revisions.Add(key, revision);
                state.RevisionLength = new FileInfo(RevisionPathFor(scope)).Length;
            }
        }

        public ResourceRevisionMetadata GetRevision(ResourceAuthorityScopeId scope, ResourceRef reference)
        {
            if (scope == null || reference == null || !reference.IsExact) return null;
            lock (_sync)
            using (AcquireLock())
            {
                ResourceRevisionMetadata value;
                return Load(scope).Revisions.TryGetValue(RevisionKey(reference), out value) ? value : null;
            }
        }

        public void RegisterView(ResourceAuthorityScopeId scope, ResourceRevisionView view)
        {
            if (scope == null || view == null) throw new ArgumentNullException();
            lock (_sync)
            using (AcquireLock())
            {
                var state = Load(scope);
                if (!state.Revisions.ContainsKey(RevisionKey(view.Reference)))
                    throw new InvalidDataException("A view requires registered revision metadata.");
                var key = ViewKey(view);
                ResourceRevisionView existing;
                if (state.Views.TryGetValue(key, out existing))
                {
                    if (existing.ContentSha256 != view.ContentSha256 || existing.Payload?.Sha256 != view.Payload?.Sha256 ||
                        existing.Payload?.ByteLength != view.Payload?.ByteLength ||
                        JsonConvert.SerializeObject(existing.Parts) != JsonConvert.SerializeObject(view.Parts))
                        throw new InvalidDataException("An immutable revision view was changed.");
                    return;
                }
                var bytes = Utf8.GetBytes(JsonConvert.SerializeObject(view) + "\n");
                using (var stream = new FileStream(ViewPathFor(scope), FileMode.Append, FileAccess.Write, FileShare.Read))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                state.Views.Add(key, view);
                state.ViewLength = new FileInfo(ViewPathFor(scope)).Length;
            }
        }

        public ResourceRevisionView GetView(ResourceAuthorityScopeId scope, ResourceRef reference, string view)
        {
            lock (_sync)
            using (AcquireLock())
                return Load(scope).Views.Values.Where(item => RevisionKey(item.Reference) == RevisionKey(reference) && item.View == view)
                    .OrderByDescending(item => item.Coverage.Kind == ResourceCoverageKinds.Whole).FirstOrDefault();
        }

        private static string ViewKey(ResourceRevisionView view)
        { return RevisionKey(view.Reference) + "\n" + view.View + "\n" + JsonConvert.SerializeObject(view.Coverage); }

        private string ViewPathFor(ResourceAuthorityScopeId scope)
        { return Path.Combine(_directory, RNAssistant.Core.Tools.TextPatternEngine.Sha256(scope.ToString()) + ".views.jsonl"); }

        private ScopeProjection Load(ResourceAuthorityScopeId scope)
        {
            ScopeProjection state;
            var scopeKey = scope.ToString();
            var path = PathFor(scope);
            var revisionPath = RevisionPathFor(scope);
            var authorityLength = File.Exists(path) ? new FileInfo(path).Length : 0;
            var revisionLength = File.Exists(revisionPath) ? new FileInfo(revisionPath).Length : 0;
            var viewPath = ViewPathFor(scope);
            var viewLength = File.Exists(viewPath) ? new FileInfo(viewPath).Length : 0;
            if (_scopes.TryGetValue(scopeKey, out state) && state.AuthorityLength == authorityLength &&
                state.RevisionLength == revisionLength && state.ViewLength == viewLength) return state;
            state = state ?? new ScopeProjection(scope);
            if (authorityLength < state.AuthorityLength || revisionLength < state.RevisionLength || viewLength < state.ViewLength)
                throw new InvalidDataException("An append-only resource journal was truncated.");
            try
            {
                // Cross-window catch-up reads only new metadata. Bytes were registered before
                // heads, so replay revisions/views before validating publication references.
                foreach (var revision in ReadTail<ResourceRevisionMetadata>(revisionPath, state.RevisionLength))
                {
                    if (revision == null || revision.Reference == null || !revision.Reference.IsExact)
                        throw new InvalidDataException("Resource revision journal contains an incomplete record.");
                    var key = RevisionKey(revision.Reference);
                    if (state.Revisions.ContainsKey(key))
                        throw new InvalidDataException("Resource revision journal contains a duplicate revision.");
                    state.Revisions.Add(key, revision);
                }
                foreach (var view in ReadTail<ResourceRevisionView>(viewPath, state.ViewLength))
                {
                    if (view == null || !state.Revisions.ContainsKey(RevisionKey(view.Reference)) || state.Views.ContainsKey(ViewKey(view)))
                        throw new InvalidDataException("Resource view journal contains an invalid record.");
                    state.Views.Add(ViewKey(view), view);
                }
                foreach (var commit in ReadTail<ResourceAuthorityCommit>(path, state.AuthorityLength))
                { Validate(state, commit); Apply(state, commit); }
                state.AuthorityLength = authorityLength; state.RevisionLength = revisionLength; state.ViewLength = viewLength;
                if (!_scopes.ContainsKey(scopeKey) && _scopes.Count >= 64) _scopes.Remove(_scopes.Keys.First());
                _scopes[scopeKey] = state;
                return state;
            }
            catch { _scopes.Remove(scopeKey); throw; }
        }

        private static IEnumerable<T> ReadTail<T>(string path, long offset)
        {
            if (!File.Exists(path)) yield break;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (stream.Length == offset) yield break;
                stream.Seek(-1, SeekOrigin.End);
                if (stream.ReadByte() != '\n') throw new InvalidDataException("Resource journal has an incomplete terminal record.");
                stream.Seek(offset, SeekOrigin.Begin);
                using (var reader = new StreamReader(stream, Utf8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) throw new InvalidDataException("Resource journal contains a blank record.");
                        T record;
                        try { record = JsonConvert.DeserializeObject<T>(line); }
                        catch (JsonException error) { throw new InvalidDataException("Resource journal contains an invalid record.", error); }
                        yield return record;
                    }
                }
            }
        }

        private static void Validate(ScopeProjection state, ResourceAuthorityCommit commit)
        {
            if (commit == null || !state.Scope.Equals(commit.ScopeId))
                throw new ResourceAuthorityConflictException("Authority commit targets a different scope.");
            if (commit.PreviousGeneration != state.Generation || commit.NewGeneration != state.Generation + 1)
                throw new ResourceAuthorityConflictException("RESOURCE_AUTHORITY_CONFLICT: authority generation changed.");
            if (state.CommitIds.Contains(commit.CommitId))
                throw new ResourceAuthorityConflictException("Duplicate authority commit id.");
            if (commit.HeadChanges.Select(item => item.Identity.Uri).Distinct(StringComparer.Ordinal).Count() != commit.HeadChanges.Count)
                throw new ResourceAuthorityConflictException("A commit cannot change one head twice.");
            foreach (var change in commit.HeadChanges)
            {
                ResourceHeadState current;
                state.Heads.TryGetValue(change.Identity.Uri, out current);
                if (change.Before == null ? current != null : current == null || !current.SameAuthority(change.Before))
                    throw new ResourceAuthorityConflictException("RESOURCE_AUTHORITY_CONFLICT: expected resource head changed.");
                if (change.After.AuthorityGeneration != commit.NewGeneration)
                    throw new ResourceAuthorityConflictException("Published head generation does not match its commit.");
                if (change.After.Knowledge == HeadKnowledge.Known && !state.Revisions.ContainsKey(RevisionKey(change.After.Revision)))
                    throw new ResourceAuthorityConflictException("Known heads require durable exact revision metadata before publication.");
            }
        }

        private static void Apply(ScopeProjection state, ResourceAuthorityCommit commit)
        {
            foreach (var change in commit.HeadChanges)
                state.Heads[change.Identity.Uri] = change.After.AtGeneration(commit.NewGeneration);
            state.Generation = commit.NewGeneration;
            state.Commits.Add(commit);
            state.HighWaterCommitId = commit.CommitId;
            if (commit.Effect != null) state.EffectHighWaterMark++;
            state.CommitIds.Add(commit.CommitId);
            if (!string.IsNullOrWhiteSpace(commit.MutationAttemptId))
                state.MutationAttemptCommits[commit.MutationAttemptId] = commit.CommitId;
        }

        private void Append(ResourceAuthorityCommit commit)
        {
            var path = PathFor(commit.ScopeId);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var bytes = Utf8.GetBytes(JsonConvert.SerializeObject(commit, Formatting.None) + "\n");
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write,
                FileShare.Read, 8192, FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        private void AppendRevision(ResourceAuthorityScopeId scope, ResourceRevisionMetadata revision)
        {
            var path = RevisionPathFor(scope);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var bytes = Utf8.GetBytes(JsonConvert.SerializeObject(revision, Formatting.None) + "\n");
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write,
                FileShare.Read, 8192, FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        private string PathFor(ResourceAuthorityScopeId scope)
        {
            return Path.Combine(_directory, RNAssistant.Core.Tools.TextPatternEngine.Sha256(scope.ToString()) + ".authority.jsonl");
        }

        private string RevisionPathFor(ResourceAuthorityScopeId scope)
        {
            return Path.Combine(_directory, RNAssistant.Core.Tools.TextPatternEngine.Sha256(scope.ToString()) + ".revisions.jsonl");
        }

        private IDisposable AcquireLock()
        { return StorageFileSystem.AcquireWriteLock(Path.Combine(_directory, "authority.lck")); }

        internal void ScanCasReferences(CasReachabilityScan scan)
        {
            lock (_sync)
            using (AcquireLock())
            {
                foreach (var path in Directory.EnumerateFiles(_directory, "*.authority.jsonl"))
                {
                    try
                    {
                        var first = File.ReadLines(path, Utf8).FirstOrDefault();
                        var commit = JsonConvert.DeserializeObject<ResourceAuthorityCommit>(first ?? string.Empty);
                        if (commit == null) throw new InvalidDataException("Authority journal is empty.");
                        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(PathFor(commit.ScopeId)), StringComparison.Ordinal))
                            throw new InvalidDataException("Authority scope does not match its journal identity.");
                        var state = Load(commit.ScopeId);
                        foreach (var revision in state.Revisions.Values)
                            if (revision.Payload != null) scan.AddReference(revision.Payload.ToBlobReference(),
                                "resource-authority", commit.ScopeId.ToString(), revision.Reference.Uri + "@" + revision.Reference.Revision);
                        foreach (var view in state.Views.Values)
                        {
                            if (view.Payload != null) scan.AddReference(view.Payload.ToBlobReference(),
                                "resource-authority", commit.ScopeId.ToString(), view.Reference.Uri + "@" + view.Reference.Revision + ":" + view.View);
                            foreach (var part in view.Parts)
                                scan.AddReference(part.ToBlobReference(), "resource-authority", commit.ScopeId.ToString(), view.View);
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is JsonException || ex is ArgumentException || ex is InvalidOperationException)
                    {
                        scan.AddSourceIssue("resource_authority_invalid", "resource-authority", Path.GetFileName(path), ex.Message);
                    }
                }
                // Revision/view bodies may be durable before the first head commit.
                // A crash in that interval must not turn their CAS parts into garbage.
                foreach (var path in Directory.EnumerateFiles(_directory, "*.jsonl")
                    .Where(path => path.EndsWith(".revisions.jsonl", StringComparison.Ordinal) || path.EndsWith(".views.jsonl", StringComparison.Ordinal)))
                {
                    var isView = path.EndsWith(".views.jsonl", StringComparison.Ordinal);
                    var authorityPath = path.Substring(0, path.Length - (isView ? ".views.jsonl" : ".revisions.jsonl").Length) + ".authority.jsonl";
                    if (File.Exists(authorityPath)) continue;
                    try
                    {
                        foreach (var line in File.ReadLines(path, Utf8))
                        {
                            if (isView)
                            {
                                var view = JsonConvert.DeserializeObject<ResourceRevisionView>(line);
                                if (view == null) throw new InvalidDataException("Unpublished view record is empty.");
                                if (view.Payload != null) scan.AddReference(view.Payload.ToBlobReference(), "resource-authority", Path.GetFileName(path), view.Reference.Uri);
                                foreach (var part in view.Parts) scan.AddReference(part.ToBlobReference(), "resource-authority", Path.GetFileName(path), view.View);
                            }
                            else
                            {
                                var revision = JsonConvert.DeserializeObject<ResourceRevisionMetadata>(line);
                                if (revision == null) throw new InvalidDataException("Unpublished revision record is empty.");
                                if (revision.Payload != null) scan.AddReference(revision.Payload.ToBlobReference(), "resource-authority", Path.GetFileName(path), revision.Reference.Uri);
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is JsonException || ex is ArgumentException || ex is InvalidOperationException)
                    { scan.AddSourceIssue("resource_revision_invalid", "resource-authority", Path.GetFileName(path), ex.Message); }
                }
            }
        }

        private static string RevisionKey(ResourceRef reference)
        {
            return (reference == null ? string.Empty : reference.Uri ?? string.Empty) + "\n" +
                (reference == null ? string.Empty : reference.Revision ?? string.Empty);
        }

        private static bool SameRevision(ResourceRevisionMetadata first, ResourceRevisionMetadata second)
        {
            return first != null && second != null &&
                string.Equals(RevisionKey(first.Reference), RevisionKey(second.Reference), StringComparison.Ordinal) &&
                string.Equals(first.ContentSha256 ?? string.Empty, second.ContentSha256 ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(first.Payload == null ? string.Empty : first.Payload.Sha256,
                    second.Payload == null ? string.Empty : second.Payload.Sha256, StringComparison.OrdinalIgnoreCase) &&
                first.Payload?.ByteLength == second.Payload?.ByteLength &&
                JsonConvert.SerializeObject(first.Parent) == JsonConvert.SerializeObject(second.Parent) &&
                JsonConvert.SerializeObject(first.RestoredFrom) == JsonConvert.SerializeObject(second.RestoredFrom) &&
                JsonConvert.SerializeObject(first.Dependencies) == JsonConvert.SerializeObject(second.Dependencies);
        }

        private static ResourceAuthoritySnapshot Snapshot(ScopeProjection state)
        {
            return new ResourceAuthoritySnapshot(state.Scope, state.Generation,
                state.HighWaterCommitId, state.EffectHighWaterMark, state.Heads.Values, state.Commits);
        }

        private sealed class ScopeProjection
        {
            internal ResourceAuthorityScopeId Scope;
            internal long Generation;
            internal long AuthorityLength;
            internal long RevisionLength;
            internal long ViewLength;
            internal string HighWaterCommitId;
            internal long EffectHighWaterMark;
            internal readonly List<ResourceAuthorityCommit> Commits = new List<ResourceAuthorityCommit>();
            internal readonly Dictionary<string, ResourceHeadState> Heads =
                new Dictionary<string, ResourceHeadState>(StringComparer.Ordinal);
            internal readonly HashSet<string> CommitIds = new HashSet<string>(StringComparer.Ordinal);
            internal readonly Dictionary<string, string> MutationAttemptCommits =
                new Dictionary<string, string>(StringComparer.Ordinal);
            internal readonly Dictionary<string, ResourceRevisionMetadata> Revisions =
                new Dictionary<string, ResourceRevisionMetadata>(StringComparer.Ordinal);
            internal readonly Dictionary<string, ResourceRevisionView> Views =
                new Dictionary<string, ResourceRevisionView>(StringComparer.Ordinal);

            internal ScopeProjection(ResourceAuthorityScopeId scope) { Scope = scope; }
        }
    }
}
