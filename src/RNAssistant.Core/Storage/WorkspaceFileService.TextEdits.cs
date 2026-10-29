using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    public sealed partial class WorkspaceFileService
    {
        private sealed class ByteEdit
        {
            internal long Offset;
            internal byte[] Before, After;
        }
        public List<PatchPreview> ValidateTextEdits(WorkspaceDescriptor workspace, string path, ResourceRef expected,
            string patch, CancellationToken token, ResourceEvidence observed = null)
        {
            var snapshot = AcceptedStreamedTextSource(workspace, path, expected);
            using (var stream = OpenRetainedStream(snapshot.Evidence.Payload, long.MaxValue, token, null, true))
            {
                var parsed = TextPatch.Parse(patch);
                if (parsed.RequiresWholeSource && !(observed?.Complete == true && observed.Coverage.Kind == ResourceCoverageKinds.Whole))
                    throw new TextPatternException("source_observation_required", "A context-free insertion requires complete observed source.");
                var edits = PlanByteEdits(stream, patch, token);
                if (observed?.Coverage.Kind == ResourceCoverageKinds.CharacterRange)
                {
                    var start = CharacterByteOffset(stream, observed.Coverage.Start ?? 0, token);
                    var end = CharacterByteOffset(stream, observed.Coverage.End ?? 0, token);
                    if (edits.Any(edit => edit.Offset < start || edit.Offset + edit.Before.Length > end))
                        throw new TextPatternException("source_observation_required", "Changed lines must stay inside the observed source excerpt.");
                }
                return PatchPreview.From(edits.Select(e => new TextPatchEdit {
                    Before = StrictUtf8.GetString(e.Before), After = StrictUtf8.GetString(e.After) }));
            }
        }
        public WorkspaceFileObservation ApplyTextEdits(WorkspaceDescriptor workspace, string path,
            ResourceRef expected, string patch, Action onDispatch = null,
            CancellationToken token = default(CancellationToken), string effectOperation = ResourceMutationToolCatalog.Patch)
        {
            // One staged commit, even for many hunks. Source and result are streamed; memory follows patch size.
            var snapshot = AcceptedStreamedTextSource(workspace, path, expected);
            using (var stream = OpenRetainedStream(snapshot.Evidence.Payload, long.MaxValue, token, null, true))
                return CommitTextEdits(workspace, path, expected, stream, PlanByteEdits(stream, patch, token), onDispatch, token, effectOperation);
        }
        public WorkspaceFileObservation ReplaceMatches(WorkspaceDescriptor workspace, string path, ResourceRef expected,
            string query, string replacement, string mode, bool matchCase, int count,
            Action onDispatch, CancellationToken token, bool validateOnly = false, string effectOperation = null)
        {
            var snapshot = AcceptedStreamedTextSource(workspace, path, expected);
            using (var stream = OpenRetainedStream(snapshot.Evidence.Payload, long.MaxValue, token, null, true))
            {
                var text = new StringBuilder(); var buffer = new char[81920];
                using (var reader = new StreamReader(stream, StrictUtf8, false, 81920, true))
                {
                    int read;
                    while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        if (text.Length + read > ResourceTextSearch.MaximumScanCharacters)
                            throw new TextPatternException("replacement_limit_exceeded", "Source exceeds the complete search budget; use core.patch hunks.");
                        text.Append(buffer, 0, read);
                    }
                }
                var source = text.ToString();
                var plan = TextEditPlan.Replace(source, query, replacement, mode, matchCase, count, token);
                if (validateOnly) return null;
                var edits = new List<ByteEdit>(); long offset = 0; var position = 0;
                foreach (var edit in plan.Edits)
                {
                    offset += TextByteLength(source.Substring(position, edit.Offset - position));
                    var before = EncodePatchFragment(edit.Before);
                    edits.Add(new ByteEdit { Offset = offset, Before = before, After = EncodePatchFragment(edit.After) });
                    position = edit.Offset + edit.Before.Length; offset += before.Length;
                }
                return CommitTextEdits(workspace, path, expected, stream, edits, onDispatch, token, effectOperation);
            }
        }
        private WorkspaceFileObservation CommitTextEdits(WorkspaceDescriptor workspace, string path,
            ResourceRef expected, Stream source, IReadOnlyList<ByteEdit> edits, Action onDispatch, CancellationToken token, string effectOperation)
        {
            var stagedPath = Path.Combine(Path.GetTempPath(), "rna-hunks-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var staged = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    source.Position = 0;
                    foreach (var edit in edits)
                    {
                        token.ThrowIfCancellationRequested();
                        CopyExactly(source, staged, edit.Offset - source.Position, token);
                        SkipExactAnchor(source, edit.Before);
                        staged.Write(edit.After, 0, edit.After.Length);
                    }
                    CopyExactly(source, staged, source.Length - source.Position, token); staged.Flush(true);
                }
                var saved = CommitStagedFileCore(workspace, path, stagedPath, expected, long.MaxValue, onDispatch, token, true, effectOperation: effectOperation);
                // Bounded read-back may be reused as source evidence. A huge file retains only exact metadata.
                if (saved.ByteLength <= MaximumTextBytes)
                {
                    // Evidence is read from the verified immutable preimage, never from a newer live file.
                    var revision = _authority.GetRevision(saved.Evidence.ScopeId, saved.Reference);
                    var text = _blobs.ReadText(revision.Payload.ToBlobReference());
                    _authority.RegisterView(saved.Evidence.ScopeId, new ResourceRevisionView(saved.Reference,
                        ResourceRepresentations.Text, saved.ContentSha256, revision.Payload, ResourceCoverage.Whole()));
                    return new WorkspaceFileObservation { RelativePath = path, Text = text, Reference = saved.Reference,
                        ContentSha256 = saved.ContentSha256, SourceByteLength = saved.ByteLength, AuthorityCommit = saved.AuthorityCommit,
                        Evidence = new ResourceEvidence("ev_" + Guid.NewGuid().ToString("N"), saved.Evidence.ScopeId,
                            saved.Reference, ResourceRepresentations.Text, ResourceCoverage.Whole(), true,
                            saved.Evidence.AuthorityGeneration, revision.Payload, contentSha256: saved.ContentSha256) };
                }
                return new WorkspaceFileObservation { RelativePath = path, Reference = saved.Reference,
                    ContentSha256 = saved.ContentSha256, SourceByteLength = saved.ByteLength, AuthorityCommit = saved.AuthorityCommit };
            }
            finally
            {
                try { if (File.Exists(stagedPath)) File.Delete(stagedPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        private static IReadOnlyList<ByteEdit> PlanByteEdits(Stream source, string patch, CancellationToken token)
        {
            source.Position = 0;
            IReadOnlyList<TextPatchEdit> resolved;
            using (var reader = new StreamReader(source, StrictUtf8, false, 81920, true))
                resolved = TextPatch.Parse(patch).Resolve(reader, token);
            return resolved.Select(edit => new ByteEdit {
                Offset = CharacterByteOffset(source, edit.Offset, token),
                Before = EncodePatchFragment(edit.Before), After = EncodePatchFragment(edit.After)
            }).ToList();
        }
        public IReadOnlyList<TextPatchEdit> ReadRetainedPatchEdits(ResourceRef before, ResourceRef after, string patch, int maximumCharacters)
        {
            var evidence = PublishedEvidence(before, ResourceRepresentations.Raw);
            using (var stream = OpenRetainedStream(evidence.Payload, long.MaxValue, CancellationToken.None, null, true))
            using (var reader = new StreamReader(stream, StrictUtf8, false, 81920, true))
            {
                var edits = TextPatch.Parse(patch).Resolve(reader);
                if (edits.Sum(e => (long)e.Before.Length + e.After.Length) > maximumCharacters)
                    throw new WorkspaceFileException("file_too_large", "Retained patch fragments exceed the display budget.");
                var result = PublishedEvidence(after, ResourceRepresentations.Raw);
                using (var afterStream = OpenRetainedStream(result.Payload, long.MaxValue, CancellationToken.None, null, true))
                using (var afterReader = new StreamReader(afterStream, StrictUtf8, false))
                    return TextPatch.ReadAfterFragments(afterReader, edits);
            }
        }
        private static long CharacterByteOffset(Stream source, long characters, CancellationToken token)
        {
            source.Position = 0; long position = 0, bytes = 0;
            using (var reader = new StreamReader(source, StrictUtf8, false, 81920, true))
            {
                while (position < characters)
                {
                    if ((position & 4095) == 0) token.ThrowIfCancellationRequested();
                    var c = reader.Read(); if (c < 0) throw new TextPatternException("patch_location_invalid", "Excerpt is outside the source.");
                    position++;
                    if (char.IsHighSurrogate((char)c))
                    {
                        if (position == characters || !char.IsLowSurrogate((char)reader.Read())) throw new TextPatternException("patch_location_invalid", "Excerpt splits a Unicode scalar.");
                        position++; bytes += 4;
                    }
                    else bytes += c < 128 ? 1 : c < 2048 ? 2 : 3;
                }
            }
            return bytes;
        }
        private static byte[] EncodePatchFragment(string value)
        {
            try
            {
                return StrictUtf8.GetBytes(value);
            }
            catch (EncoderFallbackException)
            { throw new WorkspaceFileException("encoding_ambiguous", "Patch fragments must be valid UTF-8 text."); }
        }

        private static void CopyExactly(Stream input, Stream output, long count, CancellationToken token = default(CancellationToken))
        {
            var buffer = new byte[81920];
            while (count > 0)
            {
                token.ThrowIfCancellationRequested();
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

    }
}
