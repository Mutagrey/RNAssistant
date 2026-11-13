using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Persistence;
using System.Threading;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        private ConversationMetadata BrowserOwner(BrowserOwnerScope owner, bool allowCatalogRead = false)
        {
            if (owner?.Kind != BrowserOwnerKind.Document || string.IsNullOrEmpty(owner.ChatId)) throw new InvalidOperationException("Выберите точный документ и чат Browser.");
            var metadata = LoadDiagnosticsMetadata(owner.ChatId, null);
            if (allowCatalogRead) return ArtifactCatalog().SourceMetadata(metadata, owner.ScopeId);
            if (metadata.DocumentAuthorityId != owner.ScopeId) throw new InvalidOperationException("Контекст документа Browser изменился.");
            return metadata;
        }
        private DocumentHtmlVersionService HtmlVersions()
        { return new DocumentHtmlVersionService(_paths, _chatStore.DocumentArtifacts); }
        internal BrowserResolveResult ResolveBrowserLocator(BrowserResolveRequest request)
        {
            BrowserLocalLocator locator;
            var exact = BrowserLocalLocator.TryParse(request?.Locator, out locator) &&
                (locator.VersionId != null || locator.Kind == BrowserLocatorKind.Resource && locator.Key.Contains("~"));
            var owner = BrowserOwner(request?.OwnerScope, exact);
            if (request.Locator == "/") return BrowserTargetResolver.ResolveLocalLocator(request.OwnerScope, "/", null);
            return HtmlVersions().Resolve(owner, request);
        }
        internal BrowserVersionState BrowserVersionState(BrowserVersionRequest request)
        { return HtmlVersions().State(BrowserOwner(request?.Target?.OwnerScope, true), request); }
        internal SourceComparisonDto CompareBrowserSources(BrowserSourceComparisonRequest request)
        { return HtmlVersions().Compare(BrowserOwner(request?.Target?.OwnerScope, true), request); }
        internal BrowserVersionPage BrowserVersions(BrowserVersionRequest request)
        { return HtmlVersions().Versions(BrowserOwner(request?.Target?.OwnerScope, true), request); }
        internal BrowserVersionResult BrowserVersionCommand(BrowserVersionCommand request, CancellationToken token)
        { return HtmlVersions().Command(BrowserOwner(request?.Target?.OwnerScope, request?.Action == BrowserVersionAction.Delete), request, token); }

        internal HtmlProjectImportResult CopyBrowserProject(BrowserProjectTransferRequest request, CancellationToken token)
        { return new WorkspaceHtmlVersionService(_paths).CopySavedProject(request?.Target, token); }
        internal RNAssistant.Office.Contracts.ArtifactTransferDownload ExportBrowserProject(BrowserProjectTransferRequest request, CancellationToken token)
        {
            var service = new WorkspaceHtmlVersionService(_paths); var target = request?.Target;
            HtmlProjectExport source = null;
            var data = _resourceData.OpenWorkspaceDownload(target?.WorkspaceId,
                () => service.TransferOwnerAvailable(target?.OwnerScope, target?.WorkspaceId), WorkspaceHtmlVersionService.MaximumExportBytes,
                cancellation => {
                    using (source = service.ExportSavedProject(target, cancellation)) {
                        var content = new RNAssistant.Office.Services.ResourceDownloadContent { VerifiedStream = source.Stream,
                            VerifiedPayload = source.Payload, ContentType = source.Payload.ContentType };
                        source.Stream = null; return content;
                    }
                }, token);
            return new RNAssistant.Office.Contracts.ArtifactTransferDownload { FileName = source.FileName,
                ContentType = source.Payload.ContentType, Kind = "html_project", Data = data };
        }
        internal RNAssistant.Office.Contracts.ResourceDataCloseResponse CloseBrowserProjectTransfer(BrowserProjectTransferRequest request)
        {
            _resourceData.Close(null, request?.Target?.WorkspaceId, request?.LeaseId);
            return new RNAssistant.Office.Contracts.ResourceDataCloseResponse { Closed = true };
        }

        internal BrowserSourcePage ReadBrowserSource(BrowserSourceRequest request, BrowserTargetDescriptor target)
        {
            var metadata = BrowserOwner(request.OwnerScope, target.Immutable);
            if (target.OwnerScope.ScopeId != metadata.DocumentAuthorityId || target.ChatId != metadata.Id ||
                target.State != BrowserTargetState.Ready) throw new InvalidOperationException("Контекст исходника Browser изменился.");
            var reference = ChatResourceUri.ArtifactSnapshot(new ResourceRef(target.ResourceUri));
            var artifact = _chatStore.DocumentArtifacts.Read(metadata, reference, false);
            if (_chatStore.DocumentArtifacts.IsRemoved(metadata, artifact)) throw new InvalidOperationException("Источник удалён из каталога.");
            if (artifact.ContentByteLength > 8 * 1024 * 1024) throw new InvalidOperationException("Исходник превышает лимит просмотра.");
            if (artifact.Kind == ChatArtifactKinds.HtmlWorkspace)
            {
                artifact = _chatStore.DocumentArtifacts.Read(metadata, reference);
                var snapshot = JsonConvert.DeserializeObject<HtmlWorkspaceSnapshot>(artifact.InlineText);
                return BrowserSourceReader.Read(target, request, snapshot.Files.Select(file => file.Path),
                    path => snapshot.Files.Single(file => file.Path == path).Content);
            }
            if (!BrowserTargetResolver.IsHtmlResource(artifact)) throw new InvalidOperationException("Источник не является HTML.");
            var text = new UTF8Encoding(false, true).GetString(_toolExecutor.Payloads.ReadBytes(
                new PayloadRef(artifact.ContentSha256, artifact.ContentByteLength ?? -1, artifact.MimeType).ToBlobReference()));
            return BrowserSourceReader.Read(target, request, new[] { "index.html" }, path => text);
        }

        internal IReadOnlyList<BrowserTargetDescriptor> BrowserTargets(BrowserOwnerScope owner, string suggestionsPrefix = null)
        {
            var metadata = BrowserOwner(owner);
            var result = new List<BrowserTargetDescriptor>(); var savedFamilies = 0;
            // Current metadata roots only: no chat replay, aggregate bodies or automatic history.
            var page = _chatStore.DocumentArtifacts.InspectCurrentMetadata(metadata, 0, 100);
            foreach (var artifact in page.Items)
            {
                if (artifact.Kind != ChatArtifactKinds.HtmlWorkspace && !BrowserTargetResolver.IsHtmlResource(artifact)) continue;
                var target = BrowserTargetResolver.Saved(metadata, artifact, ChatResourceUri.CreateArtifactRevision(metadata.Id, artifact).Uri);
                if (artifact.Kind == ChatArtifactKinds.HtmlWorkspace)
                {
                    target.DisplayLocator = BrowserLocalLocator.ForResource(target.LogicalId);
                    target.Kind = BrowserTargetKind.HtmlWorkspace; target.Immutable = false;
                    target.Capabilities.SaveVersion = true;
                    if (suggestionsPrefix != null && savedFamilies < 8 && BrowserTargetResolver.SuggestionFamilyMatches(suggestionsPrefix, target.Title, target.DisplayLocator)) {
                        savedFamilies++;
                        result.AddRange(HtmlVersions().SavedBrowserTargets(metadata, target, 8));
                    }
                }
                result.Add(target);
            }
            return result;
        }
    }
}
