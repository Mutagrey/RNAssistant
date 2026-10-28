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
        public void ValidateTextEdits(WorkspaceDescriptor workspace, string path, ResourceRef expected,
            IReadOnlyList<TextEditHunk> hunks, CancellationToken token, ResourceEvidence observed = null)
        {
            var snapshot = AcceptedStreamedTextSource(workspace, path, expected);
            using (var stream = OpenRetainedStream(snapshot.Evidence.Payload, long.MaxValue, token, null, true))
            {
                var edits = PlanByteEdits(stream, hunks, token);
                if (observed?.Coverage.Kind == ResourceCoverageKinds.CharacterRange)
                {
                    var start = CharacterByteOffset(stream, observed.Coverage.Start ?? 0, token);
                    var end = CharacterByteOffset(stream, observed.Coverage.End ?? 0, token);
                    if (edits.Any(edit => edit.Offset < start || edit.Offset + edit.Before.Length > end))
                        throw new TextPatternException("source_observation_required", "Hunk coordinates must stay inside the observed source excerpt.");
                }
            }
        }
        public WorkspaceFileObservation ApplyTextEdits(WorkspaceDescriptor workspace, string path,
            ResourceRef expected, IReadOnlyList<TextEditHunk> hunks, Action onDispatch = null,
            CancellationToken token = default(CancellationToken), string effectOperation = null)
        {
            // One staged commit, even for many hunks. Source and result are streamed; memory follows patch size.
            var snapshot = AcceptedStreamedTextSource(workspace, path, expected);
            using (var stream = OpenRetainedStream(snapshot.Evidence.Payload, long.MaxValue, token, null, true))
                return CommitTextEdits(workspace, path, expected, stream, PlanByteEdits(stream, hunks, token), onDispatch, token, effectOperation);
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
        private static IReadOnlyList<ByteEdit> PlanByteEdits(Stream source, IReadOnlyList<TextEditHunk> hunks, CancellationToken token)
        {
            if (hunks == null || hunks.Count == 0 || hunks.Count > 100) throw new TextPatternException("patch_invalid", "Use 1–100 hunks.");
            var edits = new List<ByteEdit>();
            foreach (var hunk in hunks)
            {
                token.ThrowIfCancellationRequested();
                if (hunk?.OldText == null || hunk.NewText == null) throw new TextPatternException("patch_invalid", "oldText and newText are required.");
                var context = EncodePatchFragment(hunk.ContextBefore ?? "");
                var before = EncodePatchFragment(hunk.OldText); var after = EncodePatchFragment(hunk.NewText);
                var block = EncodePatchFragment((hunk.ContextBefore ?? "") + hunk.OldText + (hunk.ContextAfter ?? ""));
                long offset;
                source.Position = 0;
                if (hunk.Boundary != null)
                {
                    TextEditPlan.Patch("", new[] { hunk }, token); // shared boundary validation
                    // The UTF-8 signature stays at byte zero when inserting at the start of content.
                    offset = hunk.Boundary == "start"
                        ? (source.ReadByte() == 0xef && source.ReadByte() == 0xbb && source.ReadByte() == 0xbf ? 3 : 0)
                        : source.Length;
                }
                else if (hunk.StartLine.HasValue || hunk.StartColumn.HasValue)
                {
                    offset = CoordinateByteOffset(source, hunk.StartLine ?? 0, hunk.StartColumn ?? 1, token);
                    if (block.Length == 0 || offset < context.Length) throw new TextPatternException("patch_not_found", "The exact coordinate/context does not match.");
                    source.Position = offset - context.Length;
                    try { SkipExactAnchor(source, block); }
                    catch (IOException) { throw new TextPatternException("patch_not_found", "The exact coordinate/context does not match."); }
                }
                else
                {
                    if (block.Length == 0) throw new TextPatternException("patch_invalid", "Insertion requires context or boundary.");
                    var match = FindUniqueUtf8Anchor(source, block, source.Length, token);
                    if (match < 0) throw new TextPatternException("patch_not_found", "The exact hunk was not found.");
                    offset = match + context.Length;
                }
                edits.Add(new ByteEdit { Offset = offset, Before = before, After = after });
            }
            edits.Sort((a,b) => a.Offset.CompareTo(b.Offset));
            for (var index = 1; index < edits.Count; index++)
                if (edits[index].Offset == edits[index-1].Offset || edits[index].Offset < edits[index-1].Offset + edits[index-1].Before.Length)
                    throw new TextPatternException("patch_overlap", "Hunks overlap in the original source.");
            return edits;
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
        private static long CoordinateByteOffset(Stream source, int line, int column, CancellationToken token)
        {
            if (line < 1 || column < 1) throw new TextPatternException("patch_location_invalid", "Coordinates are 1-based UTF-16 positions.");
            source.Position = 0;
            using (var reader = new StreamReader(source, StrictUtf8, false, 81920, true))
            {
                int currentLine = 1, currentColumn = 1; long bytes = 0;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (currentLine == line && currentColumn == column) return bytes;
                    var c = reader.Read();
                    if (c < 0) break;
                    if (c == '\r' || c == '\n')
                    {
                        if (currentLine == line) break;
                        bytes++; if (c == '\r' && reader.Peek() == '\n') { reader.Read(); bytes++; }
                        currentLine++; currentColumn = 1;
                    }
                    else if (char.IsHighSurrogate((char)c))
                    {
                        if (currentLine == line && column == currentColumn + 1) break;
                        var low = reader.Read(); if (low < 0 || !char.IsLowSurrogate((char)low)) break;
                        bytes += 4; currentColumn += 2;
                    }
                    else { bytes += c < 128 ? 1 : c < 2048 ? 2 : 3; currentColumn++; }
                }
            }
            throw new TextPatternException("patch_location_invalid", "Coordinate is outside the source or splits a Unicode scalar.");
        }
    }
}
