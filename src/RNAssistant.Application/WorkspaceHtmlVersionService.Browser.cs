using System;
using System.Linq;
using System.Threading;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Application
{
    public sealed partial class WorkspaceHtmlVersionService
    {
        public BrowserContentSession OpenBrowserContent(BrowserTargetDescriptor requested, CancellationToken token)
        {
            var request = new BrowserResolveRequest { OwnerScope = requested?.OwnerScope, Locator = requested?.DisplayLocator };
            var target = Resolve(request).Target;
            if (target?.State != BrowserTargetState.Ready || target.Kind != BrowserTargetKind.WorkspaceHtmlProject ||
                target.SavedVersionId != requested.SavedVersionId) throw new InvalidOperationException("The exact HTML project is unavailable.");
            HtmlProjectSnapshotV1 snapshot = null;
            if (target.SavedVersionId != null) snapshot = _artifacts.ReadWorkspaceHtml(target.WorkspaceId, target.SavedVersionId);
            else Capture(target.WorkspaceId, token, (value, validate) => { validate(); snapshot = value; }, readOnlyPreview: true);
            target.ContentRevision = DocumentArtifactStore.WorkspaceHtmlFingerprint(snapshot);
            var members = snapshot.Members.ToDictionary(member => member.Path, StringComparer.Ordinal);
            var blobs = new ChatBlobStore(_paths);
            return new BrowserContentSession(target, members.Keys, target.RelativePath ?? snapshot.EntryPath,
                (path, cancellation) => {
                    var member = members[path];
                    return blobs.OpenVerifiedRead(new PayloadRef(member.Sha256, member.ByteLength, member.Mime).ToBlobReference(),
                        WorkspaceFileService.MaximumCaptureSetBytes, cancellation);
                }, () => {
                    var current = Resolve(request).Target;
                    if (current?.State != BrowserTargetState.Ready || current.SavedVersionId != target.SavedVersionId)
                        throw new InvalidOperationException("The HTML project was removed or its owner is unavailable.");
                });
        }
    }
}
