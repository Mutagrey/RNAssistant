using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Runtime
{
    public sealed partial class WorkspaceConversationService
    {
        private ResourceProviderRouter<WorkspaceFileResourceProvider> FileResources(WorkspaceDescriptor workspace)
        { return new ResourceProviderRouter<WorkspaceFileResourceProvider>(new[] { new WorkspaceFileResourceProvider(_files, workspace) }); }

        public WorkspaceResourcePage FindResources(WorkspaceDescriptor workspace, WorkspaceFindResourcesRequest request)
        {
            if (request == null || request.Limit < 1 || request.Limit > 200)
                throw new ArgumentException("Resource discovery requires a limit from 1 to 200.");
            var page = FileResources(workspace).Select("file").Find(request.Directory, request.Query, request.Limit);
            return new WorkspaceResourcePage { Directory = page.Directory, Query = page.Query,
                Items = page.Items.Select(item => item.Descriptor).ToArray(), Complete = page.Complete,
                ScannedEntries = page.ScannedEntries, AvailabilityHint = page.AvailabilityHint,
                UnavailableScopes = page.UnavailableScopes.ToArray() };
        }

        public WorkspaceResourceView OpenResourceView(WorkspaceDescriptor workspace, WorkspaceOpenResourceRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Target))
                throw new ArgumentException("A workspace-relative resource target is required.");
            if (request.Representation != ResourceRepresentations.Text && request.Representation != ResourceRepresentations.Metadata)
                throw new ArgumentException("Supported resource views: metadata, text.");
            if (request.Offset < 0 || request.MaxCharacters < ResourceReadRequest.MinimumCharacters ||
                request.MaxCharacters > ResourceReadRequest.MaximumCharacters)
                throw new ArgumentException("Resource offset must be nonnegative and max characters must be 128–32000.");
            if (request.Exact != null && (!request.Exact.IsExact || request.SnapshotId != null) ||
                request.Offset != 0 && request.Exact == null && request.SnapshotId == null)
                throw new ArgumentException("Continuation requires one exact reference or retained snapshot.");
            var resources = FileResources(workspace);
            if (request.Representation == ResourceRepresentations.Metadata)
            {
                if (request.Exact != null || request.SnapshotId != null || request.Offset != 0)
                    throw new ArgumentException("Metadata discovery describes the current target, without a revision or offset.");
                return new WorkspaceResourceView { Target = request.Target,
                    Descriptor = resources.Select("file").Describe(request.Target) };
            }
            var read = request.SnapshotId != null
                ? resources.Select("file").ReadSnapshot(_webSnapshots.ReadSnapshot(workspace, request.SnapshotId), request.Target)
                : request.Exact != null ? resources.ForUri(request.Exact.Uri).ReadExact(request.Target, request.Exact)
                : resources.Select("file").Read(request.Target);
            read.RequireCompleteExactText();
            var text = read.Result.Text;
            if (request.Offset > text.Length || request.Offset < text.Length && char.IsLowSurrogate(text[request.Offset]))
                throw new ArgumentException("Resource offset is outside this text or splits a Unicode character.");
            var end = request.Offset + Math.Min(request.MaxCharacters, text.Length - request.Offset);
            if (end < text.Length && char.IsHighSurrogate(text[end - 1])) end--;
            var complete = request.Offset == 0 && end == text.Length;
            var coverage = complete ? ResourceCoverage.Whole() :
                new ResourceCoverage(ResourceCoverageKinds.CharacterRange, start: request.Offset, end: end);
            // UI reads never append accepted tool evidence or grant model write authority.
            return new WorkspaceResourceView { Target = request.Target, Descriptor = read.Result.Resource,
                Retained = request.Exact != null || request.SnapshotId != null,
                NextOffset = end < text.Length ? (int?)end : null,
                Content = new ResourceReadProjection { Target = request.Target, Type = "file", Scope = "workspace",
                    Representation = ResourceRepresentations.Text, Text = text.Substring(request.Offset, end - request.Offset),
                    Coverage = coverage, Offset = request.Offset, ReturnedCharacters = end - request.Offset,
                    TotalCharacters = text.Length, Complete = complete, RawContentIncluded = true } };
        }

        public Task<WorkspacePreviewReport> OpenPreviewAsync(WorkspaceDescriptor workspace, WorkspacePreviewRequest request,
            Action<WorkspacePreviewReport> opened, CancellationToken cancellationToken = default(CancellationToken))
        { return new WorkspaceWebVerifier(FileResources(workspace), _webSnapshots, workspace)
                .PreviewAsync(request, opened, cancellationToken); }
    }
}
