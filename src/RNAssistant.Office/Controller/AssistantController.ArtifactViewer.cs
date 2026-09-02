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
    }
}
