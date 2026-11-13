using System;
using System.Linq;
using System.Threading;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Models;

namespace RNAssistant.Application
{
    public sealed partial class PluginApplicationService
    {
        public ChatAttachment StageBrowserVisualSelection(BrowserAnnotationCapture capture, BrowserAnnotationRequest request)
        {
            using (PluginStore.AcquireRuntimeUse(_paths)) lock (_sync) {
                RequireOpen();
                if (capture?.Owner?.Kind != BrowserOwnerKind.Workspace || capture.Page?.WorkspaceId != capture.Owner.ScopeId)
                    throw new InvalidOperationException("Укажите точный проект аннотации.");
                var workspace = RequireProjectWorkspace(capture.Owner.ScopeId);
                var session = Conversations.GetSession(workspace, capture.Owner.ChatId);
                if (session == null) throw new InvalidOperationException("Чат аннотации недоступен.");
                WorkspaceVisualSelectionService.RequireSource(_paths, workspace,
                    new ChatAttachment { VisualSelectionSource = capture.Source, VisualSelectionPage = capture.Page });
                return new AttachmentStore(_paths).StageVisualSelection(session.Id, request.Selection, capture.Title,
                    request.Comment, request.AnnotationNumber, capture.Source, capture.Page);
            }
        }
        public BrowserContentSession OpenBrowserContent(BrowserTargetDescriptor requested, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var target = ResolveBrowserLocator(new BrowserResolveRequest { OwnerScope = requested?.OwnerScope, Locator = requested?.DisplayLocator }).Target;
            if (target?.State != BrowserTargetState.Ready || target.SavedVersionId != requested.SavedVersionId)
                throw new InvalidOperationException("The Browser source is unavailable.");
            if (target.Kind == BrowserTargetKind.WorkspaceHtmlProject) return HtmlProjects.OpenBrowserContent(target, token);
            if (target.Kind != BrowserTargetKind.PluginSource && target.Kind != BrowserTargetKind.PluginRelease)
                throw new InvalidOperationException("This source is not a plugin.");
            // Working preview uses the existing bounded Check capture, never an implicit Save/Apply.
            var candidate = target.SavedVersionId == null ? Check(target.PluginId) : null;
            token.ThrowIfCancellationRequested();
            var view = Open(new PluginOpenRequest { PluginId = target.PluginId, CandidateId = candidate?.CandidateId, VersionId = target.SavedVersionId });
            var instance = FindInstance(new Uri(view.EntryUrl).GetLeftPart(UriPartial.Authority));
            if (instance == null) throw new InvalidOperationException("The exact plugin instance was not created.");
            try
            {
                var release = instance.Release; var blobs = new ChatBlobStore(_paths);
                var members = release.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
                var entry = target.RelativePath != "plugin.json" && members.ContainsKey(target.RelativePath ?? "") ? target.RelativePath : PluginPackageFiles.ParseManifest(release.ManifestJson).Entry;
                target.EntryPath = entry; target.ContentRevision = release.ReleaseId;
                return new BrowserContentSession(target, members.Keys, entry,
                    (path, cancellation) => blobs.OpenVerifiedRead(members[path].Payload.ToBlobReference(), WorkspaceFileService.MaximumCaptureSetBytes, cancellation),
                    () => {
                        instance.Token.ThrowIfCancellationRequested();
                        var current = ResolveBrowserLocator(new BrowserResolveRequest { OwnerScope = target.OwnerScope, Locator = target.DisplayLocator }).Target;
                        if (current?.State != BrowserTargetState.Ready || current.SavedVersionId != target.SavedVersionId)
                            throw new InvalidOperationException("The plugin source was removed or revoked.");
                    }, instance, () => CloseInstanceAsync(instance.Id),
                    annotationSource: path => PluginStore.ReleaseRef(release.PluginId, release.ReleaseId));
            }
            catch { CloseInstanceAsync(instance.Id).GetAwaiter().GetResult(); throw; }
        }
    }
}
