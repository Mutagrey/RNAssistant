using System;
using System.IO;
using System.Threading;
using System.Text;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    public sealed partial class WorkspaceFileService
    {
        private static int TextByteLength(string text)
        {
            try { return StrictUtf8.GetByteCount(text ?? string.Empty); }
            catch (EncoderFallbackException)
            { throw new WorkspaceFileException("encoding_ambiguous", "Only strict UTF-8 text is supported."); }
        }

        private bool IsLargeExpected(ResourceRef expected)
        {
            if (expected?.IsExact != true) return false;
            var address = ResourceUri.Parse(expected.Uri);
            return address.Provider == "file" && address.Segments.Count == 1 &&
                _authority.GetRevision(Scope(address.Segments[0]), expected)?.Payload?.ByteLength > MaximumTextBytes;
        }

        private WorkspaceFileObservation WriteStagedText(WorkspaceDescriptor workspace,
            string relativePath, string text, ResourceRef expected, Action onDispatch)
        {
            RequireWritable(workspace);
            text = text ?? string.Empty;
            if (expected != null)
            {
                bool bom, crlfOnly;
                InspectTextFormat(ResolvePath(workspace, relativePath, false), out bom, out crlfOnly);
                if (crlfOnly) text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
                if (bom && !text.StartsWith("\uFEFF", StringComparison.Ordinal)) text = "\uFEFF" + text;
            }
            var stagedPath = Path.Combine(Path.GetTempPath(), "rna-text-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var staged = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 81920, FileOptions.SequentialScan))
                {
                    using (var writer = new StreamWriter(staged, StrictUtf8, 81920, true))
                        writer.Write(text);
                    staged.Flush(true);
                }
                var saved = CommitStagedFileCore(workspace, relativePath, stagedPath, expected,
                    long.MaxValue, onDispatch, default(CancellationToken), true);
                return new WorkspaceFileObservation { RelativePath = relativePath,
                    Reference = saved.Reference, ContentSha256 = saved.ContentSha256,
                    SourceByteLength = saved.ByteLength, AuthorityCommit = saved.AuthorityCommit };
            }
            catch (EncoderFallbackException)
            { throw new WorkspaceFileException("encoding_ambiguous", "Only strict UTF-8 text is supported."); }
            finally
            {
                try { if (File.Exists(stagedPath)) File.Delete(stagedPath); }
                catch (IOException) { /* Preserve the write outcome; scratch cleanup is separate. */ }
                catch (UnauthorizedAccessException) { /* The journal still owns any uncertain effect. */ }
            }
        }

        private static void InspectTextFormat(string path, out bool bom, out bool crlfOnly)
        {
            var decoder = StrictUtf8.GetDecoder();
            var bytes = new byte[81920];
            var chars = new char[81922];
            var first = true;
            var previousCr = false;
            var foundCrLf = false;
            var bareLf = false;
            bom = false;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, FileOptions.SequentialScan))
            {
                try
                {
                    int count;
                    while ((count = input.Read(bytes, 0, bytes.Length)) != 0)
                    {
                        var length = decoder.GetChars(bytes, 0, count, chars, 0, false);
                        for (var i = 0; i < length; i++)
                        {
                            var value = chars[i];
                            if (first) { bom = value == '\uFEFF'; first = false; }
                            if (value == '\n')
                            { if (previousCr) foundCrLf = true; else bareLf = true; }
                            previousCr = value == '\r';
                        }
                    }
                    decoder.GetChars(Array.Empty<byte>(), 0, 0, chars, 0, true);
                }
                catch (DecoderFallbackException)
                { throw new WorkspaceFileException("encoding_ambiguous", "Only strict UTF-8 text is supported."); }
            }
            crlfOnly = foundCrLf && !bareLf;
        }

        // Bounded binary publication for plugin result streams. Staging is private to
        // the caller; all target mutation, journal and read-back remain in this owner.
        public WorkspaceFileSnapshot CommitStagedFile(WorkspaceDescriptor workspace, string relativePath,
            string stagedPath, ResourceRef expected, long maximumBytes, Action onDispatch = null,
            CancellationToken token = default(CancellationToken))
        { return CommitStagedFileCore(workspace, relativePath, stagedPath, expected,
            maximumBytes, onDispatch, token, false); }

        private WorkspaceFileSnapshot CommitStagedFileCore(WorkspaceDescriptor workspace,
            string relativePath, string stagedPath, ResourceRef expected, long maximumBytes,
            Action onDispatch, CancellationToken token, bool textSource)
        {
            RequireWritable(workspace);
            var path = ResolvePath(workspace, relativePath, false, true);
            if (maximumBytes < 1 || !textSource && maximumBytes > 256L * 1024 * 1024)
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
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
                    payload = PayloadRef.FromBlob(_blobs.StoreHashedFile(staged, hash, length,
                        textSource ? "text/plain; charset=utf-8" : "application/octet-stream", token, null));
                }
                if (textSource && expected != null &&
                    payload.Sha256 == _authority.GetRevision(scope, expected).ContentSha256)
                    return new WorkspaceFileSnapshot(relativePath,
                        PublishedEvidence(expected, ResourceRepresentations.Raw));
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
                    return new WorkspaceFileSnapshot(relativePath, PublishedEvidence(exact, ResourceRepresentations.Raw))
                    { AuthorityCommit = commit };
                }
                catch (Exception error)
                {
                    if (!dispatched) { _journal.AbandonBeforeDispatch(attempt.AttemptId); throw; }
                    throw new WorkspaceFileException("effect_unknown", "File publication is uncertain; inspect before retrying. " + error.Message);
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (IOException) { /* Do not hide a journaled uncertain effect. */ }
                    catch (UnauthorizedAccessException) { /* Cleanup can be retried separately. */ }
                }
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
