using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                catch (FileNotFoundException) { result.Partial = true; }
                catch (DirectoryNotFoundException) { result.Partial = true; }
                catch (WorkspaceFileException) { result.Partial = true; }
                catch (UnauthorizedAccessException) { result.Partial = true; }
            }
            if (result.Partial) result.UnavailableScopes.Add("workspace");
            result.Total = result.Items.Count;
            result.Complete = !listed.Truncated && !result.Partial;
            result.Empty = result.Items.Count == 0 && result.Complete;
            result.RefineQuery = listed.Truncated;
            if (listed.Truncated) result.AvailabilityHint = "Directory scan was truncated; narrow directory or filename query.";
            else if (result.Partial) result.AvailabilityHint = "Some entries changed or were unavailable during discovery; refresh the affected directory.";
            return result;
        }

        public ResourceReadObservation Read(string relativePath)
        {
            return Project(_files.ReadText(_workspace, relativePath), false);
        }

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
            var descriptor = new ResourceDescriptor { Reference = observed.Reference,
                Provider = Id, Kind = "file", Title = Path.GetFileName(observed.RelativePath),
                Mutable = !retained && !_workspace.ReadOnly, Tracking = retained ? "snapshot" : "current",
                ByteLength = observed.Evidence.Payload.ByteLength,
                ContentSha256 = observed.ContentSha256, Coverage = ResourceCoverage.Whole() };
            descriptor.Representations.Add(ResourceRepresentations.Metadata);
            descriptor.Representations.Add(ResourceRepresentations.Text);
            descriptor.Metadata["relativePath"] = observed.RelativePath;
            var result = new ResourceReadResult { Resource = descriptor,
                Representation = ResourceRepresentations.Text, Text = observed.Text,
                ContentSha256 = observed.ContentSha256, Coverage = ResourceCoverage.Whole(),
                Complete = true, ReturnedCharacters = observed.Text.Length,
                TotalCharacters = observed.Text.Length, RawContentIncluded = true,
                CompleteViewPayload = observed.Evidence.Payload };
            return new ResourceReadObservation(result, new[] { observed.Evidence });
        }

        public ResourceAuthoritySnapshotSet CaptureAuthority(IEnumerable<ResourceEvidence> evidence)
        {
            return _files.CaptureAuthority(evidence);
        }
    }
}
