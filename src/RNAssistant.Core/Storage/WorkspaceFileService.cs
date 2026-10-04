using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    public sealed class WorkspaceFileException : IOException
    {
        public string Code { get; private set; }
        public WorkspaceFileException(string code, string message) : base(message) { Code = code; }
    }

    public sealed class WorkspaceFileObservation
    {
        public string RelativePath { get; internal set; }
        public string Text { get; internal set; }
        public ResourceRef Reference { get; internal set; }
        public string ContentSha256 { get; internal set; }
        public ResourceEvidence Evidence { get; internal set; }
        public ResourceAuthorityCommit AuthorityCommit { get; internal set; }
    }

    public enum WorkspaceRecoveryOutcome
    {
        AbandonedBeforeDispatch,
        AlreadyPublished,
        UnknownAfterDispatch
    }

    public sealed class WorkspaceFileRecoveryResult
    {
        public WorkspaceRecoveryOutcome Outcome { get; internal set; }
        public WorkspaceFileObservation Current { get; internal set; }
        public bool Exists { get; internal set; }
    }

    public sealed class WorkspaceFileDeletion
    {
        public string RelativePath { get; internal set; }
        public ResourceRef DeletedReference { get; internal set; }
        public ResourceAuthorityCommit AuthorityCommit { get; internal set; }
    }

    public sealed class WorkspaceDirectoryPage
    {
        public IReadOnlyList<string> Names { get; internal set; }
        public bool Truncated { get; internal set; }
        public int ScannedEntries { get; internal set; }
    }

    // One file owner for text reads and writes. The locator catalog is not a head store.
    public sealed class WorkspaceFileService
    {
        private const int MaximumTextBytes = 1024 * 1024;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private readonly AppDataPaths _paths;
        private readonly ChatBlobStore _blobs;
        private readonly ResourceAuthorityStore _authority;
        private readonly ResourceMutationJournal _journal;
        internal Action<string> FaultPoint { get; set; }

        private sealed class FileLocator
        {
            public string FileId { get; set; }
            public string AbsolutePath { get; set; }
        }

        private sealed class FileMoveIntent
        {
            public string AttemptId { get; set; }
            public string FileId { get; set; }
            public string WorkspaceId { get; set; }
            public string SourceRelativePath { get; set; }
            public string TargetRelativePath { get; set; }
            public string SourceAbsolutePath { get; set; }
            public string TargetAbsolutePath { get; set; }
            public string ExpectedSha256 { get; set; }
        }

        public WorkspaceFileService(AppDataPaths paths)
        {
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            _blobs = new ChatBlobStore(paths);
            _authority = new ResourceAuthorityStore(paths);
            _journal = new ResourceMutationJournal(paths);
        }

        public WorkspaceDirectoryPage ListPage(WorkspaceDescriptor workspace, string relativeDirectory = "",
            string query = null, int limit = 200)
        {
            if (limit < 1 || limit > 500) throw new ArgumentOutOfRangeException(nameof(limit));
            if (query != null && (query.Length > 128 || query.Any(char.IsControl)))
                throw new WorkspaceFileException("invalid_find_query", "Filename query exceeds the bounded text contract.");
            var directory = ResolvePath(workspace, relativeDirectory, true);
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
            var names = new List<string>();
            var scanned = 0;
            var truncated = false;
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                scanned++;
                var name = Path.GetFileName(path);
                if (!StorageFileSystem.IsReparsePoint(path) && IsVisibleFileName(name) &&
                    (string.IsNullOrWhiteSpace(query) || name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    if (names.Count == limit) { truncated = true; break; }
                    names.Add(name);
                }
                if (scanned == 5000) { truncated = true; break; }
            }
            names.Sort(StringComparer.Ordinal);
            return new WorkspaceDirectoryPage
            { Names = names.AsReadOnly(), Truncated = truncated, ScannedEntries = scanned };
        }

        private static bool IsVisibleFileName(string name)
        {
            return !name.Equals(".rnassistant", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals(".git", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals(".codex", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(".key", StringComparison.OrdinalIgnoreCase);
        }

        public WorkspaceFileObservation ReadText(WorkspaceDescriptor workspace, string relativePath)
        {
            var path = ResolvePath(workspace, relativePath, false);
            var id = FileIdentity(path);
            var scope = Scope(id);
            using (_journal.AcquireScope(scope, true))
            {
                EnsureNoUnresolved(scope);
                ResolvePath(workspace, relativePath, false);
                if (!File.Exists(path))
                {
                    ObserveMissing(scope, id);
                    throw new FileNotFoundException("Workspace file was not found.", path);
                }
                byte[] bytes;
                string text;
                try { bytes = ReadBounded(path); text = Decode(bytes); }
                catch (WorkspaceFileException ex) when (ex.Code == "encoding_ambiguous" || ex.Code == "file_too_large")
                {
                    ObserveUnknown(scope, id, ex.Code);
                    throw;
                }
                var exact = Observe(scope, id, bytes);
                var snapshot = _authority.Capture(scope);
                var view = _authority.GetView(scope, exact, "text");
                if (view?.Payload == null || snapshot.GetHead(Identity(id))?.Revision?.Revision != exact.Revision)
                    throw new WorkspaceFileException("snapshot_unavailable", "Exact observed file view is unavailable.");
                return new WorkspaceFileObservation
                { RelativePath = relativePath, Text = text, Reference = exact, ContentSha256 = Hash(bytes),
                    Evidence = new ResourceEvidence("ev_" + Guid.NewGuid().ToString("N"), scope, exact,
                        "text", ResourceCoverage.Whole(), true, snapshot.Generation, view.Payload,
                        contentSha256: Hash(bytes)) };
            }
        }

        public ResourceAuthoritySnapshotSet CaptureAuthority(IEnumerable<ResourceEvidence> evidence)
        {
            var scopes = (evidence ?? Enumerable.Empty<ResourceEvidence>()).Where(item => item != null)
                .Select(item => item.ScopeId).Distinct().ToArray();
            if (_journal.Unresolved().Any(attempt => attempt.State == MutationAttemptState.DispatchMayHaveOccurred &&
                scopes.Contains(attempt.ScopeId)))
                throw new WorkspaceFileException("unresolved_previous_effect", "File authority has an unresolved dispatch.");
            return _authority.CaptureMany(scopes);
        }

        public string ReadHistoricalText(WorkspaceDescriptor workspace, string relativePath, ResourceRef exact)
        {
            var path = ResolvePath(workspace, relativePath, false);
            var id = FileIdentity(path);
            if (exact == null || !exact.IsExact || exact.Uri != ResourceUri.Create("file", id))
                throw new WorkspaceFileException("invalid_resource_ref", "Historical reference does not belong to this file.");
            var view = _authority.GetView(Scope(id), exact, "text");
            if (view?.Payload == null || !_blobs.HasStoredReference(view.Payload.ToBlobReference()))
                throw new WorkspaceFileException("snapshot_unavailable", "Exact historical file payload is unavailable.");
            if (!_authority.Capture(Scope(id)).Commits.Any(commit => commit.HeadChanges.Any(change =>
                change.After.Knowledge == HeadKnowledge.Known &&
                change.After.Revision.Revision == exact.Revision)))
                throw new WorkspaceFileException("snapshot_unavailable", "Exact file revision was not published.");
            return Decode(_blobs.ReadBytes(view.Payload.ToBlobReference()));
        }

        public WorkspaceFileObservation CreateText(WorkspaceDescriptor workspace, string relativePath, string text,
            Action onDispatch = null)
        {
            return CreateTextCore(workspace, relativePath, text, null, "files.create", onDispatch);
        }

        public WorkspaceFileObservation CopyText(WorkspaceDescriptor workspace, string sourceRelativePath,
            ResourceRef expectedSource, string targetRelativePath, Action onDispatch = null)
        {
            RequireWritable(workspace);
            var source = ReadText(workspace, sourceRelativePath);
            if (expectedSource == null || expectedSource.Uri != source.Reference.Uri ||
                expectedSource.Revision != source.Reference.Revision)
                throw new WorkspaceFileException("target_conflict", "Source changed since the accepted complete read.");
            return CreateTextCore(workspace, targetRelativePath, source.Text,
                source.Reference, "files.copy", onDispatch);
        }

        // An in-workspace move keeps the logical file id but advances its revision:
        // old path observations must be refreshed after the locator changes.
        public WorkspaceFileObservation MoveText(WorkspaceDescriptor workspace, string sourceRelativePath,
            ResourceRef expectedSource, string targetRelativePath, Action onDispatch = null)
        {
            RequireWritable(workspace);
            var source = ResolvePath(workspace, sourceRelativePath, false, true);
            var target = ResolvePath(workspace, targetRelativePath, false, true);
            if (source == target)
                throw new WorkspaceFileException("target_conflict", "Move source and target must differ.");
            var id = FileIdentity(source);
            var scope = Scope(id);
            using (_journal.AcquireScope(scope, true))
            {
                EnsureNoUnresolved(scope);
                if (expectedSource == null || !expectedSource.IsExact || expectedSource.Uri != Identity(id).Uri)
                    throw new WorkspaceFileException("target_conflict", "An exact accepted source read is required.");
                if (!File.Exists(source))
                {
                    ObserveMissing(scope, id);
                    throw new WorkspaceFileException("target_conflict", "Move source is missing.");
                }
                byte[] bytes;
                try { bytes = ReadBounded(source); Decode(bytes); }
                catch (WorkspaceFileException ex) when (ex.Code == "encoding_ambiguous" || ex.Code == "file_too_large")
                {
                    ObserveUnknown(scope, id, ex.Code);
                    throw;
                }
                var snapshot = _authority.Capture(scope);
                var head = snapshot.GetHead(Identity(id));
                if (head?.Knowledge != HeadKnowledge.Known || head.Revision.Revision != expectedSource.Revision)
                    throw new WorkspaceFileException("target_conflict", "Source authority changed since the accepted read.");
                var revision = _authority.GetRevision(scope, expectedSource);
                var hash = Hash(bytes);
                if (revision?.ContentSha256 != hash)
                {
                    Observe(scope, id, bytes);
                    throw new WorkspaceFileException("target_conflict", "Source changed outside RNAssistant since the accepted read.");
                }
                if (revision.Payload == null || !_blobs.HasStoredReference(revision.Payload.ToBlobReference()))
                    throw new WorkspaceFileException("snapshot_unavailable", "The retained move preimage is unavailable.");
                if (!Directory.Exists(Path.GetDirectoryName(target)))
                    throw new WorkspaceFileException("target_parent_missing", "Move target parent must already exist.");
                if (File.Exists(target) || Directory.Exists(target) || ExistingLocatorId(target) != null ||
                    PendingMoveFileId(target) != null)
                    throw new WorkspaceFileException("target_exists", "Move never overwrites a target or its prior identity.");
                var attempt = _journal.Prepare(scope, "files.move", Identity(id), expectedSource.Revision, revision.Payload);
                var intent = new FileMoveIntent { AttemptId = attempt.AttemptId, FileId = id,
                    WorkspaceId = workspace.WorkspaceId, SourceRelativePath = sourceRelativePath,
                    TargetRelativePath = targetRelativePath, SourceAbsolutePath = source,
                    TargetAbsolutePath = target, ExpectedSha256 = hash };
                var dispatched = false;
                try
                {
                    SaveMoveIntent(intent);
                    onDispatch?.Invoke();
                    _journal.MarkDispatchMayHaveOccurred(attempt.AttemptId);
                    dispatched = true;
                    ResolvePath(workspace, sourceRelativePath, false, true);
                    ResolvePath(workspace, targetRelativePath, false, true);
                    if (Hash(ReadBounded(source)) != hash || File.Exists(target) || Directory.Exists(target) ||
                        ExistingLocatorId(target) != null || PendingMoveFileId(target) != id)
                        throw new WorkspaceFileException("target_conflict", "Move paths changed just before dispatch.");
                    File.Move(source, target);
                    FaultPoint?.Invoke("after-dispatch");
                    ResolvePath(workspace, targetRelativePath, false, true);
                    if (File.Exists(source) || Hash(ReadBounded(target)) != hash)
                        throw new WorkspaceFileException("read_back_mismatch", "Move read-back did not match its retained preimage.");
                    RelocateLocator(source, target, id);
                    FaultPoint?.Invoke("after-locator");
                    var exact = NewRevision(id);
                    _authority.RegisterRevision(scope, new ResourceRevisionMetadata(exact, hash,
                        revision.Payload, expectedSource));
                    _authority.RegisterView(scope, new ResourceRevisionView(exact, "text", hash,
                        revision.Payload, ResourceCoverage.Whole()));
                    var current = _authority.Capture(scope);
                    var currentHead = current.GetHead(Identity(id));
                    if (currentHead == null || !currentHead.SameAuthority(head))
                        throw new WorkspaceFileException("authority_conflict", "File authority changed before move publication.");
                    var effect = new ResourceEffect("fx_" + Guid.NewGuid().ToString("N"), "files.move",
                        ResourceEffectOutcome.VerifiedChanged,
                        new[] { new ResourceImpact(Identity(id), ResourceImpactRelation.Exact,
                            before: expectedSource, after: exact, changeKind: "text-move") },
                        "source absent; target bytes and locator verified");
                    var commit = ResourceAuthorityCommit.Create(scope, current.Generation, effect,
                        new[] { new ResourceHeadChange(Identity(id), currentHead,
                            ResourceHeadState.Known(exact, current.Generation + 1, "files.move")) },
                        AuthorityCommitReason.MutationEffect, attempt.AttemptId);
                    _authority.Publish(commit);
                    _journal.Resolve(attempt.AttemptId, commit.CommitId);
                    RemoveMoveIntent(attempt.AttemptId);
                    return new WorkspaceFileObservation { RelativePath = targetRelativePath,
                        Text = Decode(bytes), Reference = exact, ContentSha256 = hash, AuthorityCommit = commit };
                }
                catch (Exception ex)
                {
                    if (!dispatched)
                    {
                        _journal.AbandonBeforeDispatch(attempt.AttemptId);
                        RemoveMoveIntent(attempt.AttemptId);
                        throw;
                    }
                    throw new WorkspaceFileException("effect_unknown",
                        "Move dispatch or publication is uncertain; reconcile before retry. " + ex.Message);
                }
            }
        }

        private WorkspaceFileObservation CreateTextCore(WorkspaceDescriptor workspace, string relativePath,
            string text, ResourceRef copySource, string operation, Action onDispatch)
        {
            RequireWritable(workspace);
            var path = ResolvePath(workspace, relativePath, false, true);
            if (File.Exists(path) || Directory.Exists(path))
                throw new WorkspaceFileException("target_exists", "Create does not overwrite an existing target.");
            var bytes = Encode(text);
            var id = FileIdentity(path);
            var scope = Scope(id);
            using (_journal.AcquireScope(scope, true))
            {
                EnsureNoUnresolved(scope);
                if (File.Exists(path)) throw new WorkspaceFileException("target_exists", "Target appeared before dispatch.");
                var head = _authority.Capture(scope).GetHead(Identity(id));
                if (head != null) throw new WorkspaceFileException("target_conflict", "The path has prior resource history; inspect it before reuse.");
                return Mutate(scope, id, relativePath, bytes, null, null, operation, onDispatch, () =>
                {
                    ResolvePath(workspace, relativePath, false, true);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    ResolvePath(workspace, relativePath, false, true);
                    using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { output.Write(bytes, 0, bytes.Length); output.Flush(true); }
                }, path, copySource);
            }
        }

        public WorkspaceFileObservation ReplaceText(WorkspaceDescriptor workspace, string relativePath,
            ResourceRef expected, string text, Action onDispatch = null)
        {
            return ReplaceTextCore(workspace, relativePath, expected, text, null, "files.replace", onDispatch);
        }

        public WorkspaceFileObservation RestoreHistoricalText(WorkspaceDescriptor workspace, string relativePath,
            ResourceRef expectedCurrent, ResourceRef historical, Action onDispatch = null)
        {
            var text = ReadHistoricalText(workspace, relativePath, historical);
            return ReplaceTextCore(workspace, relativePath, expectedCurrent, text,
                historical, "files.restore", onDispatch);
        }

        // Move the accepted whole text to workspace-local managed trash. No unlink
        // fallback is allowed if the move or read-back cannot be verified.
        public WorkspaceFileDeletion DeleteText(WorkspaceDescriptor workspace, string relativePath,
            ResourceRef expected, Action onDispatch = null)
        {
            RequireWritable(workspace);
            var path = ResolvePath(workspace, relativePath, false, true);
            var id = FileIdentity(path);
            var scope = Scope(id);
            using (_journal.AcquireScope(scope, true))
            {
                EnsureNoUnresolved(scope);
                if (expected == null || !expected.IsExact || expected.Uri != Identity(id).Uri)
                    throw new WorkspaceFileException("target_conflict", "An exact accepted file read is required.");
                if (!File.Exists(path))
                {
                    ObserveMissing(scope, id);
                    throw new WorkspaceFileException("target_conflict", "Observed file is missing.");
                }
                byte[] bytes;
                try { bytes = ReadBounded(path); Decode(bytes); }
                catch (WorkspaceFileException ex) when (ex.Code == "encoding_ambiguous" || ex.Code == "file_too_large")
                {
                    ObserveUnknown(scope, id, ex.Code);
                    throw;
                }
                var snapshot = _authority.Capture(scope);
                var head = snapshot.GetHead(Identity(id));
                if (head?.Knowledge != HeadKnowledge.Known || head.Revision.Revision != expected.Revision)
                    throw new WorkspaceFileException("target_conflict", "File authority changed since the accepted read.");
                var revision = _authority.GetRevision(scope, expected);
                if (revision?.ContentSha256 != Hash(bytes))
                {
                    Observe(scope, id, bytes);
                    throw new WorkspaceFileException("target_conflict", "File changed outside RNAssistant since the accepted read.");
                }
                if (revision.Payload == null || !_blobs.HasStoredReference(revision.Payload.ToBlobReference()))
                    throw new WorkspaceFileException("snapshot_unavailable", "The retained delete preimage is unavailable.");
                var trashDirectory = TrashDirectory(workspace, id);
                var attempt = _journal.Prepare(scope, "files.delete", Identity(id), expected.Revision, revision.Payload);
                var trashPath = Path.Combine(trashDirectory, attempt.AttemptId + ".utf8");
                var dispatched = false;
                try
                {
                    onDispatch?.Invoke();
                    _journal.MarkDispatchMayHaveOccurred(attempt.AttemptId);
                    dispatched = true;
                    ResolvePath(workspace, relativePath, false, true);
                    if (File.Exists(trashPath)) throw new IOException("Managed trash target already exists.");
                    File.Move(path, trashPath);
                    FaultPoint?.Invoke("after-dispatch");
                    if (File.Exists(path) || Hash(ReadBounded(trashPath)) != revision.ContentSha256)
                        throw new WorkspaceFileException("read_back_mismatch", "Delete read-back did not match its retained preimage.");
                    var current = _authority.Capture(scope);
                    var currentHead = current.GetHead(Identity(id));
                    if (!currentHead.SameAuthority(head))
                        throw new WorkspaceFileException("authority_conflict", "File authority changed before deletion publication.");
                    var effect = new ResourceEffect("fx_" + Guid.NewGuid().ToString("N"), "files.delete",
                        ResourceEffectOutcome.VerifiedChanged,
                        new[] { new ResourceImpact(Identity(id), ResourceImpactRelation.Exact,
                            before: expected, changeKind: "text-delete-to-managed-trash") }, "source absent; managed trash preimage verified");
                    var commit = ResourceAuthorityCommit.Create(scope, current.Generation, effect,
                        new[] { new ResourceHeadChange(Identity(id), currentHead,
                            ResourceHeadState.Unavailable(Identity(id), current.Generation + 1, "files.delete")) },
                        AuthorityCommitReason.MutationEffect, attempt.AttemptId);
                    _authority.Publish(commit);
                    _journal.Resolve(attempt.AttemptId, commit.CommitId);
                    return new WorkspaceFileDeletion
                    { RelativePath = relativePath, DeletedReference = expected.Copy(), AuthorityCommit = commit };
                }
                catch (Exception ex)
                {
                    if (!dispatched) { _journal.AbandonBeforeDispatch(attempt.AttemptId); throw; }
                    throw new WorkspaceFileException("effect_unknown",
                        "Delete dispatch or publication is uncertain; reconcile before retry. " + ex.Message);
                }
            }
        }

        // Restores only the latest verified (or explicitly reconciled) managed
        // deletion. The new head gets a fresh revision with exact provenance.
        public WorkspaceFileObservation RestoreDeletedText(WorkspaceDescriptor workspace,
            string relativePath, Action onDispatch = null)
        {
            RequireWritable(workspace);
            var path = ResolvePath(workspace, relativePath, false, true);
            var id = FileIdentity(path);
            var scope = Scope(id);
            using (_journal.AcquireScope(scope, true))
            {
                EnsureNoUnresolved(scope);
                if (File.Exists(path) || Directory.Exists(path))
                    throw new WorkspaceFileException("target_conflict", "Restore never overwrites an existing path.");
                var snapshot = _authority.Capture(scope);
                var head = snapshot.GetHead(Identity(id));
                var deletion = snapshot.Commits.LastOrDefault(commit => commit.NewGeneration == snapshot.Generation &&
                    commit.Effect?.Operation == "files.delete" && commit.MutationAttemptId != null);
                var change = deletion?.HeadChanges.SingleOrDefault();
                if (head?.Knowledge != HeadKnowledge.Unavailable || change?.Before?.Knowledge != HeadKnowledge.Known ||
                    !change.After.SameAuthority(head))
                    throw new WorkspaceFileException("target_conflict", "The current head is not a recoverable managed deletion.");
                var previous = change.Before.Revision;
                var retained = _authority.GetRevision(scope, previous);
                if (retained?.Payload == null || !_blobs.HasStoredReference(retained.Payload.ToBlobReference()))
                    throw new WorkspaceFileException("snapshot_unavailable", "The retained deleted text is unavailable.");
                var trashPath = Path.Combine(TrashDirectory(workspace, id, false), deletion.MutationAttemptId + ".utf8");
                if (!File.Exists(trashPath) || StorageFileSystem.IsReparsePoint(trashPath))
                    throw new WorkspaceFileException("snapshot_unavailable", "The managed trash preimage is unavailable.");
                var bytes = ReadBounded(trashPath);
                Decode(bytes);
                if (Hash(bytes) != retained.ContentSha256)
                    throw new WorkspaceFileException("snapshot_unavailable", "The managed trash preimage changed.");
                return Mutate(scope, id, relativePath, bytes, null, previous, "files.restore", onDispatch, () =>
                {
                    ResolvePath(workspace, relativePath, false, true);
                    if (File.Exists(path) || Directory.Exists(path))
                        throw new WorkspaceFileException("target_conflict", "Restore target appeared before dispatch.");
                    File.Move(trashPath, path);
                }, path, expectedHead: head);
            }
        }

        private WorkspaceFileObservation ReplaceTextCore(WorkspaceDescriptor workspace, string relativePath,
            ResourceRef expected, string text, ResourceRef restoredFrom, string operation, Action onDispatch)
        {
            RequireWritable(workspace);
            var path = ResolvePath(workspace, relativePath, false, true);
            var id = FileIdentity(path);
            var scope = Scope(id);
            using (_journal.AcquireScope(scope, true))
            {
                EnsureNoUnresolved(scope);
                if (expected == null || !expected.IsExact || expected.Uri != Identity(id).Uri)
                    throw new WorkspaceFileException("target_conflict", "An exact accepted file observation is required.");
                if (!File.Exists(path)) throw new WorkspaceFileException("target_conflict", "Observed file is missing.");
                byte[] before;
                string beforeText;
                try { before = ReadBounded(path); beforeText = Decode(before); }
                catch (WorkspaceFileException ex) when (ex.Code == "encoding_ambiguous" || ex.Code == "file_too_large")
                {
                    ObserveUnknown(scope, id, ex.Code);
                    throw;
                }
                var head = _authority.Capture(scope).GetHead(Identity(id));
                if (head?.Knowledge != HeadKnowledge.Known || head.Revision.Revision != expected.Revision)
                    throw new WorkspaceFileException("target_conflict", "File authority changed since the accepted read.");
                if (_authority.GetRevision(scope, expected)?.ContentSha256 != Hash(before))
                {
                    Observe(scope, id, before);
                    throw new WorkspaceFileException("target_conflict", "File changed outside RNAssistant since the accepted read.");
                }
                var updated = PreserveLineEndings(text, beforeText);
                // UTF-8 decoding retains U+FEFF. A whole-file replacement from a
                // model usually omits it, so preserve the source BOM explicitly.
                if (before.Length >= 3 && before[0] == 0xef && before[1] == 0xbb && before[2] == 0xbf &&
                    !updated.StartsWith("\uFEFF", StringComparison.Ordinal))
                    updated = "\uFEFF" + updated;
                var bytes = Encode(updated);
                if (Hash(bytes) == Hash(before))
                    return new WorkspaceFileObservation
                    { RelativePath = relativePath, Text = Decode(before), Reference = expected.Copy(), ContentSha256 = Hash(before) };
                return Mutate(scope, id, relativePath, bytes, expected, restoredFrom, operation, onDispatch, () =>
                {
                    var temp = path + ".rna-" + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        { output.Write(bytes, 0, bytes.Length); output.Flush(true); }
                        // This recheck narrows, but cannot eliminate, races with uncoordinated editors.
                        ResolvePath(workspace, relativePath, false, true);
                        if (Hash(ReadBounded(path)) != Hash(before))
                            throw new WorkspaceFileException("target_conflict", "File changed just before replace.");
                        File.Replace(temp, path, null);
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }, path);
            }
        }

        public WorkspaceFileObservation PatchExact(WorkspaceDescriptor workspace, string relativePath,
            ResourceRef expected, string oldText, string newText, Action onDispatch = null)
        {
            if (string.IsNullOrEmpty(oldText)) throw new WorkspaceFileException("invalid_patch", "Exact patch anchor is required.");
            var observed = ReadText(workspace, relativePath);
            if (expected == null || observed.Reference.Revision != expected.Revision)
                throw new WorkspaceFileException("target_conflict", "File changed since the accepted read.");
            var first = observed.Text.IndexOf(oldText, StringComparison.Ordinal);
            if (first < 0 || observed.Text.IndexOf(oldText, first + oldText.Length, StringComparison.Ordinal) >= 0)
                throw new WorkspaceFileException("invalid_patch", "Patch anchor is missing or ambiguous.");
            return ReplaceTextCore(workspace, relativePath, expected,
                observed.Text.Substring(0, first) + (newText ?? string.Empty) + observed.Text.Substring(first + oldText.Length),
                null, "files.patch", onDispatch);
        }

        // Resolves an interrupted attempt by inspecting current bytes. The effect
        // remains causally unknown even when the intended bytes are present.
        public WorkspaceFileRecoveryResult ReconcileUncertain(WorkspaceDescriptor workspace, string relativePath)
        {
            var path = ResolvePath(workspace, relativePath, false);
            var id = FileIdentity(path);
            var scope = Scope(id);
            using (_journal.AcquireScope(scope, true))
            {
                var attempts = _journal.Unresolved().Where(item => item.ScopeId.Equals(scope)).ToArray();
                if (attempts.Length == 0)
                    throw new WorkspaceFileException("no_pending_effect", "This file has no unresolved mutation attempt.");
                foreach (var prepared in attempts.Where(item => item.State == MutationAttemptState.Prepared))
                {
                    _journal.AbandonBeforeDispatch(prepared.AttemptId);
                    if (prepared.Operation == "files.move") RemoveMoveIntent(prepared.AttemptId);
                }
                var dispatched = attempts.Where(item => item.State == MutationAttemptState.DispatchMayHaveOccurred).ToArray();
                if (dispatched.Length == 0)
                    return new WorkspaceFileRecoveryResult
                    { Outcome = WorkspaceRecoveryOutcome.AbandonedBeforeDispatch, Exists = File.Exists(path) };
                if (dispatched.Length != 1)
                    throw new WorkspaceFileException("recovery_ambiguous", "Multiple dispatched attempts require manual inspection.");
                var attempt = dispatched[0];
                var snapshot = _authority.Capture(scope);
                var published = snapshot.Commits.LastOrDefault(item => item.MutationAttemptId == attempt.AttemptId);
                if (attempt.Operation == "files.move")
                    return ReconcileMove(workspace, scope, id, attempt, snapshot, published);
                if (published != null)
                {
                    _journal.Resolve(attempt.AttemptId, published.CommitId);
                    return new WorkspaceFileRecoveryResult
                    { Outcome = WorkspaceRecoveryOutcome.AlreadyPublished, Exists = File.Exists(path),
                        Current = File.Exists(path) ? ReadCurrentUnderLease(scope, id, relativePath, path) : null };
                }

                var before = snapshot.GetHead(Identity(id));
                WorkspaceFileObservation observed = null;
                ResourceHeadState after;
                if (File.Exists(path))
                {
                    var bytes = ReadBounded(path);
                    var text = Decode(bytes);
                    var hash = Hash(bytes);
                    var exact = before?.Knowledge == HeadKnowledge.Known &&
                        _authority.GetRevision(scope, before.Revision)?.ContentSha256 == hash
                            ? before.Revision.Copy() : NewRevision(id);
                    if (before?.Revision?.Revision != exact.Revision)
                    {
                        var payload = PayloadRef.FromBlob(_blobs.StoreBytes(bytes, "text/plain; charset=utf-8"));
                        _authority.RegisterRevision(scope, new ResourceRevisionMetadata(exact, hash, payload, before?.Revision));
                        _authority.RegisterView(scope, new ResourceRevisionView(exact, "text", hash,
                            payload, ResourceCoverage.Whole()));
                    }
                    after = ResourceHeadState.Known(exact, snapshot.Generation + 1, "reconciled-unknown-effect");
                    observed = new WorkspaceFileObservation
                    { RelativePath = relativePath, Text = text, Reference = exact, ContentSha256 = hash };
                }
                else after = ResourceHeadState.Unavailable(Identity(id), snapshot.Generation + 1,
                    "reconciled-missing-file-after-unknown-effect");
                var effect = new ResourceEffect("fx_" + Guid.NewGuid().ToString("N"), attempt.Operation,
                    ResourceEffectOutcome.UnknownAfterDispatch,
                    new[] { new ResourceImpact(Identity(id), ResourceImpactRelation.Exact,
                        before: before?.Revision, after: observed?.Reference, changeKind: "reconciled-current-state") },
                    "Current state inspected; causality of interrupted dispatch is not proven.");
                var commit = ResourceAuthorityCommit.Create(scope, snapshot.Generation, effect,
                    new[] { new ResourceHeadChange(Identity(id), before, after) },
                    AuthorityCommitReason.Reconciliation, attempt.AttemptId);
                _authority.Publish(commit);
                _journal.Resolve(attempt.AttemptId, commit.CommitId);
                return new WorkspaceFileRecoveryResult
                { Outcome = WorkspaceRecoveryOutcome.UnknownAfterDispatch, Exists = observed != null,
                    Current = observed };
            }
        }

        private WorkspaceFileRecoveryResult ReconcileMove(WorkspaceDescriptor workspace,
            ResourceAuthorityScopeId scope, string id, MutationAttempt attempt,
            ResourceAuthoritySnapshot snapshot, ResourceAuthorityCommit published)
        {
            var intent = ReadMoveIntent(attempt.AttemptId);
            if (intent == null || intent.FileId != id || intent.WorkspaceId != workspace.WorkspaceId ||
                intent.AttemptId != attempt.AttemptId || intent.ExpectedSha256 != attempt.Payload?.Sha256)
                throw new WorkspaceFileException("recovery_ambiguous", "The durable move intent is missing or inconsistent.");
            var source = ResolvePath(workspace, intent.SourceRelativePath, false);
            var target = ResolvePath(workspace, intent.TargetRelativePath, false);
            if (Directory.Exists(source) || Directory.Exists(target))
                throw new WorkspaceFileException("recovery_ambiguous", "A move path became a directory.");
            var sourceExists = File.Exists(source);
            var targetExists = File.Exists(target);
            if (published != null)
            {
                RelocateLocator(source, target, id);
                _journal.Resolve(attempt.AttemptId, published.CommitId);
                RemoveMoveIntent(attempt.AttemptId);
                return new WorkspaceFileRecoveryResult
                { Outcome = WorkspaceRecoveryOutcome.AlreadyPublished, Exists = targetExists };
            }
            if (sourceExists && targetExists)
                throw new WorkspaceFileException("recovery_ambiguous", "Both move paths now contain files; inspect them manually.");
            var previous = snapshot.GetHead(Identity(id));
            if (previous?.Knowledge != HeadKnowledge.Known ||
                previous.Revision.Revision != attempt.ExpectedRevision)
                throw new WorkspaceFileException("recovery_ambiguous", "Move source authority changed before reconciliation.");
            WorkspaceFileObservation observed = null;
            ResourceHeadState after;
            if (targetExists)
            {
                var bytes = ReadBounded(target);
                Decode(bytes);
                if (Hash(bytes) != intent.ExpectedSha256)
                    throw new WorkspaceFileException("recovery_ambiguous", "Moved target no longer matches the prepared preimage.");
                RelocateLocator(source, target, id);
                var exact = NewRevision(id);
                _authority.RegisterRevision(scope, new ResourceRevisionMetadata(exact, intent.ExpectedSha256,
                    attempt.Payload, previous.Revision));
                _authority.RegisterView(scope, new ResourceRevisionView(exact, "text", intent.ExpectedSha256,
                    attempt.Payload, ResourceCoverage.Whole()));
                after = ResourceHeadState.Known(exact, snapshot.Generation + 1, "reconciled-unknown-move");
                observed = new WorkspaceFileObservation { RelativePath = intent.TargetRelativePath,
                    Text = Decode(bytes), Reference = exact, ContentSha256 = intent.ExpectedSha256 };
            }
            else if (sourceExists)
            {
                var bytes = ReadBounded(source);
                var text = Decode(bytes);
                RelocateLocator(target, source, id);
                var hash = Hash(bytes);
                var exact = hash == intent.ExpectedSha256 ? previous.Revision.Copy() : NewRevision(id);
                if (exact.Revision != previous.Revision.Revision)
                {
                    var payload = PayloadRef.FromBlob(_blobs.StoreBytes(bytes, "text/plain; charset=utf-8"));
                    _authority.RegisterRevision(scope, new ResourceRevisionMetadata(exact, hash, payload, previous.Revision));
                    _authority.RegisterView(scope, new ResourceRevisionView(exact, "text", hash,
                        payload, ResourceCoverage.Whole()));
                }
                after = ResourceHeadState.Known(exact, snapshot.Generation + 1, "reconciled-unknown-move");
                observed = new WorkspaceFileObservation { RelativePath = intent.SourceRelativePath,
                    Text = text, Reference = exact, ContentSha256 = hash };
            }
            else
            {
                RelocateLocator(target, source, id);
                after = ResourceHeadState.Unavailable(Identity(id), snapshot.Generation + 1,
                    "reconciled-missing-file-after-unknown-move");
            }
            var effect = new ResourceEffect("fx_" + Guid.NewGuid().ToString("N"), "files.move",
                ResourceEffectOutcome.UnknownAfterDispatch,
                new[] { new ResourceImpact(Identity(id), ResourceImpactRelation.Exact,
                    before: previous.Revision, after: observed?.Reference,
                    changeKind: "reconciled-current-location") },
                "Current paths inspected; causality of interrupted move is not proven.");
            var commit = ResourceAuthorityCommit.Create(scope, snapshot.Generation, effect,
                new[] { new ResourceHeadChange(Identity(id), previous, after) },
                AuthorityCommitReason.Reconciliation, attempt.AttemptId);
            _authority.Publish(commit);
            _journal.Resolve(attempt.AttemptId, commit.CommitId);
            RemoveMoveIntent(attempt.AttemptId);
            return new WorkspaceFileRecoveryResult
            { Outcome = WorkspaceRecoveryOutcome.UnknownAfterDispatch, Exists = observed != null, Current = observed };
        }

        private WorkspaceFileObservation ReadCurrentUnderLease(ResourceAuthorityScopeId scope, string id,
            string relativePath, string path)
        {
            var bytes = ReadBounded(path);
            var text = Decode(bytes);
            return new WorkspaceFileObservation
            { RelativePath = relativePath, Text = text, Reference = Observe(scope, id, bytes), ContentSha256 = Hash(bytes) };
        }

        private WorkspaceFileObservation Mutate(ResourceAuthorityScopeId scope, string id, string relativePath,
            byte[] bytes, ResourceRef before, ResourceRef restoredFrom, string operation,
            Action onDispatch, Action dispatch, string path, ResourceRef copySource = null,
            ResourceHeadState expectedHead = null)
        {
            var payload = PayloadRef.FromBlob(_blobs.StoreBytes(bytes, "text/plain; charset=utf-8"));
            var attempt = _journal.Prepare(scope, operation, Identity(id), before?.Revision, payload);
            var dispatched = false;
            try
            {
                onDispatch?.Invoke();
                _journal.MarkDispatchMayHaveOccurred(attempt.AttemptId);
                dispatched = true;
                dispatch();
                FaultPoint?.Invoke("after-dispatch");
                var afterBytes = ReadBounded(path);
                if (Hash(afterBytes) != payload.Sha256)
                    throw new WorkspaceFileException("read_back_mismatch", "Written file did not match the intended bytes.");
                var exact = NewRevision(id);
                _authority.RegisterRevision(scope, new ResourceRevisionMetadata(exact, payload.Sha256,
                    payload, before ?? (expectedHead?.Knowledge == HeadKnowledge.Unavailable ? restoredFrom : null), restoredFrom,
                    dependencies: copySource == null ? null : new[] {
                        new ResourceDependency(copySource, "text", ResourceCoverage.Whole(), "immutable-snapshot") }));
                _authority.RegisterView(scope, new ResourceRevisionView(exact, "text", payload.Sha256,
                    payload, ResourceCoverage.Whole()));
                var snapshot = _authority.Capture(scope);
                var head = snapshot.GetHead(Identity(id));
                if (expectedHead != null ? head == null || !head.SameAuthority(expectedHead) :
                    head?.Revision?.Revision != before?.Revision)
                    throw new WorkspaceFileException("authority_conflict", "File authority changed before publication.");
                var effect = new ResourceEffect("fx_" + Guid.NewGuid().ToString("N"), operation,
                    restoredFrom == null ? ResourceEffectOutcome.VerifiedChanged : ResourceEffectOutcome.Restored,
                    new[] { new ResourceImpact(Identity(id), ResourceImpactRelation.Exact,
                        before: before, after: exact,
                        changeKind: restoredFrom != null ? "text-restore" :
                            copySource != null ? "text-copy" : "text-write") }, "exact read-back");
                var commit = ResourceAuthorityCommit.Create(scope, snapshot.Generation, effect,
                    new[] { new ResourceHeadChange(Identity(id), head,
                        ResourceHeadState.Known(exact, snapshot.Generation + 1)) },
                    restoredFrom == null ? AuthorityCommitReason.MutationEffect : AuthorityCommitReason.Restore,
                    attempt.AttemptId);
                _authority.Publish(commit);
                _journal.Resolve(attempt.AttemptId, commit.CommitId);
                return new WorkspaceFileObservation
                { RelativePath = relativePath, Text = Decode(afterBytes), Reference = exact,
                    ContentSha256 = payload.Sha256, AuthorityCommit = commit };
            }
            catch (Exception ex)
            {
                if (!dispatched)
                {
                    _journal.AbandonBeforeDispatch(attempt.AttemptId);
                    throw;
                }
                throw new WorkspaceFileException("effect_unknown",
                    "File dispatch or publication is uncertain; inspect the file and mutation journal before retry. " + ex.Message);
            }
        }

        private ResourceRef Observe(ResourceAuthorityScopeId scope, string id, byte[] bytes)
        {
            var hash = Hash(bytes);
            var snapshot = _authority.Capture(scope);
            var previous = snapshot.GetHead(Identity(id));
            if (previous?.Knowledge == HeadKnowledge.Known &&
                _authority.GetRevision(scope, previous.Revision)?.ContentSha256 == hash) return previous.Revision.Copy();
            var payload = PayloadRef.FromBlob(_blobs.StoreBytes(bytes, "text/plain; charset=utf-8"));
            var exact = NewRevision(id);
            _authority.RegisterRevision(scope, new ResourceRevisionMetadata(exact, hash, payload, previous?.Revision));
            _authority.RegisterView(scope, new ResourceRevisionView(exact, "text", hash, payload, ResourceCoverage.Whole()));
            _authority.Publish(ResourceAuthorityCommit.Create(scope, snapshot.Generation, null,
                new[] { new ResourceHeadChange(Identity(id), previous,
                    ResourceHeadState.Known(exact, snapshot.Generation + 1)) },
                previous == null ? AuthorityCommitReason.InitialObservation : AuthorityCommitReason.ExternalDrift));
            return exact;
        }

        private void ObserveMissing(ResourceAuthorityScopeId scope, string id)
        {
            var snapshot = _authority.Capture(scope);
            var previous = snapshot.GetHead(Identity(id));
            if (previous == null || previous.Knowledge == HeadKnowledge.Unavailable) return;
            var effect = new ResourceEffect("fx_" + Guid.NewGuid().ToString("N"), "filesystem.external_delete",
                ResourceEffectOutcome.ExternalDriftObserved,
                new[] { new ResourceImpact(Identity(id), ResourceImpactRelation.Exact,
                    before: previous.Revision, changeKind: "external-delete") }, "File missing during exact read.");
            _authority.Publish(ResourceAuthorityCommit.Create(scope, snapshot.Generation, effect,
                new[] { new ResourceHeadChange(Identity(id), previous,
                    ResourceHeadState.Unavailable(Identity(id), snapshot.Generation + 1, "external-delete")) },
                AuthorityCommitReason.ExternalDrift));
        }

        private void ObserveUnknown(ResourceAuthorityScopeId scope, string id, string cause)
        {
            var snapshot = _authority.Capture(scope);
            var previous = snapshot.GetHead(Identity(id));
            if (previous?.Knowledge == HeadKnowledge.Unknown && previous.Cause == cause) return;
            var effect = new ResourceEffect("fx_" + Guid.NewGuid().ToString("N"), "filesystem.external_unrepresentable",
                ResourceEffectOutcome.ExternalDriftObserved,
                new[] { new ResourceImpact(Identity(id), ResourceImpactRelation.Exact,
                    before: previous?.Revision, changeKind: cause) }, "File cannot be represented as bounded UTF-8 text.");
            _authority.Publish(ResourceAuthorityCommit.Create(scope, snapshot.Generation, effect,
                new[] { new ResourceHeadChange(Identity(id), previous,
                    ResourceHeadState.Unknown(Identity(id), snapshot.Generation + 1, cause)) },
                AuthorityCommitReason.ExternalDrift));
        }

        private void EnsureNoUnresolved(ResourceAuthorityScopeId scope)
        {
            if (_journal.Unresolved().Any(attempt => attempt.ScopeId.Equals(scope)))
                throw new WorkspaceFileException("unresolved_previous_effect",
                    "A possible previous file effect is unresolved; reconcile it before another write.");
        }

        private string FileIdentity(string absolutePath)
        {
            var directory = Path.Combine(_paths.WorkspaceDirectory, "file-locators");
            StorageFileSystem.EnsureRegularDirectory(directory);
            var locatorPath = LocatorPath(directory, absolutePath);
            using (StorageFileSystem.AcquireWriteLock(Path.Combine(directory, "locators.lck")))
            {
                var saved = ReadLocator(locatorPath, absolutePath);
                if (saved != null) return saved.FileId;
                var pendingId = PendingMoveFileId(absolutePath);
                if (pendingId != null) return pendingId;
                var id = "file_" + Guid.NewGuid().ToString("N");
                using (var stream = new FileStream(locatorPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, StrictUtf8))
                    writer.Write(JsonConvert.SerializeObject(new FileLocator { FileId = id, AbsolutePath = absolutePath }));
                return id;
            }
        }

        private string ExistingLocatorId(string absolutePath)
        {
            var directory = Path.Combine(_paths.WorkspaceDirectory, "file-locators");
            StorageFileSystem.EnsureRegularDirectory(directory);
            using (StorageFileSystem.AcquireWriteLock(Path.Combine(directory, "locators.lck")))
                return ReadLocator(LocatorPath(directory, absolutePath), absolutePath)?.FileId;
        }

        private static string LocatorPath(string directory, string absolutePath)
        { return Path.Combine(directory, RNAssistant.Core.Tools.TextPatternEngine.Sha256(absolutePath) + ".json"); }

        private static FileLocator ReadLocator(string locatorPath, string absolutePath)
        {
            if (!File.Exists(locatorPath)) return null;
            if (StorageFileSystem.IsReparsePoint(locatorPath)) throw new IOException("File locator cannot be a link.");
            var saved = JsonConvert.DeserializeObject<FileLocator>(File.ReadAllText(locatorPath, StrictUtf8));
            if (saved == null || saved.AbsolutePath != absolutePath || string.IsNullOrWhiteSpace(saved.FileId))
                throw new InvalidDataException("File locator is invalid.");
            return saved;
        }

        private void RelocateLocator(string source, string target, string id)
        {
            var directory = Path.Combine(_paths.WorkspaceDirectory, "file-locators");
            StorageFileSystem.EnsureRegularDirectory(directory);
            using (StorageFileSystem.AcquireWriteLock(Path.Combine(directory, "locators.lck")))
            {
                var sourcePath = LocatorPath(directory, source);
                var targetPath = LocatorPath(directory, target);
                var oldLocator = ReadLocator(sourcePath, source);
                var newLocator = ReadLocator(targetPath, target);
                if (oldLocator?.FileId != id && newLocator?.FileId != id)
                    throw new WorkspaceFileException("recovery_ambiguous", "The move's file locator is missing.");
                if (oldLocator != null && oldLocator.FileId != id ||
                    newLocator != null && newLocator.FileId != id)
                    throw new WorkspaceFileException("target_conflict", "A move path has another file identity.");
                if (newLocator == null)
                    StorageFileSystem.WriteAllTextAtomic(targetPath,
                        JsonConvert.SerializeObject(new FileLocator { FileId = id, AbsolutePath = target }), StrictUtf8);
                if (oldLocator != null) File.Delete(sourcePath);
            }
        }

        private string MoveIntentDirectory()
        { return Path.Combine(_paths.WorkspaceDirectory, "file-moves"); }

        private void SaveMoveIntent(FileMoveIntent intent)
        {
            var directory = MoveIntentDirectory();
            StorageFileSystem.EnsureRegularDirectory(directory);
            var path = Path.Combine(directory, intent.AttemptId + ".json");
            var locatorDirectory = Path.Combine(_paths.WorkspaceDirectory, "file-locators");
            StorageFileSystem.EnsureRegularDirectory(locatorDirectory);
            using (StorageFileSystem.AcquireWriteLock(Path.Combine(locatorDirectory, "locators.lck")))
            {
                if (File.Exists(path)) throw new IOException("Move intent already exists.");
                StorageFileSystem.WriteAllTextAtomic(path, JsonConvert.SerializeObject(intent), StrictUtf8);
            }
        }

        private FileMoveIntent ReadMoveIntent(string attemptId)
        {
            var path = Path.Combine(MoveIntentDirectory(), attemptId + ".json");
            if (!File.Exists(path)) return null;
            if (StorageFileSystem.IsReparsePoint(path)) throw new IOException("Move intent cannot be a link.");
            var intent = JsonConvert.DeserializeObject<FileMoveIntent>(File.ReadAllText(path, StrictUtf8));
            if (intent == null || intent.AttemptId != attemptId || string.IsNullOrWhiteSpace(intent.FileId) ||
                string.IsNullOrWhiteSpace(intent.WorkspaceId) || string.IsNullOrWhiteSpace(intent.SourceRelativePath) ||
                string.IsNullOrWhiteSpace(intent.TargetRelativePath) || string.IsNullOrWhiteSpace(intent.ExpectedSha256))
                throw new InvalidDataException("Move intent is invalid.");
            return intent;
        }

        private void RemoveMoveIntent(string attemptId)
        {
            try
            {
                var directory = Path.Combine(_paths.WorkspaceDirectory, "file-locators");
                StorageFileSystem.EnsureRegularDirectory(directory);
                using (StorageFileSystem.AcquireWriteLock(Path.Combine(directory, "locators.lck")))
                    File.Delete(Path.Combine(MoveIntentDirectory(), attemptId + ".json"));
            }
            catch (IOException) { /* A resolved journal attempt remains authoritative. */ }
            catch (UnauthorizedAccessException) { /* Stale intent is ignored after resolution. */ }
        }

        private string PendingMoveFileId(string absolutePath)
        {
            var directory = MoveIntentDirectory();
            if (!Directory.Exists(directory)) return null;
            StorageFileSystem.EnsureRegularDirectory(directory);
            var unresolved = _journal.Unresolved().Where(item => item.Operation == "files.move")
                .ToDictionary(item => item.AttemptId, StringComparer.Ordinal);
            if (unresolved.Count == 0) return null;
            string id = null;
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                if (StorageFileSystem.IsReparsePoint(path)) throw new IOException("Move intent cannot be a link.");
                var intent = JsonConvert.DeserializeObject<FileMoveIntent>(File.ReadAllText(path, StrictUtf8));
                if (intent == null || string.IsNullOrWhiteSpace(intent.AttemptId) ||
                    Path.GetFileNameWithoutExtension(path) != intent.AttemptId ||
                    string.IsNullOrWhiteSpace(intent.FileId))
                    throw new InvalidDataException("Move intent is invalid.");
                MutationAttempt attempt;
                if (!unresolved.TryGetValue(intent.AttemptId, out attempt) ||
                    intent.SourceAbsolutePath != absolutePath && intent.TargetAbsolutePath != absolutePath) continue;
                if (!attempt.ScopeId.Equals(Scope(intent.FileId)) || !attempt.Target.Equals(Identity(intent.FileId)))
                    throw new InvalidDataException("Move intent does not match its mutation attempt.");
                if (id != null && id != intent.FileId)
                    throw new WorkspaceFileException("recovery_ambiguous", "More than one unresolved move names this path.");
                id = intent.FileId;
            }
            return id;
        }

        private static string TrashDirectory(WorkspaceDescriptor workspace, string id, bool create = true)
        {
            var control = Path.Combine(workspace.RootPath, ".rnassistant");
            if (!StorageFileSystem.IsRegularDirectory(control))
                throw new WorkspaceFileException("workspace_unavailable", "Workspace control directory is unavailable or linked.");
            var root = Path.Combine(control, "trash");
            var directory = Path.Combine(root, id);
            if (create)
            {
                StorageFileSystem.EnsureRegularDirectory(root);
                StorageFileSystem.EnsureRegularDirectory(directory);
            }
            else if (!StorageFileSystem.IsRegularDirectory(root) ||
                !StorageFileSystem.IsRegularDirectory(directory))
                throw new WorkspaceFileException("snapshot_unavailable", "Managed trash directory is unavailable or linked.");
            return directory;
        }

        internal static void RelocateLocators(AppDataPaths paths, string oldRoot, string newRoot)
        {
            var directory = Path.Combine(paths.WorkspaceDirectory, "file-locators");
            if (!Directory.Exists(directory)) return;
            StorageFileSystem.EnsureRegularDirectory(directory);
            var oldPrefix = Path.GetFullPath(oldRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var newPrefix = Path.GetFullPath(newRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            using (StorageFileSystem.AcquireWriteLock(Path.Combine(directory, "locators.lck")))
            {
                var intentDirectory = Path.Combine(paths.WorkspaceDirectory, "file-moves");
                if (Directory.Exists(intentDirectory))
                {
                    StorageFileSystem.EnsureRegularDirectory(intentDirectory);
                    foreach (var path in Directory.EnumerateFiles(intentDirectory, "*.json"))
                    {
                        if (StorageFileSystem.IsReparsePoint(path)) throw new IOException("Move intent cannot be a link.");
                        var intent = JsonConvert.DeserializeObject<FileMoveIntent>(File.ReadAllText(path, StrictUtf8));
                        if (intent == null || string.IsNullOrWhiteSpace(intent.SourceAbsolutePath) ||
                            string.IsNullOrWhiteSpace(intent.TargetAbsolutePath))
                            throw new InvalidDataException("Move intent is invalid.");
                        var changed = false;
                        if (intent.SourceAbsolutePath.StartsWith(oldPrefix, comparison))
                        { intent.SourceAbsolutePath = newPrefix + intent.SourceAbsolutePath.Substring(oldPrefix.Length); changed = true; }
                        if (intent.TargetAbsolutePath.StartsWith(oldPrefix, comparison))
                        { intent.TargetAbsolutePath = newPrefix + intent.TargetAbsolutePath.Substring(oldPrefix.Length); changed = true; }
                        if (changed) StorageFileSystem.WriteAllTextAtomic(path, JsonConvert.SerializeObject(intent), StrictUtf8);
                    }
                }
                var moves = new List<Tuple<string, string, FileLocator>>();
                foreach (var source in Directory.EnumerateFiles(directory, "*.json"))
                {
                    if (StorageFileSystem.IsReparsePoint(source)) throw new IOException("File locator cannot be a link.");
                    var locator = JsonConvert.DeserializeObject<FileLocator>(File.ReadAllText(source, StrictUtf8));
                    if (locator == null || string.IsNullOrWhiteSpace(locator.FileId) || string.IsNullOrWhiteSpace(locator.AbsolutePath))
                        throw new InvalidDataException("File locator is invalid.");
                    if (!locator.AbsolutePath.StartsWith(oldPrefix, comparison)) continue;
                    var targetPath = newPrefix + locator.AbsolutePath.Substring(oldPrefix.Length);
                    var target = Path.Combine(directory, RNAssistant.Core.Tools.TextPatternEngine.Sha256(targetPath) + ".json");
                    if (File.Exists(target))
                    {
                        var existing = JsonConvert.DeserializeObject<FileLocator>(File.ReadAllText(target, StrictUtf8));
                        if (existing?.FileId != locator.FileId || existing.AbsolutePath != targetPath)
                            throw new InvalidDataException("Relocated file identity collides with another source.");
                    }
                    moves.Add(Tuple.Create(source, target, new FileLocator { FileId = locator.FileId, AbsolutePath = targetPath }));
                }
                foreach (var move in moves)
                {
                    if (!File.Exists(move.Item2))
                        StorageFileSystem.WriteAllTextAtomic(move.Item2, JsonConvert.SerializeObject(move.Item3), StrictUtf8);
                    File.Delete(move.Item1);
                }
            }
        }

        private static string ResolvePath(WorkspaceDescriptor workspace, string relativePath,
            bool directory, bool forWrite = false)
        {
            if (workspace == null || string.IsNullOrWhiteSpace(workspace.RootPath)) throw new ArgumentException("Opened workspace required.");
            if (!Directory.Exists(workspace.RootPath) || StorageFileSystem.IsReparsePoint(workspace.RootPath))
                throw new WorkspaceFileException("workspace_unavailable", "Workspace root is unavailable or linked.");
            if (directory && string.IsNullOrEmpty(relativePath)) return workspace.RootPath;
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) ||
                relativePath.IndexOf(':') >= 0 || relativePath.Any(char.IsControl))
                throw new WorkspaceFileException("path_outside_mount", "A safe relative path is required.");
            var parts = relativePath.Replace('\\', '/').Split('/');
            if (parts.Any(part => part.Length == 0 || part == "." || part == ".." ||
                part.Equals(".rnassistant", StringComparison.OrdinalIgnoreCase) ||
                part.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                part.Equals(".codex", StringComparison.OrdinalIgnoreCase) ||
                part.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
                part.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) ||
                part.EndsWith(".key", StringComparison.OrdinalIgnoreCase) ||
                forWrite && (part.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))))
                throw new WorkspaceFileException("path_outside_mount", "Path escapes or modifies workspace control metadata.");
            var path = workspace.RootPath;
            foreach (var part in parts)
            {
                if (Directory.Exists(path))
                {
                    var collision = Directory.EnumerateFileSystemEntries(path)
                        .Select(Path.GetFileName)
                        .FirstOrDefault(name => string.Equals(name, part, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(name, part, StringComparison.Ordinal));
                    if (collision != null) throw new WorkspaceFileException("case_collision", "Path collides by case with " + collision);
                }
                path = Path.Combine(path, part);
                if ((File.Exists(path) || Directory.Exists(path)) && StorageFileSystem.IsReparsePoint(path))
                    throw new WorkspaceFileException("path_outside_mount", "Linked path components are not supported.");
            }
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(workspace.RootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new WorkspaceFileException("path_outside_mount", "Path is outside the workspace root.");
            return full;
        }

        private static byte[] ReadBounded(string path)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (input.Length > MaximumTextBytes) throw new WorkspaceFileException("file_too_large", "Text file exceeds the one MiB whole-read bound.");
                var bytes = new byte[input.Length];
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var count = input.Read(bytes, offset, bytes.Length - offset);
                    if (count == 0) throw new IOException("File changed during read.");
                    offset += count;
                }
                if (input.Length != bytes.Length) throw new IOException("File changed during read.");
                return bytes;
            }
        }

        private static string Decode(byte[] bytes)
        {
            try { return StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException) { throw new WorkspaceFileException("encoding_ambiguous", "Only strict UTF-8 text is supported."); }
        }
        private static byte[] Encode(string text)
        {
            var bytes = StrictUtf8.GetBytes(text ?? string.Empty);
            if (bytes.Length > MaximumTextBytes) throw new WorkspaceFileException("file_too_large", "Text exceeds the one MiB write bound.");
            return bytes;
        }
        private static string PreserveLineEndings(string next, string previous)
        {
            next = next ?? string.Empty;
            if (previous.Contains("\r\n") && !previous.Replace("\r\n", "").Contains("\n"))
                return next.Replace("\r\n", "\n").Replace("\n", "\r\n");
            return next;
        }
        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        private static ResourceIdentity Identity(string id) { return new ResourceIdentity(ResourceUri.Create("file", id)); }
        private static ResourceAuthorityScopeId Scope(string id) { return new ResourceAuthorityScopeId("file", id); }
        private static ResourceRef NewRevision(string id) { return new ResourceRef(Identity(id).Uri, "r_" + Guid.NewGuid().ToString("N")); }
        private static void RequireWritable(WorkspaceDescriptor workspace)
        {
            if (workspace == null || workspace.ReadOnly) throw new WorkspaceFileException("read_only_mount", "Workspace is read-only.");
        }
    }
}
