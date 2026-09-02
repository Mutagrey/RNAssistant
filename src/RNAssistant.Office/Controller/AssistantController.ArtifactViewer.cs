using RNAssistant.Office.Contracts;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        public ArtifactViewerPageDto ReadArtifactViewerPage(
            string chatId,
            string resourceUri,
            string cursor)
        {
            return _artifactViewer.ReadPage(LoadSession(chatId), resourceUri, cursor);
        }

        public ArtifactImageViewerDto ReadArtifactImage(string chatId, string resourceUri)
        {
            return _artifactViewer.ReadImage(LoadSession(chatId), resourceUri);
        }

        public ArtifactImageThumbnailDto ReadArtifactImageThumbnail(string chatId, string resourceUri)
        {
            return _artifactViewer.ReadImageThumbnail(LoadSession(chatId), resourceUri);
        }

        public ArtifactPdfViewerDto ReadArtifactPdfInfo(string chatId, string resourceUri)
        {
            return _artifactViewer.ReadPdfInfo(LoadSession(chatId), resourceUri);
        }

        public ArtifactPdfPageDto ReadArtifactPdfPage(string chatId, string resourceUri, int pageIndex)
        {
            return _artifactViewer.ReadPdfPage(LoadSession(chatId), resourceUri, pageIndex);
        }

        public ArtifactPdfPageDto ReadArtifactPdfThumbnail(string chatId, string resourceUri, int pageIndex)
        {
            return _artifactViewer.ReadPdfThumbnail(LoadSession(chatId), resourceUri, pageIndex);
        }
    }
}
