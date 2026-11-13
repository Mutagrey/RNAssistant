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
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        internal ResourceDataOpenResponse OpenBrowserResource(BrowserTargetDescriptor target, string name, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 128) throw new InvalidOperationException("RESOURCE_BINDING_UNKNOWN");
            var metadata = BrowserOwner(target?.OwnerScope, target?.Immutable == true);
            if (target?.State != BrowserTargetState.Ready) throw new InvalidOperationException("RESOURCE_ACCESS_DENIED");
            var current = ResolveBrowserLocator(new BrowserResolveRequest { OwnerScope = target.OwnerScope, Locator = target.DisplayLocator }).Target;
            if (current?.ResourceUri != target.ResourceUri || current.State != BrowserTargetState.Ready) throw new InvalidOperationException("RESOURCE_WORKSPACE_CHANGED");
            var artifact = _chatStore.DocumentArtifacts.Read(metadata, ChatResourceUri.ArtifactSnapshot(new ResourceRef(target.ResourceUri)));
            if (artifact.Kind != ChatArtifactKinds.HtmlWorkspace || _chatStore.DocumentArtifacts.IsRemoved(metadata, artifact)) throw new InvalidOperationException("RESOURCE_ACCESS_DENIED");
            var snapshot = JsonConvert.DeserializeObject<HtmlWorkspaceSnapshot>(artifact.InlineText);
            var sources = snapshot.DataSources.Where(source => source.Name == name).ToArray();
            if (sources.Length != 1) throw new InvalidOperationException("RESOURCE_BINDING_UNKNOWN");
            var binding = sources[0].Binding; HtmlWorkspaceToolService.NormalizeBinding(binding, sources[0]);
            var session = LoadAddressedSession(metadata.Id); _toolExecutor.BindResourceAuthority(session);
            return _resourceData.Open(session, "browser", binding.Policy == "head" ? new ResourceRef(binding.Resource.Identity.Uri) : binding.Resource.Copy(),
                binding.View, binding.ViewPath, token);
        }
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
                var entry = snapshot.Files.SingleOrDefault(file => file.Id == snapshot.ActiveFileId && file.Kind == "html") ?? snapshot.Files.FirstOrDefault(file => file.Kind == "html");
                target.EntryPath = target.RelativePath ?? entry?.Path;
                return new BrowserContentSession(target, files.Keys, target.EntryPath,
                    (path, cancellation) => { cancellation.ThrowIfCancellationRequested(); return new MemoryStream(new UTF8Encoding(false, true).GetBytes(files[path]), false); }, validate, htmlWorkspace: snapshot);
            }
            if (!BrowserTargetResolver.IsHtmlResource(artifact)) throw new InvalidOperationException("This resource is not HTML.");
            var payload = new PayloadRef(artifact.ContentSha256, artifact.ContentByteLength ?? -1, artifact.MimeType);
            return new BrowserContentSession(target, new[] { "index.html" }, "index.html",
                (path, cancellation) => new ChatBlobStore(_paths).OpenVerifiedRead(payload.ToBlobReference(), 16 * 1024 * 1024, cancellation), validate);
        }
    }
}
