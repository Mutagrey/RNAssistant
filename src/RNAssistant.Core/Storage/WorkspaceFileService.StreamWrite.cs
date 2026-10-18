using System;
using System.IO;
using System.Threading;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    public sealed partial class WorkspaceFileService
    {
        // Bounded binary publication for plugin result streams. Staging is private to
        // the caller; all target mutation, journal and read-back remain in this owner.
        public WorkspaceFileSnapshot CommitStagedFile(WorkspaceDescriptor workspace, string relativePath,
            string stagedPath, ResourceRef expected, long maximumBytes, Action onDispatch = null,
            CancellationToken token = default(CancellationToken))
        {
            RequireWritable(workspace);
            var path = ResolvePath(workspace, relativePath, false, true);
            if (maximumBytes < 1 || maximumBytes > 256L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            if (StorageFileSystem.IsReparsePoint(stagedPath)) throw new WorkspaceFileException("invalid_staging", "Staging cannot be a link.");
            using (AcquireFileScope(path, out var id, out var scope, token))
            {
                EnsureNoUnresolved(scope);
                var state = _authority.Capture(scope); var before = state.GetHead(Identity(id));
                if (expected == null)
                {
                    if (File.Exists(path) || Directory.Exists(path) || before != null)
                        throw new WorkspaceFileException("target_exists", "Create never overwrites an existing or previously owned path.");
                }
                else
                {
                    if (before?.Knowledge != HeadKnowledge.Known || before.Revision.Uri != expected.Uri || before.Revision.Revision != expected.Revision || !File.Exists(path))
                        throw new WorkspaceFileException("target_conflict", "The accepted file observation changed.");
                    using (var previous = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        long length;
                        if (previous.Length > maximumBytes || ChatBlobStore.HashFile(previous, out length, token, null) != _authority.GetRevision(scope, expected).ContentSha256)
                            throw new WorkspaceFileException("target_conflict", "File changed outside RNAssistant.");
                    }
                }
                PayloadRef payload;
                using (var staged = new FileStream(stagedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (staged.Length > maximumBytes) throw new WorkspaceFileException("file_too_large", "Staged file exceeds its budget.");
                    long length; var hash = ChatBlobStore.HashFile(staged, out length, token, null);
                    payload = PayloadRef.FromBlob(_blobs.StoreHashedFile(staged, hash, length, "application/octet-stream", token, null));
                }
                var attempt = _journal.Prepare(scope, expected == null ? "files.create" : "files.replace", Identity(id), expected?.Revision, payload);
                var dispatched = false;
                var temporary = path + ".rna-" + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    token.ThrowIfCancellationRequested();
                    ResolvePath(workspace, relativePath, false, true);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    ResolvePath(workspace, relativePath, false, true);
                    using (var retained = _blobs.OpenVerifiedRead(payload.ToBlobReference(), maximumBytes, token))
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { retained.CopyTo(output); output.Flush(true); }
                    token.ThrowIfCancellationRequested();
                    if (expected != null)
                        using (var previous = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            long length;
                            if (ChatBlobStore.HashFile(previous, out length, token, null) != _authority.GetRevision(scope, expected).ContentSha256)
                                throw new WorkspaceFileException("target_conflict", "File changed immediately before dispatch.");
                        }
                    onDispatch?.Invoke(); _journal.MarkDispatchMayHaveOccurred(attempt.AttemptId); dispatched = true;
                    if (expected == null) File.Move(temporary, path); else File.Replace(temporary, path, null);
                    FaultPoint?.Invoke("after-dispatch");
                    using (var readBack = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        long length;
                        if (readBack.Length != payload.ByteLength || ChatBlobStore.HashFile(readBack, out length, token, null) != payload.Sha256)
                            throw new WorkspaceFileException("read_back_mismatch", "Published bytes do not match staging.");
                    }
                    var exact = NewRevision(id);
                    _authority.RegisterRevision(scope, new ResourceRevisionMetadata(exact, payload.Sha256, payload, expected));
                    _authority.RegisterView(scope, new ResourceRevisionView(exact, ResourceRepresentations.Raw, payload.Sha256, payload, ResourceCoverage.Whole()));
                    var frozen = _authority.Capture(scope); var head = frozen.GetHead(Identity(id));
                    if (head?.Revision?.Revision != expected?.Revision) throw new WorkspaceFileException("authority_conflict", "File authority changed before publication.");
                    var effect = new ResourceEffect("fx_" + Guid.NewGuid().ToString("N"), attempt.Operation, ResourceEffectOutcome.VerifiedChanged,
                        new[] { new ResourceImpact(Identity(id), ResourceImpactRelation.Exact, before: expected, after: exact, changeKind: "file-write") }, "exact read-back");
                    var commit = ResourceAuthorityCommit.Create(scope, frozen.Generation, effect,
                        new[] { new ResourceHeadChange(Identity(id), head, ResourceHeadState.Known(exact, frozen.Generation + 1)) }, AuthorityCommitReason.MutationEffect, attempt.AttemptId);
                    _authority.Publish(commit); _journal.Resolve(attempt.AttemptId, commit.CommitId);
                    return new WorkspaceFileSnapshot(relativePath, PublishedEvidence(exact, ResourceRepresentations.Raw));
                }
                catch (Exception error)
                {
                    if (!dispatched) { _journal.AbandonBeforeDispatch(attempt.AttemptId); throw; }
                    throw new WorkspaceFileException("effect_unknown", "File publication is uncertain; inspect before retrying. " + error.Message);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }

        public bool CreateDirectory(WorkspaceDescriptor workspace, string relativePath, Action onDispatch = null)
        {
            RequireWritable(workspace);
            var path = ResolvePath(workspace, relativePath, false, true);
            using (AcquireFileScope(path, out var id, out var scope))
            {
                EnsureNoUnresolved(scope);
                if (File.Exists(path)) throw new WorkspaceFileException("target_exists", "A file occupies this directory path.");
                if (Directory.Exists(path)) return false;
                var payload = PayloadRef.FromBlob(_blobs.StoreText("{\"kind\":\"directory\"}", "application/json"));
                var attempt = _journal.Prepare(scope, "files.mkdir", Identity(id), null, payload);
                var dispatched = false;
                try
                {
                    ResolvePath(workspace, relativePath, false, true);
                    onDispatch?.Invoke(); _journal.MarkDispatchMayHaveOccurred(attempt.AttemptId); dispatched = true;
                    Directory.CreateDirectory(path); FaultPoint?.Invoke("after-dispatch");
                    ResolvePath(workspace, relativePath, false);
                    if (!Directory.Exists(path)) throw new IOException("Directory read-back failed.");
                    var exact = NewRevision(id); var frozen = _authority.Capture(scope); var before = frozen.GetHead(Identity(id));
                    _authority.RegisterRevision(scope, new ResourceRevisionMetadata(exact, payload.Sha256, payload, before?.Revision));
                    _authority.RegisterView(scope, new ResourceRevisionView(exact, ResourceRepresentations.Metadata, payload.Sha256, payload, ResourceCoverage.Whole()));
                    var effect = new ResourceEffect("fx_" + Guid.NewGuid().ToString("N"), "files.mkdir", ResourceEffectOutcome.VerifiedChanged,
                        new[] { new ResourceImpact(Identity(id), ResourceImpactRelation.Exact, before: before?.Revision, after: exact, changeKind: "directory-create") }, "Directory read-back");
                    var commit = ResourceAuthorityCommit.Create(scope, frozen.Generation, effect,
                        new[] { new ResourceHeadChange(Identity(id), before, ResourceHeadState.Known(exact, frozen.Generation + 1)) }, AuthorityCommitReason.MutationEffect, attempt.AttemptId);
                    _authority.Publish(commit); _journal.Resolve(attempt.AttemptId, commit.CommitId); return true;
                }
                catch (Exception error)
                { if (!dispatched) { _journal.AbandonBeforeDispatch(attempt.AttemptId); throw; } throw new WorkspaceFileException("effect_unknown", "Directory creation is uncertain. " + error.Message); }
            }
        }
    }
}
