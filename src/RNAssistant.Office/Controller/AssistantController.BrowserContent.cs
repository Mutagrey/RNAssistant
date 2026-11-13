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
using RNAssistant.Office.Services;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        internal ArtifactTransferDownload OpenBrowserScreenshot(BrowserScreenshotRequest request, BrowserMediaCapture capture, Func<bool> current, CancellationToken token)
        {
            if (request == null || capture?.Page == null || capture.Page.BrowserTabId != request.BrowserTabId ||
                capture.Page.SessionId != request.SessionId || capture.Page.DocumentGeneration != request.Generation ||
                !capture.PrivacyVerified || capture.Png == null || capture.Png.Length == 0 || capture.Png.Length > 4 * 1024 * 1024 || current?.Invoke() != true)
                throw new InvalidOperationException("Точный снимок страницы недоступен.");
            return new ArtifactTransferDownload { FileName = "rnassistant-page.png", ContentType = "image/png", Kind = "browser_screenshot",
                Data = _resourceData.OpenBrowserDownload(request.SessionId, current,
                    cancellation => new ResourceDownloadContent { Bytes = capture.Png, ContentType = "image/png" }, token) };
        }
        internal ResourceDataCloseResponse CloseBrowserScreenshot(BrowserScreenshotRequest request)
        {
            _resourceData.CloseBrowserDownload(request?.SessionId, request?.LeaseId);
            return new ResourceDataCloseResponse { Closed = true };
        }
        internal ChatAttachment StageBrowserVisualSelection(BrowserAnnotationCapture capture, BrowserAnnotationRequest request)
        {
            var session = LoadAddressedSession(capture.Owner.ChatId);
            ConversationRunPolicy.RequireCurrentSession(session); EnsureCurrentDocument(session);
            return _chatResourceIngestion.StageBrowserVisualSelection(session, capture, request);
        }
        internal BrowserHtmlExportResponse PrepareBrowserHtmlExport(BrowserProjectTransferRequest request, CancellationToken token)
        {
            using (var content = OpenBrowserContent(request?.Target, token)) {
                if (content.HtmlWorkspace == null) throw new InvalidOperationException("Выберите HTML workspace для автономного экспорта.");
                var target = content.Target; var metadata = BrowserOwner(target.OwnerScope, target.Immutable);
                var artifact = _chatStore.DocumentArtifacts.Read(metadata, ChatResourceUri.ArtifactSnapshot(new ResourceRef(target.ResourceUri)), false);
                var payload = new PayloadRef(artifact.ContentSha256, artifact.ContentByteLength ?? -1, "application/json; charset=utf-8");
                var session = LoadAddressedSession(metadata.Id); _toolExecutor.BindResourceAuthority(session);
                ResourceDownloadOpenResponse source = null; HtmlResourceExport resources = null;
                try {
                    source = _resourceData.OpenDownload(metadata, "browser", 16 * 1024 * 1024, cancellation => new ResourceDownloadContent {
                        VerifiedStream = new ChatBlobStore(_paths).OpenVerifiedRead(payload.ToBlobReference(), 16 * 1024 * 1024, cancellation),
                        VerifiedPayload = payload, ContentType = payload.ContentType }, token);
                    resources = new HtmlWorkspaceExportService(_toolExecutor.ResourceGateway, _resourceData).OpenBindings(session, "browser", content.HtmlWorkspace.DataSources, token);
                    content.Validate(); token.ThrowIfCancellationRequested();
                    return new BrowserHtmlExportResponse { ChatId = metadata.Id, Target = target, Source = source, ResourceExport = resources };
                } catch {
                    if (source != null) _resourceData.Close(metadata.Id, "browser", source.LeaseId);
                    if (resources != null) foreach (var binding in resources.Bindings) _resourceData.Close(metadata.Id, "browser", binding.Lease.LeaseId);
                    throw;
                }
            }
        }
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
                    (path, cancellation) => { cancellation.ThrowIfCancellationRequested(); return new MemoryStream(new UTF8Encoding(false, true).GetBytes(files[path]), false); }, validate, htmlWorkspace: snapshot,
                    annotationSource: path => reference);
            }
            if (!BrowserTargetResolver.IsHtmlResource(artifact)) throw new InvalidOperationException("This resource is not HTML.");
            var payload = new PayloadRef(artifact.ContentSha256, artifact.ContentByteLength ?? -1, artifact.MimeType);
            return new BrowserContentSession(target, new[] { "index.html" }, "index.html",
                (path, cancellation) => new ChatBlobStore(_paths).OpenVerifiedRead(payload.ToBlobReference(), 16 * 1024 * 1024, cancellation), validate,
                annotationSource: path => reference);
        }
    }
}
