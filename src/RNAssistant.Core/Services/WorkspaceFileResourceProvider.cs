using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
