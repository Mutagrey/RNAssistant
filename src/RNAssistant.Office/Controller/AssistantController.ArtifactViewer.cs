using RNAssistant.Office.Contracts;
using RNAssistant.Core.Services;
using RNAssistant.Office.Services;
using System.Threading;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        public ArtifactViewerPageDto ReadArtifactViewerPage(
            string chatId,
            string resourceUri,
            string cursor, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
        {
            return _artifactViewer.ReadPage(LoadSession(chatId), resourceUri, cursor, _resourceData, cancellationToken);
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
            return _artifactViewer.ReadPdfInfo(LoadSession(chatId), resourceUri);
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
            var session = LoadSession(chatId);
            var artifact = ArtifactViewerService.ResolveExactArtifact(session, resourceUri);
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
    }
}
