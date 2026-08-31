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
    }
}
