using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Services;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        internal BrowserContentSession OpenBrowserContent(BrowserTargetDescriptor requested, CancellationToken token)
        {
            var request = new BrowserResolveRequest { OwnerScope = requested?.OwnerScope, Locator = requested?.DisplayLocator };
            var target = ResolveBrowserLocator(request).Target;
            if (target?.State != BrowserTargetState.Ready || target.ResourceUri != requested.ResourceUri ||
                target.SavedVersionId != requested.SavedVersionId) throw new InvalidOperationException("The exact document HTML is unavailable.");
            var metadata = BrowserOwner(target.OwnerScope, target.Immutable);
            var reference = ChatResourceUri.ArtifactSnapshot(new ResourceRef(target.ResourceUri));
            var artifact = _chatStore.DocumentArtifacts.Read(metadata, reference, false);
            Action validate = () => {
                BrowserOwner(target.OwnerScope, target.Immutable);
                var current = ResolveBrowserLocator(request).Target;
                if (current?.State != BrowserTargetState.Ready || current.ResourceUri != target.ResourceUri ||
                    current.SavedVersionId != target.SavedVersionId || _chatStore.DocumentArtifacts.IsRemoved(metadata, artifact))
                    throw new InvalidOperationException("The document HTML binding changed or was removed.");
            };
            validate(); token.ThrowIfCancellationRequested();
            if (artifact.Kind == ChatArtifactKinds.HtmlWorkspace)
            {
                if (artifact.ContentByteLength > 16 * 1024 * 1024) throw new InvalidOperationException("HTML snapshot exceeds 16 MiB.");
                artifact = _chatStore.DocumentArtifacts.Read(metadata, reference);
                var snapshot = JsonConvert.DeserializeObject<HtmlWorkspaceSnapshot>(artifact.InlineText);
                var files = snapshot.Files.ToDictionary(file => file.Path, file => file.Content, StringComparer.Ordinal);
                return new BrowserContentSession(target, files.Keys, target.RelativePath ?? target.EntryPath,
                    (path, cancellation) => { cancellation.ThrowIfCancellationRequested(); return new MemoryStream(new UTF8Encoding(false, true).GetBytes(files[path]), false); }, validate);
            }
            if (!BrowserTargetResolver.IsHtmlResource(artifact)) throw new InvalidOperationException("This resource is not HTML.");
            var payload = new PayloadRef(artifact.ContentSha256, artifact.ContentByteLength ?? -1, artifact.MimeType);
            return new BrowserContentSession(target, new[] { "index.html" }, "index.html",
                (path, cancellation) => new ChatBlobStore(_paths).OpenVerifiedRead(payload.ToBlobReference(), 16 * 1024 * 1024, cancellation), validate);
        }
    }
}
