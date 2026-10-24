using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Runtime
{
    public sealed partial class WorkspaceConversationService
    {
        private List<ChatAttachment> LoadVisualSelectionDrafts(WorkspaceDescriptor workspace,
            ChatSession session, IReadOnlyList<string> draftIds, AttachmentStore attachments)
        {
            var drafts = attachments.LoadDrafts(draftIds, session.Id);
            if (drafts.Count == 0) return drafts;
            using (PluginStore.AcquireRuntimeUse(_paths))
            {
                var plugins = new PluginStore(_paths);
                foreach (var draft in drafts)
                {
                    ResourceAddress address;
                    var source = draft.VisualSelectionSource;
                    if (draft.Status != "ready" || draft.Kind != "text" ||
                        !string.Equals(draft.ContentType, "application/json", StringComparison.OrdinalIgnoreCase) ||
                        draft.Size < 1 || draft.Size > HtmlVisualSelectionPolicy.MaximumBodyBytes ||
                        source?.IsExact != true || source.Revision != "1" ||
                        !ResourceUri.TryParse(source.Uri, out address) || address.Provider != "plugin" ||
                        address.Segments.Count != 3 || address.Segments[1] != "release")
                        throw new InvalidOperationException("Выделение плагина недействительно. Выберите элемент заново.");
                    var release = plugins.ReadRelease(new PluginReleaseIdentity(address.Segments[0], address.Segments[2]));
                    var registration = plugins.Get(address.Segments[0]);
                    if (release.SourceWorkspaceId != workspace.WorkspaceId ||
                        registration?.SourceWorkspaceId != workspace.WorkspaceId)
                        throw new InvalidOperationException("Выделение принадлежит другому workspace.");
                    HtmlVisualSelectionPolicy.ReadCapturedBody(attachments.ReadBytes(draft));
                }
            }
            return drafts;
        }

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
            var binary = request.Representation == ResourceRepresentations.Raw || request.Representation == ResourceRepresentations.Image;
            if (!binary && request.Representation != ResourceRepresentations.Text && request.Representation != ResourceRepresentations.Metadata)
                throw new ArgumentException("Supported resource views: metadata, text, raw, image.");
            if (request.Offset < 0 || request.MaxCharacters < ResourceReadRequest.MinimumCharacters ||
                request.MaxCharacters > ResourceReadRequest.MaximumCharacters)
                throw new ArgumentException("Resource offset must be nonnegative and max characters must be 128–32000.");
            if (request.Exact != null && (!request.Exact.IsExact || request.SnapshotId != null) ||
                request.Offset != 0 && request.Exact == null && request.SnapshotId == null)
                throw new ArgumentException("Continuation requires one exact reference or retained snapshot.");
            var resources = FileResources(workspace);
            if (binary)
            {
                if (request.Offset != 0) throw new ArgumentException("Binary views are bounded whole-file reads; offsets are not supported.");
                var bytes = request.SnapshotId != null
                    ? resources.Select("file").ReadSnapshotBytes(_webSnapshots.ReadSnapshot(workspace, request.SnapshotId), request.Target)
                    : request.Exact != null ? resources.ForUri(request.Exact.Uri).ReadExactBytes(request.Target, request.Exact)
                    : resources.Select("file").ReadBytes(request.Target);
                bytes.RequireCompleteExactBytes();
                if (request.Representation == ResourceRepresentations.Image &&
                    !bytes.Result.Resource.Representations.Contains(ResourceRepresentations.Image))
                    throw new RNAssistant.Core.Storage.WorkspaceFileException("unsupported_image", "Image view requires a bounded PNG or JPEG header (at most 4096 pixels per side).");
                return new WorkspaceResourceView { Target = request.Target, Descriptor = bytes.Result.Resource,
                    Representation = request.Representation, Binary = bytes.Result.Binary, Bytes = bytes.Result.Bytes,
                    Retained = request.Exact != null || request.SnapshotId != null };
            }
            if (request.Representation == ResourceRepresentations.Metadata)
            {
                if (request.Exact != null || request.SnapshotId != null || request.Offset != 0)
                    throw new ArgumentException("Metadata discovery describes the current target, without a revision or offset.");
                return new WorkspaceResourceView { Target = request.Target,
                    Representation = ResourceRepresentations.Metadata, Descriptor = resources.Select("file").Describe(request.Target) };
            }
            ResourceReadObservation read;
            try
            {
                read = request.SnapshotId != null
                    ? resources.Select("file").ReadSnapshot(_webSnapshots.ReadSnapshot(workspace, request.SnapshotId), request.Target)
                    : request.Exact != null ? resources.ForUri(request.Exact.Uri).ReadExact(request.Target, request.Exact)
                    : resources.Select("file").Read(request.Target);
            }
            catch (WorkspaceFileException error) when (request.SnapshotId == null &&
                (request.Exact == null && error.Code == "file_too_large" ||
                    request.Exact != null && error.Code == "snapshot_unavailable"))
            {
                // Raw-only large text pins are explicitly decoded from the exact
                // retained revision. The file owner rejects substitution when a
                // published text view exists but is unavailable or corrupt.
                read = resources.Select("file").ReadViewCharacters(request.Target, request.Offset,
                    request.MaxCharacters, request.Exact);
            }
            if (!read.Result.Complete)
            {
                var selected = read.Result;
                var evidence = read.Evidence.SingleOrDefault();
                if (selected.Text == null || selected.Resource?.Reference?.IsExact != true ||
                    selected.Representation != ResourceRepresentations.Text ||
                    selected.Offset != request.Offset || selected.Text.Length > request.MaxCharacters ||
                    selected.ReturnedCharacters != selected.Text.Length ||
                    selected.Coverage?.Kind != ResourceCoverageKinds.CharacterRange ||
                    selected.Coverage.Start != request.Offset ||
                    selected.Coverage.End != (long)request.Offset + selected.Text.Length ||
                    selected.TotalCharacters < selected.Coverage.End || evidence?.Complete != false ||
                    evidence.View != ResourceRepresentations.Text || evidence.Payload == null ||
                    evidence.Resource.Uri != selected.Resource.Reference.Uri ||
                    evidence.Resource.Revision != selected.Resource.Reference.Revision ||
                    evidence.Coverage.Start != selected.Coverage.Start ||
                    evidence.Coverage.End != selected.Coverage.End)
                    throw new InvalidOperationException("Large text view requires an exact bounded excerpt.");
                var excerpt = ResourceReadProjection.From(selected, request.Target, "file", "workspace");
                var next = read.Result.Offset + read.Result.ReturnedCharacters;
                return new WorkspaceResourceView { Target = request.Target, Descriptor = read.Result.Resource,
                    Representation = ResourceRepresentations.Text, Retained = request.Exact != null,
                    NextOffset = next < read.Result.TotalCharacters ? (int?)next : null, Content = excerpt };
            }
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
                Representation = ResourceRepresentations.Text,
                Retained = request.Exact != null || request.SnapshotId != null,
                NextOffset = end < text.Length ? (int?)end : null,
                Content = new ResourceReadProjection { Target = request.Target, Type = "file", Scope = "workspace",
                    Representation = ResourceRepresentations.Text, Text = text.Substring(request.Offset, end - request.Offset),
                    Coverage = coverage, Offset = request.Offset, ReturnedCharacters = end - request.Offset,
                    TotalCharacters = text.Length, Complete = complete, RawContentIncluded = true } };
        }

        public Task<WorkspacePreviewReport> OpenPreviewAsync(WorkspaceDescriptor workspace, WorkspacePreviewRequest request,
            Action<WorkspacePreviewReport> opened, CancellationToken cancellationToken = default(CancellationToken))
        { return RequireWebHost().Create(FileResources(workspace), _webSnapshots, workspace)
                .PreviewAsync(request, opened, cancellationToken); }

        public WorkspaceWebDiagnosticView OpenWebDiagnostic(WorkspaceDescriptor workspace, WorkspaceWebDiagnosticRequest request)
        {
            if (request == null || !Enum.IsDefined(typeof(WebDiagnosticKind), request.Kind))
                throw new ArgumentException("A valid browser diagnostic request is required.");
            var record = _webSnapshots.ReadVerification(workspace, request.VerificationId);
            var bytes = _webSnapshots.ReadDiagnostic(workspace, record.Reference, request.Kind);
            return new WorkspaceWebDiagnosticView { VerificationId = record.Id, SnapshotId = record.SnapshotId,
                Diagnostic = record.Diagnostics.Single(item => item.Kind == request.Kind), Bytes = bytes };
        }
    }
}
