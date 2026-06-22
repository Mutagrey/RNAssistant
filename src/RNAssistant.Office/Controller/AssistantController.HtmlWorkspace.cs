using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        public HtmlWorkspaceResponse GetHtmlWorkspace(string chatId = null)
        {
            var session = LoadSession(chatId);
            return HtmlWorkspaceState(session);
        }

        public HtmlWorkspaceResponse SaveHtmlWorkspaceFile(string chatId, string path, string kind, string content, bool setActive)
        {
            var session = LoadSession(chatId);
            HtmlArtifactToolExecutor.UpsertFile(session, path, kind, content, setActive);
            _chatStore.Save(session);
            return HtmlWorkspaceState(session);
        }

        public HtmlWorkspaceResponse SaveHtmlWorkspaceData(string chatId, string name, string json)
        {
            var session = LoadSession(chatId);
            HtmlArtifactToolExecutor.UpsertDataSource(session, name, json);
            _chatStore.Save(session);
            return HtmlWorkspaceState(session);
        }

        public HtmlWorkspaceResponse SetActiveHtmlWorkspaceFile(string chatId, string path)
        {
            var session = LoadSession(chatId);
            HtmlArtifactToolExecutor.SetActiveFile(session, path);
            _chatStore.Save(session);
            return HtmlWorkspaceState(session);
        }

        private static HtmlWorkspaceResponse HtmlWorkspaceState(ChatSession session)
        {
            return new HtmlWorkspaceResponse
            {
                ActiveChatId = ChatStore.GetSessionId(session),
                Workspace = session == null ? new HtmlWorkspace() : HtmlArtifactToolExecutor.NormalizeWorkspace(session.HtmlWorkspace)
            };
        }
    }
}
