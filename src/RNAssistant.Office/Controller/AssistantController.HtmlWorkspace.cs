using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Tools;
using System.Threading;
using System.Threading.Tasks;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        public Task<HtmlFetchResponse> HtmlFetchAsync(HtmlFetchRequest request, CancellationToken cancellationToken)
        {
            return _htmlNetwork.FetchAsync(request, cancellationToken);
        }

        public object AllowHtmlNetworkOrigin(string origin)
        {
            return new { origin = _htmlNetwork.AllowOrigin(origin), allowed = true };
        }

        public HtmlWorkspaceResponse GetHtmlWorkspace(string chatId = null)
        {
            var session = LoadSession(chatId);
            return HtmlWorkspaceState(session);
        }

        public HtmlWorkspaceResponse SaveHtmlWorkspaceFile(string chatId, string path, string kind, string content, bool setActive)
        {
            var session = LoadSession(chatId);
            HtmlArtifactToolExecutor.UpsertFile(session, path, kind, content, setActive);
            SaveSessionChanges(session);
            return HtmlWorkspaceState(session);
        }

        public HtmlWorkspaceResponse SaveHtmlWorkspaceData(string chatId, string name, string json)
        {
            var session = LoadSession(chatId);
            HtmlArtifactToolExecutor.UpsertDataSource(session, name, json);
            SaveSessionChanges(session);
            return HtmlWorkspaceState(session);
        }

        public HtmlWorkspaceResponse DeleteHtmlWorkspaceFile(string chatId, string path)
        {
            var session = LoadSession(chatId);
            HtmlArtifactToolExecutor.DeleteFile(session, path);
            SaveSessionChanges(session);
            return HtmlWorkspaceState(session);
        }

        public HtmlWorkspaceResponse DeleteHtmlWorkspaceData(string chatId, string name)
        {
            var session = LoadSession(chatId);
            HtmlArtifactToolExecutor.DeleteDataSource(session, name);
            SaveSessionChanges(session);
            return HtmlWorkspaceState(session);
        }

        public HtmlWorkspaceResponse SetActiveHtmlWorkspaceFile(string chatId, string path)
        {
            var session = LoadSession(chatId);
            HtmlArtifactToolExecutor.SetActiveFile(session, path);
            SaveSessionChanges(session);
            return HtmlWorkspaceState(session);
        }

        public HtmlWorkspaceResponse RestoreHtmlWorkspaceSnapshot(string chatId, string snapshotId)
        {
            var session = LoadSession(chatId);
            HtmlArtifactToolExecutor.RestoreSnapshot(session, snapshotId);
            SaveSessionChanges(session);
            return HtmlWorkspaceState(session);
        }

        public HtmlWorkspaceResponse RedoHtmlWorkspaceSnapshot(string chatId, string snapshotId)
        {
            var session = LoadSession(chatId);
            HtmlArtifactToolExecutor.RedoSnapshot(session, snapshotId);
            SaveSessionChanges(session);
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
