using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Core.Services
{
    // A workspace-bound filesystem provider. WorkspaceFileService remains the only
    // filesystem reader, writer, authority observer and mutation journal owner.
    public sealed class WorkspaceFileResourceProvider : IResourceProviderIdentity
    {
        private readonly WorkspaceFileService _files;
        private readonly WorkspaceDescriptor _workspace;

        public string Id { get { return "file"; } }

        public WorkspaceFileResourceProvider(WorkspaceFileService files, WorkspaceDescriptor workspace)
        {
            _files = files ?? throw new ArgumentNullException(nameof(files));
            _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        }

        public ResourceFindPage Find(string directory, string query, int limit = 200)
        {
            var listed = _files.ListPage(_workspace, directory, query, limit);
            var result = new ResourceFindPage { Scope = "workspace", Directory = directory ?? string.Empty,
                Query = string.IsNullOrWhiteSpace(query) ? null : query,
                ScannedEntries = listed.ScannedEntries };
            foreach (var name in listed.Names)
            {
                var target = string.IsNullOrEmpty(directory) ? name :
                    directory.TrimEnd('/', '\\') + "/" + name;
                try
                {
                    var descriptor = _files.Describe(_workspace, target);
                    result.Items.Add(new ResourceFindCandidate {
                        Target = target, Title = descriptor.Title, Type = descriptor.Kind,
                        Scope = "workspace", Mutable = descriptor.Mutable,
                        ByteLength = descriptor.ByteLength,
                        Representations = descriptor.Representations.ToList(),
                        Usage = descriptor.Kind == "directory"
                            ? "Browse this target as a directory; it has no text body."
                            : "Read this target for exact content before editing.",
                        Reference = descriptor.Reference, Descriptor = descriptor });
                    result.ResourceRefs.Add(descriptor.Reference);
                }
                catch (FileNotFoundException) { if (!result.Partial) result.UnavailableScopes.Add("workspace"); }
                catch (DirectoryNotFoundException) { if (!result.Partial) result.UnavailableScopes.Add("workspace"); }
                catch (WorkspaceFileException) { if (!result.Partial) result.UnavailableScopes.Add("workspace"); }
                catch (UnauthorizedAccessException) { if (!result.Partial) result.UnavailableScopes.Add("workspace"); }
            }
            result.Total = result.Items.Count;
            result.Complete = !listed.Truncated && !result.Partial;
            if (listed.Truncated) result.AvailabilityHint = "Directory scan was truncated; narrow directory or filename query.";
            else if (result.Partial) result.AvailabilityHint = "Some entries changed or were unavailable during discovery; refresh the affected directory.";
            return result;
        }

        public ResourceReadObservation Read(string relativePath, int? startLine = null, int? lineCount = null,
            CancellationToken token = default(CancellationToken),
            int maximumCharacters = WorkspaceFileService.MaximumWindowTextCharacters)
        {
            if (startLine.HasValue != lineCount.HasValue)
                throw new WorkspaceFileException("resource_lines_invalid", "Supply startLine and lineCount together.");
            return Project(startLine.HasValue ? _files.ReadTextLines(_workspace, relativePath, startLine.Value, lineCount.Value,
                    token, maximumCharacters)
                : _files.ReadText(_workspace, relativePath, token), false);
        }

        public ResourceReadObservation ReadModelText(string relativePath, int maximumCharacters,
            CancellationToken token = default(CancellationToken))
        { return Project(_files.ReadModelText(_workspace, relativePath, maximumCharacters, token), false); }

        public ResourceReadObservation ReadCharacters(string relativePath, int startChar, int charCount,
            CancellationToken token = default(CancellationToken),
            int maximumCharacters = WorkspaceFileService.MaximumWindowTextCharacters)
        { return Project(_files.ReadTextCharacters(_workspace, relativePath, startChar, charCount,
            token, maximumCharacters), false); }

        public ResourceReadObservation ReadProcedure(string relativePath, string name, string kind,
            int maximumCharacters, CancellationToken token = default(CancellationToken))
        {
            ResourceProcedureSelection procedure;
            var read = Project(_files.ReadVbaProcedure(_workspace, relativePath, name, kind,
                maximumCharacters, out procedure, token), false);
            read.Result.Procedure = procedure;
            read.Result.SourceComplete = false;
            return read;
        }

        public ResourceReadObservation ReadTree(string relativePath, string pointer, int depth,
            int offset, int limit, int maximumCharacters, CancellationToken token = default(CancellationToken))
        {
            if (string.IsNullOrEmpty(relativePath))
                throw new WorkspaceFileException("resource_tree_view_unsupported", "Tree requires a JSON or YAML file.");
            var yaml = relativePath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                relativePath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);
            if (!yaml && !relativePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                throw new WorkspaceFileException("resource_tree_view_unsupported", "Tree requires a JSON or YAML file.");
            var snapshot = _files.CaptureFile(_workspace, relativePath, long.MaxValue, token);
            ResourceTree tree;
            try
            {
                using (var input = _files.OpenExactRead(_workspace, relativePath, snapshot.Reference, long.MaxValue, token))
                using (var reader = new StreamReader(input, new UTF8Encoding(false, true), false, 81920))
                    tree = yaml
                        ? ResourceYamlTreeSelector.Select(reader, pointer, depth, offset, limit, maximumCharacters, token)
                        : ResourceJsonTreeSelector.Select(reader, pointer, depth, offset, limit, maximumCharacters, token);
            }
            catch (DecoderFallbackException)
            { throw new WorkspaceFileException("encoding_ambiguous", "Only strict UTF-8 JSON/YAML is supported."); }
            var body = Newtonsoft.Json.JsonConvert.SerializeObject(tree);
            var coverage = tree.Coverage();
            var evidence = _files.PublishTreeView(snapshot, body, coverage);
            var descriptor = new ResourceDescriptor { Reference = snapshot.Reference, Provider = Id,
                Kind = "file", Title = Path.GetFileName(relativePath), Mutable = !_workspace.ReadOnly,
                Tracking = "current", ByteLength = snapshot.ByteLength,
                ContentSha256 = snapshot.ContentSha256, Coverage = coverage,
                MimeType = yaml ? "application/yaml" : "application/json" };
            descriptor.Representations.Add(ResourceRepresentations.Metadata);
            descriptor.Representations.Add(ResourceRepresentations.Text);
            descriptor.Representations.Add(ResourceRepresentations.Tree);
            descriptor.Metadata["relativePath"] = relativePath;
            var result = new ResourceReadResult { Resource = descriptor, Representation = ResourceRepresentations.Tree,
                Tree = tree, Text = body, Payload = evidence.Payload, ContentSha256 = snapshot.ContentSha256,
                Coverage = coverage, Complete = false, SourceComplete = false, Truncated = true,
                ReturnedCharacters = body.Length, TotalCharacters = body.Length };
            return new ResourceReadObservation(result, new[] { evidence });
        }

        public ResourceReadObservation ReadPdfPageText(string relativePath, int pageIndex,
            int startChar, int charCount, CancellationToken token = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(relativePath) ||
                !relativePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
                pageIndex < 0 || startChar < 0 || charCount < 1)
                throw new WorkspaceFileException("resource_page_selector_invalid",
                    "PDF page text requires a .pdf file, pageIndex and a bounded character window.");
            var snapshot = _files.CaptureFile(_workspace, relativePath, long.MaxValue, token);
            PdfAttachmentPageText page;
            try
            {
                using (var input = _files.OpenExactRead(_workspace, relativePath, snapshot.Reference,
                    long.MaxValue, token, requireSeek: true))
                    page = PdfAttachmentTextExtractor.ExtractPage(input, pageIndex);
            }
            catch (ArgumentOutOfRangeException)
            { throw new WorkspaceFileException("resource_page_selector_invalid", "pageIndex is outside the PDF's exact page bounds."); }
            catch (Exception error) when (!(error is OperationCanceledException) &&
                                          !(error is OutOfMemoryException) &&
                                          !(error is WorkspaceFileException))
            { throw new WorkspaceFileException("resource_page_unavailable", "The exact PDF page text could not be extracted: " + error.Message); }
            token.ThrowIfCancellationRequested();
            int end;
            string selected;
            try { selected = page.SelectWindow(startChar, charCount, out end); }
            catch (ArgumentException error)
            { throw new WorkspaceFileException("resource_page_selector_invalid", error.Message); }
            var coverage = new ResourceCoverage(ResourceCoverageKinds.CharacterRange,
                address: "pdf-page:" + pageIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                start: startChar, end: end);
            var evidence = _files.PublishPdfPageText(snapshot, pageIndex, selected, coverage);
            var descriptor = new ResourceDescriptor { Reference = snapshot.Reference, Provider = Id,
                Kind = "file", Title = Path.GetFileName(relativePath), Mutable = !_workspace.ReadOnly,
                Tracking = "current", ByteLength = snapshot.ByteLength,
                ContentSha256 = snapshot.ContentSha256, Coverage = coverage,
                MimeType = "application/pdf" };
            descriptor.Representations.Add(ResourceRepresentations.Metadata);
            descriptor.Representations.Add(ResourceRepresentations.Text);
            descriptor.Metadata["relativePath"] = relativePath;
            var result = new ResourceReadResult { Resource = descriptor,
                Representation = ResourceRepresentations.Text, Text = selected,
                ContentSha256 = snapshot.ContentSha256, Payload = evidence.Payload,
                RetainedViewKey = PdfAttachmentTextExtractor.PageViewProfile + ":" +
                    pageIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ":" + startChar.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ":" + end.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Coverage = coverage, SourceCoverage = startChar == 0 && end == page.Text.Length
                    ? new ResourceCoverage(ResourceCoverageKinds.PageRange,
                        start: pageIndex, end: (long)pageIndex + 1)
                    : null,
                PageIndex = pageIndex, PageCount = page.PageCount, Offset = startChar,
                ReturnedCharacters = selected.Length, TotalCharacters = page.Text.Length,
                Complete = false, SourceComplete = false, Truncated = true, RawContentIncluded = true };
            return new ResourceReadObservation(result, new[] { evidence });
        }

        public ResourceReadObservation ReadPdfPageRangeText(string relativePath, int startPage,
            int requestedPages, int maximumCharacters, CancellationToken token = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(relativePath) ||
                !relativePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
                startPage < 0 || requestedPages < 1 || maximumCharacters < 1)
                throw new WorkspaceFileException("resource_page_selector_invalid",
                    "PDF page ranges require a .pdf file, startPage and positive pageCount.");
            var snapshot = _files.CaptureFile(_workspace, relativePath, long.MaxValue, token);
            PdfAttachmentPageRangeText pages;
            try
            {
                using (var input = _files.OpenExactRead(_workspace, relativePath, snapshot.Reference,
                    long.MaxValue, token, requireSeek: true))
                    pages = PdfAttachmentTextExtractor.ExtractRange(input, startPage, requestedPages, maximumCharacters);
            }
            catch (ArgumentOutOfRangeException)
            { throw new WorkspaceFileException("resource_page_selector_invalid", "startPage is outside the PDF's exact page bounds."); }
            catch (PdfPageWindowRequiredException error)
            { throw new WorkspaceFileException("resource_page_window_required", error.Message); }
            catch (Exception error) when (!(error is OperationCanceledException) &&
                                          !(error is OutOfMemoryException) &&
                                          !(error is WorkspaceFileException))
            { throw new WorkspaceFileException("resource_page_unavailable", "The exact PDF page range could not be extracted: " + error.Message); }
            token.ThrowIfCancellationRequested();
            var coverage = new ResourceCoverage(ResourceCoverageKinds.PageRange,
                start: startPage, end: pages.EndPage);
            var evidence = _files.PublishPdfPageRangeText(snapshot, pages.Text, coverage);
            var descriptor = new ResourceDescriptor { Reference = snapshot.Reference, Provider = Id,
                Kind = "file", Title = Path.GetFileName(relativePath), Mutable = !_workspace.ReadOnly,
                Tracking = "current", ByteLength = snapshot.ByteLength,
                ContentSha256 = snapshot.ContentSha256, Coverage = coverage,
                MimeType = "application/pdf" };
            descriptor.Representations.Add(ResourceRepresentations.Metadata);
            descriptor.Representations.Add(ResourceRepresentations.Text);
            descriptor.Metadata["relativePath"] = relativePath;
            var result = new ResourceReadResult { Resource = descriptor,
                Representation = ResourceRepresentations.Text, Text = pages.Text,
                ContentSha256 = snapshot.ContentSha256, Payload = evidence.Payload,
                RetainedViewKey = PdfAttachmentTextExtractor.RangeViewProfile + ":" +
                    startPage.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
                    pages.EndPage.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Coverage = coverage, SourceCoverage = coverage, PageCount = pages.PageCount,
                ReturnedCharacters = pages.Text.Length, TotalCharacters = pages.Text.Length,
                Complete = false, SourceComplete = false, Truncated = true, RawContentIncluded = true };
            return new ResourceReadObservation(result, new[] { evidence });
        }

        public ResourceReadObservation ReadViewCharacters(string relativePath, int startChar, int charCount,
            ResourceRef exact = null, CancellationToken token = default(CancellationToken))
        { return Project(_files.ReadTextViewCharacters(_workspace, relativePath, startChar, charCount, exact, token), exact != null); }

        public ResourceReadObservation ReadBytes(string relativePath)
        { return ProjectBytes(_files.ReadBytes(_workspace, relativePath), false); }

        public ResourceReadObservation ReadExactBytes(string relativePath, ResourceRef exact)
        { return ProjectBytes(_files.ReadExactBytes(_workspace, relativePath, exact), true); }

        public ResourceReadObservation ReadSnapshotBytes(RetainedWebSnapshot snapshot, string relativePath)
        { return ProjectBytes(_files.ReadSnapshotBytes(_workspace, snapshot, relativePath), true); }

        public ResourceDescriptor Describe(string relativePath)
        { return _files.Describe(_workspace, relativePath); }

        // Runtime-only retained read. This does not observe or republish the live head;
        // callers assess currentness separately against frozen authority.
        public ResourceReadObservation ReadExact(string relativePath, ResourceRef exact)
        {
            return Project(_files.ReadExactText(_workspace, relativePath, exact), true);
        }

        public ResourceReadObservation ReadSnapshot(RetainedWebSnapshot snapshot, string relativePath)
        {
            return Project(_files.ReadSnapshotText(_workspace, snapshot, relativePath), true);
        }

        private ResourceReadObservation Project(WorkspaceFileObservation observed, bool retained)
        {
            var descriptor = Descriptor(observed, retained);
            descriptor.Representations.Add(ResourceRepresentations.Text);
            var result = new ResourceReadResult { Resource = descriptor,
                Representation = ResourceRepresentations.Text, Text = observed.Text, Bytes = observed.Bytes,
                ContentSha256 = observed.ContentSha256, Coverage = observed.Evidence.Coverage,
                Complete = observed.Evidence.Complete, SourceComplete = true, Offset = observed.Offset,
                ReturnedCharacters = observed.Text.Length,
                TotalCharacters = observed.TotalCharacters ?? observed.Text.Length, RawContentIncluded = true,
                CompleteViewPayload = observed.Evidence.Complete ? observed.Evidence.Payload : null };
            return new ResourceReadObservation(result, new[] { observed.Evidence });
        }

        private ResourceDescriptor Descriptor(WorkspaceFileObservation observed, bool retained)
        {
            var descriptor = new ResourceDescriptor { Reference = observed.Reference,
                Provider = Id, Kind = "file", Title = Path.GetFileName(observed.RelativePath),
                Mutable = !retained && !_workspace.ReadOnly, Tracking = retained ? "snapshot" : "current",
                ByteLength = observed.SourceByteLength ?? observed.Evidence.Payload.ByteLength,
                ContentSha256 = observed.ContentSha256, Coverage = ResourceCoverage.Whole() };
            descriptor.Representations.Add(ResourceRepresentations.Metadata);
            descriptor.Metadata["relativePath"] = observed.RelativePath;
            return descriptor;
        }

        private ResourceReadObservation ProjectBytes(WorkspaceFileObservation observed, bool retained)
        {
            var descriptor = Descriptor(observed, retained);
            var image = RasterImageHeader.Read(observed.Bytes);
            descriptor.MimeType = image?.MimeType ?? "application/octet-stream";
            descriptor.Representations.Add(ResourceRepresentations.Raw);
            if (image != null) descriptor.Representations.Add(ResourceRepresentations.Image);
            var result = new ResourceReadResult { Resource = descriptor,
                Representation = observed.Evidence.View, Bytes = observed.Bytes,
                ContentSha256 = observed.ContentSha256, Coverage = ResourceCoverage.Whole(),
                Complete = true, RawContentIncluded = true,
                Binary = new ResourceBinaryView { Payload = observed.Evidence.Payload,
                    Width = image?.Width ?? 0, Height = image?.Height ?? 0 },
                CompleteViewPayload = observed.Evidence.Payload };
            return new ResourceReadObservation(result, new[] { observed.Evidence });
        }

        public ResourceAuthoritySnapshotSet CaptureAuthority(IEnumerable<ResourceEvidence> evidence)
        {
            return _files.CaptureAuthority(evidence);
        }
    }
}
