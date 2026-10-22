using System;
using System.IO;
using System.Text;
using System.Threading;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    public sealed partial class WorkspaceFileService
    {
        // The complete source and intended result stay in CAS; only the anchor,
        // replacement and fixed-size stream buffers are materialized in memory.
        private WorkspaceFileObservation PatchLargeExact(WorkspaceDescriptor workspace, string relativePath,
            ResourceRef expected, string oldText, string newText, Action onDispatch)
        {
            RequireWritable(workspace);
            var oldBytes = EncodePatchFragment(oldText);
            var newBytes = EncodePatchFragment(newText ?? string.Empty);
            var path = ResolvePath(workspace, relativePath, false, true);
            using (AcquireFileScope(path, out var id, out var scope))
            {
                EnsureNoUnresolved(scope);
                if (expected == null || !expected.IsExact || expected.Uri != Identity(id).Uri || !File.Exists(path))
                    throw new WorkspaceFileException("target_conflict", "The accepted file observation changed.");
                var state = _authority.Capture(scope);
                var head = state.GetHead(Identity(id));
                var revision = _authority.GetRevision(scope, expected);
                if (head?.Knowledge != HeadKnowledge.Known || head.Revision.Revision != expected.Revision ||
                    revision?.Payload == null || revision.Payload.ByteLength <= MaximumTextBytes ||
                    revision.Payload.ByteLength > MaximumWindowTextSourceBytes)
                    throw new WorkspaceFileException("target_conflict", "The accepted large-file revision is unavailable.");
                VerifyLargePatchSource(path, revision);
                long match;
                using (var source = OpenRetainedStream(revision.Payload, MaximumWindowTextSourceBytes,
                    CancellationToken.None, null))
                    match = FindUniqueUtf8Anchor(source, oldBytes, revision.Payload.ByteLength);
                if (match < 0) throw new WorkspaceFileException("patch_not_found", "Patch anchor does not match the accepted source.");
                var afterLength = revision.Payload.ByteLength - oldBytes.Length + newBytes.Length;
                if (afterLength > MaximumWindowTextSourceBytes)
                    throw new WorkspaceFileException("file_too_large", "Patched file exceeds the 80-MiB text source bound.");
                if (oldText == (newText ?? string.Empty))
                    return new WorkspaceFileObservation { RelativePath = relativePath, Reference = expected.Copy(),
                        ContentSha256 = revision.ContentSha256, SourceByteLength = revision.Payload.ByteLength };

                var temporary = path + ".rna-" + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var source = OpenRetainedStream(revision.Payload, MaximumWindowTextSourceBytes,
                        CancellationToken.None, null))
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 81920, FileOptions.SequentialScan))
                    {
                        CopyExactly(source, output, match);
                        SkipExactAnchor(source, oldBytes);
                        output.Write(newBytes, 0, newBytes.Length);
                        source.CopyTo(output);
                        output.Flush(true);
                    }
                    PayloadRef payload;
                    using (var staged = new FileStream(temporary, FileMode.Open, FileAccess.Read,
                        FileShare.Read, 81920, FileOptions.SequentialScan))
                    {
                        if (staged.Length != afterLength)
                            throw new IOException("Staged patch length differs from the intended result.");
                        long length;
                        var digest = ChatBlobStore.HashFile(staged, out length, CancellationToken.None, null);
                        payload = PayloadRef.FromBlob(_blobs.StoreHashedFile(staged, digest, length,
                            "application/octet-stream", CancellationToken.None, null));
                    }
                    var attempt = _journal.Prepare(scope, "files.patch", Identity(id), expected.Revision, payload);
                    var dispatched = false;
                    try
                    {
                        ResolvePath(workspace, relativePath, false, true);
                        VerifyLargePatchSource(path, revision);
                        onDispatch?.Invoke();
                        _journal.MarkDispatchMayHaveOccurred(attempt.AttemptId);
                        dispatched = true;
                        File.Replace(temporary, path, null);
                        FaultPoint?.Invoke("after-dispatch");
                        VerifyLargePatchReadBack(path, payload);
                        var exact = NewRevision(id);
                        _authority.RegisterRevision(scope, new ResourceRevisionMetadata(exact,
                            payload.Sha256, payload, expected));
                        _authority.RegisterView(scope, new ResourceRevisionView(exact,
                            ResourceRepresentations.Raw, payload.Sha256, payload, ResourceCoverage.Whole()));
                        var frozen = _authority.Capture(scope);
                        var current = frozen.GetHead(Identity(id));
                        if (current?.Knowledge != HeadKnowledge.Known || current.Revision.Revision != expected.Revision)
                            throw new WorkspaceFileException("authority_conflict", "File authority changed before publication.");
                        var effect = new ResourceEffect("fx_" + Guid.NewGuid().ToString("N"), "files.patch",
                            ResourceEffectOutcome.VerifiedChanged,
                            new[] { new ResourceImpact(Identity(id), ResourceImpactRelation.Exact,
                                before: expected, after: exact, changeKind: "text-patch") }, "exact streamed read-back");
                        var commit = ResourceAuthorityCommit.Create(scope, frozen.Generation, effect,
                            new[] { new ResourceHeadChange(Identity(id), current,
                                ResourceHeadState.Known(exact, frozen.Generation + 1)) },
                            AuthorityCommitReason.MutationEffect, attempt.AttemptId);
                        _authority.Publish(commit);
                        _journal.Resolve(attempt.AttemptId, commit.CommitId);
                        return new WorkspaceFileObservation { RelativePath = relativePath, Reference = exact,
                            ContentSha256 = payload.Sha256, SourceByteLength = payload.ByteLength,
                            AuthorityCommit = commit };
                    }
                    catch (Exception error)
                    {
                        if (!dispatched) { _journal.AbandonBeforeDispatch(attempt.AttemptId); throw; }
                        throw new WorkspaceFileException("effect_unknown",
                            "Large-file patch or publication is uncertain; reconcile before retrying. " + error.Message);
                    }
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (IOException) { /* Journal outcome must not be hidden by scratch cleanup. */ }
                    catch (UnauthorizedAccessException) { /* Recovery still owns the unresolved effect. */ }
                }
            }
        }

        private static byte[] EncodePatchFragment(string value)
        {
            if (value.Length > MaximumTextBytes)
                throw new WorkspaceFileException("invalid_patch", "Patch fragments must fit one MiB of UTF-8 text.");
            try
            {
                var bytes = StrictUtf8.GetBytes(value);
                if (bytes.Length > MaximumTextBytes)
                    throw new WorkspaceFileException("invalid_patch", "Patch fragments must fit one MiB of UTF-8 text.");
                return bytes;
            }
            catch (EncoderFallbackException)
            { throw new WorkspaceFileException("encoding_ambiguous", "Patch fragments must be valid UTF-8 text."); }
        }

        private static long FindUniqueUtf8Anchor(Stream source, byte[] needle, long expectedLength)
        {
            var prefix = new int[needle.Length];
            var matched = 0;
            for (var i = 1; i < needle.Length; i++)
            {
                while (matched > 0 && needle[i] != needle[matched]) matched = prefix[matched - 1];
                if (needle[i] == needle[matched]) matched++;
                prefix[i] = matched;
            }
            var decoder = StrictUtf8.GetDecoder();
            var buffer = new byte[81920];
            var chars = new char[81922];
            long position = 0, first = -1;
            var state = 0;
            int count;
            try
            {
                while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
                {
                    decoder.GetChars(buffer, 0, count, chars, 0, false);
                    for (var i = 0; i < count; i++, position++)
                    {
                        while (state > 0 && buffer[i] != needle[state]) state = prefix[state - 1];
                        if (buffer[i] == needle[state]) state++;
                        if (state != needle.Length) continue;
                        if (first >= 0)
                            throw new WorkspaceFileException("patch_ambiguous", "Patch anchor has multiple starts; use a unique larger anchor.");
                        first = position - needle.Length + 1;
                        state = prefix[state - 1];
                    }
                }
                decoder.GetChars(Array.Empty<byte>(), 0, 0, chars, 0, true);
            }
            catch (DecoderFallbackException)
            { throw new WorkspaceFileException("encoding_ambiguous", "Only strict UTF-8 text is supported."); }
            if (position != expectedLength) throw new IOException("Retained source length changed during patch preparation.");
            return first;
        }

        private static void CopyExactly(Stream input, Stream output, long count)
        {
            var buffer = new byte[81920];
            while (count > 0)
            {
                var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                if (read == 0) throw new IOException("Retained source ended during patch preparation.");
                output.Write(buffer, 0, read);
                count -= read;
            }
        }

        private static void SkipExactAnchor(Stream input, byte[] anchor)
        {
            var buffer = new byte[81920];
            var offset = 0;
            while (offset < anchor.Length)
            {
                var count = input.Read(buffer, 0, Math.Min(buffer.Length, anchor.Length - offset));
                if (count == 0) throw new IOException("Retained anchor ended during patch preparation.");
                for (var i = 0; i < count; i++)
                    if (buffer[i] != anchor[offset + i]) throw new IOException("Retained anchor changed during patch preparation.");
                offset += count;
            }
        }

        private static void VerifyLargePatchSource(string path, ResourceRevisionMetadata revision)
        {
            using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.SequentialScan))
            {
                if (source.Length != revision.Payload.ByteLength)
                    throw new WorkspaceFileException("target_conflict", "File changed since the accepted read.");
                long length;
                if (ChatBlobStore.HashFile(source, out length, CancellationToken.None, null) != revision.ContentSha256)
                    throw new WorkspaceFileException("target_conflict", "File changed since the accepted read.");
            }
        }

        private static void VerifyLargePatchReadBack(string path, PayloadRef payload)
        {
            using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.SequentialScan))
            {
                if (source.Length != payload.ByteLength)
                    throw new WorkspaceFileException("read_back_mismatch", "Patched file length differs from prepared bytes.");
                long length;
                if (ChatBlobStore.HashFile(source, out length, CancellationToken.None, null) != payload.Sha256)
                    throw new WorkspaceFileException("read_back_mismatch", "Patched file does not match prepared bytes.");
            }
        }
    }
}
