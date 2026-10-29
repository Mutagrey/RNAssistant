using RNAssistant.Core.Persistence;
using System;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;
using RNAssistant.Core.Services;
using RNAssistant.Office.Services;
using System.Threading;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        public ArtifactLibraryPageResponse GetArtifactLibraryPage(ArtifactLibraryPageRequest request)
        { return ArtifactLibraryProjectionService.Page(LoadArtifactViewerSession(request?.ChatId), request); }

        public ArtifactLibraryHistoryResponse GetArtifactLibraryHistory(ArtifactLibraryHistoryRequest request)
        { return ArtifactLibraryProjectionService.History(LoadArtifactViewerSession(request?.ChatId), request); }

        private ArtifactCatalogService ArtifactCatalog()
        {
            return new ArtifactCatalogService(_documentAuthorityRegistry, _chatStore.DocumentArtifacts,
                _artifactWorkingSet, _resourceAuthorityStore, _toolExecutor.Payloads);
        }

        public ArtifactCatalogResponse ListArtifactCatalog(ArtifactCatalogRequest request)
        { return ArtifactCatalog().List(LoadArtifactViewerSession(request?.ChatId), request); }

        public ArtifactTransferDownload ExportArtifact(ArtifactTransferRequest request, CancellationToken token)
        {
            var session = LoadArtifactViewerSession(request?.ChatId);
            ArtifactTransferContent content = null;
            var data = _resourceData.OpenDownload(session, ArtifactCatalogService.Owner, ArtifactTransferPackage.MaximumBytes,
                cancellation => {
                    cancellation.ThrowIfCancellationRequested();
                    content = ArtifactCatalog().Export(session, request);
                    return new ResourceDownloadContent { Bytes = content.Bytes, ContentType = content.ContentType };
                }, token);
            return new ArtifactTransferDownload { FileName = content.FileName, ContentType = content.ContentType, Kind = content.Kind, Data = data };
        }

        public ResourceUploadOpenResponse BeginArtifactImport(ResourceUploadOpenRequest request, CancellationToken token)
        { return _resourceData.OpenUpload(LoadArtifactViewerSession(request?.ChatId), request, token,
            ArtifactCatalogService.Owner, ArtifactTransferPackage.MaximumBytes, allowEmpty: true); }

        public ResourceDataCloseResponse CloseArtifactTransfer(ResourceUploadLeaseRequest request)
        {
            _resourceData.CloseUpload(request.ChatId, request.LeaseId, ArtifactCatalogService.Owner);
            _resourceData.Close(request.ChatId, ArtifactCatalogService.Owner, request.LeaseId);
            return new ResourceDataCloseResponse { Closed = true };
        }

        public ChatStateResponse ImportArtifact(ArtifactTransferRequest request, CancellationToken token)
        {
            return WithReservedChatState(LoadArtifactViewerSession(request?.ChatId), session => {
                RequireArtifactTransferRevision(session, request);
                var artifact = _resourceData.ConsumeUpload(session, request.UploadLeaseId, ArtifactCatalogService.Owner,
                    (bytes, name, type) => {
                        token.ThrowIfCancellationRequested();
                        using (new ResourceMutationJournal(_paths).AcquireScope(ResourceAuthorityScopeId.Document(new DocumentAuthorityId(session.DocumentAuthorityId))))
                            return ArtifactCatalog().Import(session, bytes, name, type, request.OperationId);
                    }, token);
                AttachTransferredArtifact(session, artifact);
            });
        }

        public ChatStateResponse CopyArtifact(ArtifactTransferRequest request, CancellationToken token)
        {
            return WithReservedChatState(LoadArtifactViewerSession(request?.ChatId), session => {
                RequireArtifactTransferRevision(session, request);
                request.Preview = false;
                var source = ArtifactCatalog().Export(session, request);
                token.ThrowIfCancellationRequested();
                ChatArtifact artifact;
                using (new ResourceMutationJournal(_paths).AcquireScope(ResourceAuthorityScopeId.Document(new DocumentAuthorityId(session.DocumentAuthorityId))))
                    artifact = ArtifactCatalog().Import(session, source.Bytes, source.FileName, source.ContentType, request.OperationId, source.Origin);
                AttachTransferredArtifact(session, artifact);
            });
        }

        private static void RequireArtifactTransferRevision(ChatSession session, ArtifactTransferRequest request)
        {
            if (request.ExpectedSessionRevision != session.Revision)
                throw new InvalidOperationException("Чат изменился. Обновите каталог перед переносом.");
        }

        private void AttachTransferredArtifact(ChatSession session, ChatArtifact artifact)
        {
            _artifactWorkingSet.Change(session, new ArtifactLinkChangeRequest { ChatId = session.Id,
                ExpectedSessionRevision = session.Revision, ResourceUri = ChatResourceUri.CreateArtifactRevisionUri(session, artifact), Detached = false },
                current => { _conversationStore.Commit(current, new ConversationCommit()); _chatSessions.NotifySaved(current); });
        }

        public ChatStateResponse ChangeArtifactLink(ArtifactLinkChangeRequest request)
        {
            return WithReservedChatState(LoadArtifactViewerSession(request?.ChatId), session =>
            {
                if (!_chatSessions.IsCurrentDocument(session))
                    throw new InvalidOperationException("Откройте документ этого чата перед изменением ссылок.");
                _artifactWorkingSet.Change(session, request, current =>
                {
                    _conversationStore.Commit(current, new ConversationCommit());
                    _chatSessions.NotifySaved(current);
                });
            });
        }

        public ChatStateResponse DeleteDocumentArtifact(ArtifactDeleteRequest request)
        {
            return WithReservedChatState(LoadArtifactViewerSession(request?.ChatId), session =>
                _artifactWorkingSet.Delete(session, request, current =>
                {
                    _conversationStore.Commit(current, new ConversationCommit());
                    _chatSessions.NotifySaved(current);
                }));
        }

        private static ResourceAuthorityScopeId PatchOwnerScope(ResourceRef reference, string workspaceId)
        {
            var address = ResourceUri.Parse(reference.Uri);
            var owner = address.Provider == "state" ? address.Segments[1] : address.Segments[0];
            return workspaceId != null && owner == workspaceId ? new ResourceAuthorityScopeId("workspace", workspaceId) : null;
        }

        internal string ReadRetainedPatchSource(ResourceRef exact, int maximum)
        { return ReadRetainedPatchSource(exact, maximum, null); }
        internal System.Collections.Generic.IReadOnlyList<RNAssistant.Core.Tools.TextPatchEdit> ReadRetainedPatchFragments(ResourceRef exact, ResourceRef after, string patch)
        { return ReadRetainedPatchFragments(exact, after, patch, null); }
        internal string ReadRetainedPatchSource(ResourceRef exact, int maximum, string workspaceId)
        { return ResourceUri.Parse(exact.Uri).Provider == "catalog"
            ? _toolExecutor.ResourceGateway.ReadRetainedCatalogText(exact, maximum)
            : PatchChangesProjection.ReadSource(_toolExecutor.ResourceGateway.Authority, exact, maximum, PatchOwnerScope(exact, workspaceId)); }
        internal System.Collections.Generic.IReadOnlyList<RNAssistant.Core.Tools.TextPatchEdit> ReadRetainedPatchFragments(ResourceRef exact, ResourceRef after, string patch, string workspaceId)
        {
            if (ResourceUri.Parse(exact.Uri).Provider != "catalog")
                return PatchChangesProjection.ReadFragments(_toolExecutor.ResourceGateway.Authority, exact, after, patch, PatchOwnerScope(exact, workspaceId));
            var beforeText = _toolExecutor.ResourceGateway.ReadRetainedCatalogText(exact, 512000);
            var afterText = _toolExecutor.ResourceGateway.ReadRetainedCatalogText(after, 512000);
            using (var beforeReader = new System.IO.StringReader(beforeText))
            using (var afterReader = new System.IO.StringReader(afterText))
                return RNAssistant.Core.Tools.TextPatch.ReadAfterFragments(afterReader, RNAssistant.Core.Tools.TextPatch.Parse(patch).Resolve(beforeReader));
        }

        public RunChangesDto ReadRunChanges(RunChangesRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.RunId))
                throw new ArgumentException("An exact run is required.");
            var session = LoadArtifactViewerSession(request.ChatId);
            return new RunChangesService(
                artifact => RunChangesService.ReadRetainedSource(session, artifact,
                    _chatStore.LoadArtifactBody, _toolExecutor.ResourceGateway),
                query => _vbaJournalStore.QueryMutations(session.Host, session.DocumentKey, query),
                id => _vbaJournalStore.GetMutationDetail(session.Host, session.DocumentKey, id,
                    RunChangesService.MaximumSourceCharacters), _toolExecutor.ResourceGateway.Authority)
                .Read(session, request.RunId, request.ToolCallId);
        }

        public ToolResultPresentationDto ReadToolResultPresentation(ToolResultPresentationRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var session = LoadArtifactViewerSession(request.ChatId);
            var changes = new RunChangesService(
                artifact => RunChangesService.ReadRetainedSource(session, artifact, _chatStore.LoadArtifactBody, _toolExecutor.ResourceGateway),
                query => _vbaJournalStore.QueryMutations(session.Host, session.DocumentKey, query),
                id => _vbaJournalStore.GetMutationDetail(session.Host, session.DocumentKey, id, RunChangesService.MaximumSourceCharacters),
                _toolExecutor.ResourceGateway.Authority);
            return new ToolResultPresentationService(call => changes.Read(session, request.RunId, call), _toolExecutor.Payloads)
                .Read(session, request.RunId, request.ToolCallId);
        }

        public ArtifactViewerPageDto ReadArtifactViewerPage(
            string chatId,
            string resourceUri,
            string cursor, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(chatId))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: an explicit chat is required for artifact text reads.");
            return _artifactViewer.ReadPage(LoadAddressedSession(chatId), resourceUri, cursor, _resourceData, cancellationToken);
        }

        public ArtifactImageViewerDto ReadArtifactImage(string chatId, string resourceUri, CancellationToken cancellationToken = default(CancellationToken))
        {
            var data = OpenArtifactView(chatId, resourceUri, "image", cancellationToken: cancellationToken);
            return new ArtifactImageViewerDto { ResourceUri = resourceUri, ViewerKind = "image",
                Title = data.Descriptor.Title, MimeType = data.Binary.Payload.ContentType,
                ContentSha256 = data.Descriptor.Metadata["sourceContentSha256"],
                ByteLength = data.Binary.Payload.ByteLength, Data = data };
        }

        public ArtifactImageThumbnailDto ReadArtifactImageThumbnail(string chatId, string resourceUri, CancellationToken cancellationToken = default(CancellationToken))
        {
            var data = OpenArtifactView(chatId, resourceUri, "thumbnail", cancellationToken: cancellationToken);
            return new ArtifactImageThumbnailDto { ResourceUri = resourceUri, ViewerKind = "image",
                ContentSha256 = data.Descriptor.Metadata["sourceContentSha256"],
                Width = data.Binary.Width, Height = data.Binary.Height,
                ImageMimeType = data.Binary.Payload.ContentType, ImageContentSha256 = data.Binary.Payload.Sha256,
                ImageByteLength = data.Binary.Payload.ByteLength, Data = data };
        }

        public ArtifactPdfViewerDto ReadArtifactPdfInfo(string chatId, string resourceUri)
        {
            return _artifactViewer.ReadPdfInfo(LoadArtifactViewerSession(chatId), resourceUri);
        }

        public ArtifactPdfPageDto ReadArtifactPdfPage(string chatId, string resourceUri, int pageIndex, CancellationToken cancellationToken = default(CancellationToken))
        {
            return OpenArtifactPage(chatId, resourceUri, pageIndex, "render-page", cancellationToken);
        }

        public ArtifactPdfPageDto ReadArtifactPdfThumbnail(string chatId, string resourceUri, int pageIndex, CancellationToken cancellationToken = default(CancellationToken))
        {
            return OpenArtifactPage(chatId, resourceUri, pageIndex, "page-thumbnail", cancellationToken);
        }

        private ResourceDataOpenResponse OpenArtifactView(string chatId, string resourceUri, string view, string path = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var session = LoadArtifactViewerSession(chatId);
            var artifact = _toolExecutor.ResourceGateway.ResolveArtifact(session, resourceUri);
            return _resourceData.Open(session, "viewer", ChatResourceUri.CreateArtifactRevision(session, artifact), view, path, cancellationToken);
        }

        private ArtifactPdfPageDto OpenArtifactPage(string chatId, string resourceUri, int pageIndex, string view, CancellationToken cancellationToken)
        {
            var data = OpenArtifactView(chatId, resourceUri, view, pageIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
            return new ArtifactPdfPageDto { ResourceUri = resourceUri, ViewerKind = "pdf",
                ContentSha256 = data.Descriptor.Metadata["sourceContentSha256"],
                PageIndex = data.Binary.PageIndex.Value, PageCount = data.Binary.PageCount.Value,
                Width = data.Binary.Width, Height = data.Binary.Height,
                ImageMimeType = data.Binary.Payload.ContentType, ImageContentSha256 = data.Binary.Payload.Sha256,
                ImageByteLength = data.Binary.Payload.ByteLength, Data = data };
        }

        private ChatSession LoadArtifactViewerSession(string chatId)
        {
            if (string.IsNullOrWhiteSpace(chatId))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: an explicit chat is required for artifact viewing.");
            return LoadAddressedSession(chatId);
        }
    }
}
